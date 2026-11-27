using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Billing;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// 683 (the whole-code audit of 2026-11-26), the money findings in the website setup fee paid at a switch (678):
// PAY-1 a second charge after a pending, lost or half-finished payment; PAY-2 the fee skipped through a checkout still
// waiting for approval; PAY-3 the amount owed wiped or left stale by a later checkout; PAY-5 its invoice taken for a
// payment of the subscription. PayPal is a stand-in whose answers each test scripts.
public class MoneyFixes683Tests
{
    // ---- PAY-1: pending ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_payment_PayPal_is_still_processing_stays_in_progress_no_second_payment_starts_and_it_is_finished_when_PayPal_has_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await OwesFeeAsync(db);
        var paypal = new ScriptedPayPal();
        var billing = Billing(db, paypal);
        var order = await BeginAsync(billing, agentId);

        paypal.CaptureAnswers.Enqueue(ScriptedPayPal.Pending);
        Assert.Equal(WebsiteSetupFeeOutcome.Pending, await billing.CompleteAsync(agentId, order));

        var quote = await billing.QuoteAsync(agentId);
        Assert.Equal(order, quote!.OrderInProgress);                       // the page offers "Check the payment", not "Pay"
        var again = await billing.BeginAsync(agentId, "https://app.example.test/portal/Website/WebsiteSetupFeeReturn", "https://app.example.test/portal/Website/WebsiteSetupFeeCancel");
        Assert.False(again.Success);                                       // no second payment
        Assert.Contains("already being confirmed", again.Message);
        Assert.Equal(1, paypal.OrdersCreated);
        await billing.CancelAsync(agentId, order);                         // a cancel link opened meanwhile changes nothing
        Assert.Equal(order, (await billing.QuoteAsync(agentId))!.OrderInProgress);

        // Still pending at PayPal: nothing changes.
        paypal.OrderStatus = ScriptedPayPal.Pending;
        Assert.Equal(WebsiteSetupFeeOutcome.Pending, await billing.CompleteAsync(agentId, order));
        Assert.Empty(await Invoices(db, agentId));

        // PayPal completes it; their next look settles it, without asking for the money again.
        paypal.OrderStatus = ScriptedPayPal.Completed("CAP-PENDING-1");
        Assert.Equal(WebsiteSetupFeeOutcome.Paid, await billing.CompleteAsync(agentId, order));
        var invoice = Assert.Single(await Invoices(db, agentId));
        Assert.True(invoice.IsPaid);
        Assert.Equal(226m, invoice.Total);
        Assert.Equal("CAP-PENDING-1", invoice.PayPalTransactionId);       // the capture, not the order (PAY-5)
        Assert.Equal(1, paypal.Captures);
        Assert.Null(await billing.QuoteAsync(agentId));
        Assert.Equal(WebsiteSetupFeeOutcome.NothingOwed, await billing.CompleteAsync(agentId, order));
        Assert.Single(await Invoices(db, agentId));
    }

    // ---- PAY-1: the answer to the capture is lost -------------------------------------------------------------

    [Fact]
    public async Task A_capture_whose_answer_is_lost_is_never_called_not_charged_PayPal_is_asked_what_happened()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await OwesFeeAsync(db);
        var paypal = new ScriptedPayPal();
        var billing = Billing(db, paypal);
        var order = await BeginAsync(billing, agentId);

        // The money was taken, the answer never arrived, and PayPal cannot be asked either.
        paypal.CaptureAnswers.Enqueue(ScriptedPayPal.NoAnswer);
        paypal.OrderStatus = ScriptedPayPal.NoAnswer;
        Assert.Equal(WebsiteSetupFeeOutcome.Unfinished, await billing.CompleteAsync(agentId, order));   // not "Refused"
        Assert.Equal(order, (await billing.QuoteAsync(agentId))!.OrderInProgress);
        Assert.False((await billing.BeginAsync(agentId, "https://x.test/r", "https://x.test/c")).Success);
        Assert.Empty(await Invoices(db, agentId));

        // PayPal answers again: it has the money.
        paypal.OrderStatus = ScriptedPayPal.Completed("CAP-LOST-1");
        Assert.Equal(WebsiteSetupFeeOutcome.Paid, await billing.CompleteAsync(agentId, order));
        Assert.Equal("CAP-LOST-1", Assert.Single(await Invoices(db, agentId)).PayPalTransactionId);
        Assert.Equal(1, paypal.Captures);                                  // asked for the money once
    }

    [Fact]
    public async Task A_capture_that_never_reached_PayPal_is_made_at_the_next_look_and_the_same_key_goes_with_every_try()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await OwesFeeAsync(db);
        var paypal = new ScriptedPayPal();
        var billing = Billing(db, paypal);
        var order = await BeginAsync(billing, agentId);

        paypal.CaptureAnswers.Enqueue(ScriptedPayPal.NoAnswer);
        paypal.OrderStatus = "APPROVED";                                   // approved by them, nothing taken
        Assert.Equal(WebsiteSetupFeeOutcome.Unfinished, await billing.CompleteAsync(agentId, order));
        Assert.Null((await billing.QuoteAsync(agentId))!.OrderInProgress); // back where their return began

        paypal.CaptureAnswers.Enqueue(ScriptedPayPal.Completed("CAP-RETRY-1"));
        Assert.Equal(WebsiteSetupFeeOutcome.Paid, await billing.CompleteAsync(agentId, order));
        Assert.Single(await Invoices(db, agentId));
        Assert.Equal(2, paypal.Captures);
        Assert.Equal(new[] { "website-setup-fee-" + order }, paypal.RequestIds.Distinct().ToArray());   // PayPal takes it once
    }

    [Fact]
    public async Task A_payment_PayPal_refuses_is_called_not_charged_and_may_be_tried_again()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await OwesFeeAsync(db);
        var paypal = new ScriptedPayPal();
        var billing = Billing(db, paypal);
        var order = await BeginAsync(billing, agentId);

        paypal.CaptureAnswers.Enqueue(ScriptedPayPal.Declined);
        Assert.Equal(WebsiteSetupFeeOutcome.Refused, await billing.CompleteAsync(agentId, order));
        Assert.Null((await billing.QuoteAsync(agentId))!.OrderInProgress);
        Assert.Empty(await Invoices(db, agentId));
        Assert.True((await billing.BeginAsync(agentId, "https://x.test/r", "https://x.test/c")).Success);
    }

    // ---- PAY-1: paid, and the invoice still to write (a failure or a restart right after the capture) ---------

    [Fact]
    public async Task A_paid_payment_left_without_its_invoice_is_invoiced_once_however_many_returns_arrive()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await OwesFeeAsync(db);
        // As a failure after the capture leaves it: PayPal has the money, nothing is invoiced.
        await db.AgentOwnWebsites.Where(r => r.AgentUserId == agentId).ExecuteUpdateAsync(u => u
            .SetProperty(r => r.SetupFeeOrderId, "ORDER-PAID|paid|CAP-PAID-1").SetProperty(r => r.SetupFeeOrderTotal, 226m));

        Assert.Equal("ORDER-PAID", (await Billing(db, new ScriptedPayPal()).QuoteAsync(agentId))!.OrderInProgress);

        // Two returns at once, each on its own connection.
        await using var db2 = testDb.CreateContext();
        var paypal = new ScriptedPayPal();
        var outcomes = await Task.WhenAll(
            Billing(db, paypal).CompleteAsync(agentId, "ORDER-PAID"),
            Billing(db2, paypal).CompleteAsync(agentId, "ORDER-PAID"));

        Assert.Single(outcomes, o => o == WebsiteSetupFeeOutcome.Paid);
        Assert.Single(outcomes, o => o == WebsiteSetupFeeOutcome.NothingOwed);
        db.ChangeTracker.Clear();
        var invoice = Assert.Single(await Invoices(db, agentId));
        Assert.Equal("CAP-PAID-1", invoice.PayPalTransactionId);
        Assert.Equal(226m, invoice.Total);
        Assert.Equal(0, paypal.Captures);                                  // PayPal was not asked for money again
        Assert.Null(await Billing(db, paypal).QuoteAsync(agentId));
    }

    // ---- PAY-2 -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_checkout_priced_without_the_fee_and_still_waiting_for_approval_is_cancelled_at_a_free_switch()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var billing = Billing(db, new ScriptedPayPal());

        // In their trial: a checkout started while keeping their own website, not approved yet.
        var waiting = await OwesFeeAsync(db, BillingStatus.Pending);
        Assert.Null(await billing.QuoteAsync(waiting));                    // nothing to ask for: no subscription runs yet
        Assert.True(await billing.DropPendingCheckoutAsync(waiting));
        db.ChangeTracker.Clear();
        Assert.Equal(BillingStatus.Cancelled, (await db.Billings.AsNoTracking().SingleAsync(b => b.AgentUserId == waiting)).Status);
        Assert.False(await billing.DropPendingCheckoutAsync(waiting));     // nothing left to cancel

        // A running subscription is never touched: the fee is asked for instead.
        var running = await OwesFeeAsync(db, BillingStatus.Active);
        Assert.False(await billing.DropPendingCheckoutAsync(running));
        Assert.NotNull(await billing.QuoteAsync(running));

        // Nothing was left out: a waiting checkout stays as it is.
        var paidFee = await OwesFeeAsync(db, BillingStatus.Pending, waived: null);
        Assert.False(await billing.DropPendingCheckoutAsync(paidFee));
        db.ChangeTracker.Clear();
        Assert.Equal(BillingStatus.Pending, (await db.Billings.AsNoTracking().SingleAsync(b => b.AgentUserId == paidFee)).Status);
    }

    // ---- PAY-3 -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task What_a_checkout_leaves_out_replaces_an_older_amount_nothing_included_and_a_scheduled_change_leaves_it_alone()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await OwesFeeAsync(db);                               // 200 left out by an earlier checkout
        var billing = Billing(db, new ScriptedPayPal());

        var free = new BillingRule { PackageName = $"F683-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m, SetupFee = 0m };
        db.Add(free);
        await db.SaveChangesAsync();
        await billing.RecordWaivedSetupFeeAsync(agentId, free, null);      // this checkout leaves nothing out
        Assert.Null(await db.AgentOwnWebsites.AsNoTracking().Where(r => r.AgentUserId == agentId).Select(r => r.SetupFeeWaived).SingleAsync());

        var source = Read(@"src\IPRO.Billing\PayPalBillingService.cs");
        // Recorded for every checkout of someone keeping their own website, except one completing a scheduled change.
        Assert.Contains("if (keepsOwnWebsite && !completesScheduledChange) await RecordWaivedSetupFeeAsync(userId, requestedPackage, promo);", source);
        Assert.DoesNotContain("nothingLeftOut", source);
        Assert.DoesNotContain("nothingLeftOut", Read(@"src\IPRO.Billing\PayPalBillingService.WebsiteSetupFee.cs"));
    }

    // ---- PAY-5 -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_renewal_soon_after_the_setup_fee_invoice_gets_an_invoice_of_its_own()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await OwesFeeAsync(db);
        var paypal = new ScriptedPayPal();
        var billing = Billing(db, paypal);
        var order = await BeginAsync(billing, agentId);
        paypal.CaptureAnswers.Enqueue(ScriptedPayPal.Completed("CAP-FEE-1"));
        Assert.Equal(WebsiteSetupFeeOutcome.Paid, await billing.CompleteAsync(agentId, order));
        var subscription = await db.Billings.AsNoTracking().SingleAsync(b => b.AgentUserId == agentId);

        // The month's renewal arrives an hour later, for less than the fee's invoice: before, it was taken for a part
        // of that invoice and got none of its own.
        Assert.True(await billing.HandleSubscriptionPaymentCompletedWebhookAsync(subscription.PayPalSubscriptionId!, "SALE-RENEWAL-1", 67.80m));

        db.ChangeTracker.Clear();
        var invoices = await Invoices(db, agentId);
        Assert.Equal(2, invoices.Count);
        var fee = Assert.Single(invoices, i => i.LineItems.Any(l => l.Description == PayPalBillingService.WebsiteSetupFeeLine));
        Assert.Equal("CAP-FEE-1", fee.PayPalTransactionId);                // the renewal's sale was not appended to it
        var renewal = Assert.Single(invoices, i => i.Id != fee.Id);
        Assert.True(renewal.IsPaid);
        Assert.Contains("SALE-RENEWAL-1", renewal.PayPalTransactionId);
        Assert.Equal(67.80m, renewal.Total);
    }

    // ---- the portal's side -------------------------------------------------------------------------------------

    [Fact]
    public void The_portal_checks_a_payment_in_progress_returns_under_portal_and_switches_nothing_from_a_link()
    {
        var controller = Read(@"src\IPRO.Web\Controllers\WebsiteController.cs").Replace("\r\n", "\n");
        // PAY-1: a payment in progress is checked, never paid again.
        Assert.Contains("if (quote?.OrderInProgress != null)\n        {", controller);
        Assert.Contains("return RedirectToAction(nameof(WebsiteSetupFeeReturn), new { token = quote.OrderInProgress });", controller);
        // W08-03: PayPal's return and cancel addresses are under /portal, the one prefix that reaches the portal on every host.
        Assert.Contains("$\"{baseUrl}/portal/Website/WebsiteSetupFeeReturn\", $\"{baseUrl}/portal/Website/WebsiteSetupFeeCancel\"", controller);
        // PAY-2: a waiting checkout is cancelled before a free switch.
        Assert.Contains("var checkoutDropped = setupFee != null && await setupFee.DropPendingCheckoutAsync(AgentId);", controller);

        // PAY-4: the return (a GET) switches only after a payment it has just settled.
        var start = controller.IndexOf("public async Task<IActionResult> WebsiteSetupFeeReturn(string? token)", StringComparison.Ordinal);
        var action = controller[start..controller.IndexOf("public async Task<IActionResult> WebsiteSetupFeeCancel", start, StringComparison.Ordinal)];
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(action, @"SwitchToIproWebsiteAsync\(\)"));
        Assert.Contains("case IPRO.Billing.WebsiteSetupFeeOutcome.Paid:\n                await SwitchToIproWebsiteAsync();", action);
        Assert.Contains("case IPRO.Billing.WebsiteSetupFeeOutcome.Unfinished:", action);
        Assert.Contains("TempData[\"Warning\"] = \"Your payment is still being confirmed with PayPal, so your website has not switched yet. Nothing more will be charged.", action);   // never "nothing was charged"

        var page = Read(@"src\IPRO.Web\Views\Website\OwnWebsite.cshtml");
        Assert.Contains("@if (Model.SetupFee?.OrderInProgress != null)", page);
        Assert.Contains("Check the payment", page);
    }

    [Fact]
    public async Task My_Website_offers_Check_the_payment_instead_of_Pay_while_a_payment_is_in_progress()
    {
        var model = new IPRO.Web.Models.OwnWebsitePortalViewModel
        {
            FormUrl = "https://app.example.test/PublicWebsite/LeadForm/k683", EmbedCode = "<iframe></iframe>", TemporaryAddress = "m683.247advisers.com",
            SetupFee = new WebsiteSetupFeeQuote(200m, 26m, 0.13m, "ON 13% HST", "ORDER-683-1")
        };
        var inProgress = await DomainGuideScreens595Tests.RenderWebAsync("~/Views/Website/OwnWebsite.cshtml", model, new Dictionary<string, object?>());
        Assert.Contains("Check the payment", inProgress);
        Assert.Contains("href=\"/portal/Website/WebsiteSetupFeeReturn?token=ORDER-683-1\"", inProgress);
        Assert.Contains("Nothing more will be charged.", inProgress);
        Assert.DoesNotContain("Pay the Setup Fee", inProgress);
        Assert.DoesNotContain("action=\"/portal/Website/UseIproWebsite\"", inProgress);   // no second payment can be started from the page

        model.SetupFee = new WebsiteSetupFeeQuote(200m, 26m, 0.13m, "ON 13% HST");
        var owed = await DomainGuideScreens595Tests.RenderWebAsync("~/Views/Website/OwnWebsite.cshtml", model, new Dictionary<string, object?>());
        Assert.Contains("Pay the Setup Fee and Use an iPro Website", owed);
        Assert.DoesNotContain("Check the payment", owed);

        model.SetupFee = null;
        var free = await DomainGuideScreens595Tests.RenderWebAsync("~/Views/Website/OwnWebsite.cshtml", model, new Dictionary<string, object?>());
        Assert.Contains("Use an iPro Website", free);
        Assert.DoesNotContain("Pay the Setup Fee", free);
    }

    // ---- harness ----------------------------------------------------------------------------------------------

    // An adviser keeping their own website, on a package with a 200 setup fee their checkout left out.
    private static async Task<int> OwesFeeAsync(IPRODbContext db, BillingStatus status = BillingStatus.Active, decimal? waived = 200m)
    {
        if (!await db.ProvinceTaxRates.AnyAsync(t => t.ProvinceCode == "ON"))
            db.Add(new ProvinceTaxRate { ProvinceCode = "ON", ProvinceName = "Ontario", Rate = 0.13m, TaxLabel = "ON 13% HST", IsActive = true });
        var package = new BillingRule { PackageName = $"P683-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m, SetupFee = 200m };
        db.Add(package);
        var agent = new AgentUser
        {
            UserName = $"m683-{Guid.NewGuid():N}"[..20],
            Email = $"m683-{Guid.NewGuid():N}"[..14] + "@example.test",
            FirstName = "Money", LastName = "Fix", CompanyName = "Money Fix Ltd",
            DomainName = $"m683-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario",
            TrialEndsAt = DateTime.UtcNow.AddDays(10)
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        db.Add(new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = package.Id, Amount = 60m, Status = status,
            StartDate = DateTime.UtcNow.AddDays(-5), PayPalSubscriptionId = $"I-{Guid.NewGuid():N}"[..14]
        });
        await db.SaveChangesAsync();
        await OwnWebsitePreference.ChooseAsync(db, agent.Id, null);
        await db.AgentOwnWebsites.Where(r => r.AgentUserId == agent.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.SetupFeeWaived, waived));
        db.ChangeTracker.Clear();
        return agent.Id;
    }

    private static async Task<string> BeginAsync(PayPalBillingService billing, int agentId)
    {
        var started = await billing.BeginAsync(agentId, "https://app.example.test/portal/Website/WebsiteSetupFeeReturn", "https://app.example.test/portal/Website/WebsiteSetupFeeCancel");
        Assert.True(started.Success, started.Message);
        return started.ApprovalUrl[(started.ApprovalUrl.LastIndexOf('/') + 1)..];
    }

    private static Task<List<Invoice>> Invoices(IPRODbContext db, int agentId) =>
        db.Invoices.AsNoTracking().Include(i => i.LineItems).Where(i => i.AgentUserId == agentId).OrderBy(i => i.Id).ToListAsync();

    private static PayPalBillingService Billing(IPRODbContext db, ScriptedPayPal paypal) =>
        new(new UnitOfWork(db), db, paypal, new TestEmailRecorder(),
            Options.Create(new PayPalSettings { ClientId = "client-683", ClientSecret = "secret-683" }),
            new ConfigurationBuilder().Build(), NullLogger<PayPalBillingService>.Instance);

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }

    // PayPal's order API with scripted answers: each capture takes the next answer in CaptureAnswers (COMPLETED when
    // none is queued); a look at the order answers OrderStatus.
    private sealed class ScriptedPayPal : IHttpClientFactory
    {
        public const string Pending = "PENDING";
        public const string NoAnswer = "NO-ANSWER";
        public const string Declined = "DECLINED";
        public static string Completed(string captureId) => "COMPLETED:" + captureId;

        public readonly Queue<string> CaptureAnswers = new();
        public string OrderStatus = "APPROVED";
        public int OrdersCreated;
        public int Captures;
        public readonly List<string> RequestIds = new();

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler : HttpMessageHandler
        {
            private readonly ScriptedPayPal _owner;
            public Handler(ScriptedPayPal owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var url = request.RequestUri!.AbsoluteUri;
                if (url.Contains("/v1/oauth2/token")) return Json("{\"access_token\":\"token-683\",\"expires_in\":32400}");
                if (request.Method == HttpMethod.Post && url.EndsWith("/v2/checkout/orders"))
                {
                    var id = "ORDER-683-" + Interlocked.Increment(ref _owner.OrdersCreated);
                    return Json("{\"id\":\"" + id + "\",\"links\":[{\"rel\":\"approve\",\"href\":\"https://paypal.example.test/approve/" + id + "\"}]}");
                }
                if (request.Method == HttpMethod.Post && url.EndsWith("/capture"))
                {
                    Interlocked.Increment(ref _owner.Captures);
                    lock (_owner.RequestIds)
                        _owner.RequestIds.Add(request.Headers.TryGetValues("PayPal-Request-Id", out var ids) ? ids.First() : "");
                    string answer;
                    lock (_owner.CaptureAnswers) answer = _owner.CaptureAnswers.Count > 0 ? _owner.CaptureAnswers.Dequeue() : Completed("CAP-DEFAULT");
                    if (answer == NoAnswer) throw new HttpRequestException("the connection was lost");
                    if (answer == Declined)
                        return Task.FromResult(new HttpResponseMessage((HttpStatusCode)422) { Content = new StringContent("{\"name\":\"UNPROCESSABLE_ENTITY\",\"details\":[{\"issue\":\"INSTRUMENT_DECLINED\"}]}", Encoding.UTF8, "application/json") });
                    return Json(OrderJson(answer));
                }
                if (request.Method == HttpMethod.Get && url.Contains("/v2/checkout/orders/"))
                {
                    if (_owner.OrderStatus == NoAnswer) throw new HttpRequestException("the connection was lost");
                    return Json(OrderJson(_owner.OrderStatus));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            // An order as PayPal returns it: with a capture once money was asked for, without one before.
            private static string OrderJson(string status)
            {
                if (status.StartsWith("COMPLETED:", StringComparison.Ordinal))
                    return "{\"status\":\"COMPLETED\",\"purchase_units\":[{\"payments\":{\"captures\":[{\"id\":\"" + status[10..] + "\",\"status\":\"COMPLETED\"}]}}]}";
                if (status == Pending)
                    return "{\"status\":\"COMPLETED\",\"purchase_units\":[{\"payments\":{\"captures\":[{\"id\":\"CAP-WAITING\",\"status\":\"PENDING\"}]}}]}";
                return "{\"status\":\"" + status + "\",\"purchase_units\":[{\"reference_id\":\"x\"}]}";
            }

            private static Task<HttpResponseMessage> Json(string body) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
