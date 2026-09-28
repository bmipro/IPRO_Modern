using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using IPRO.Web.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// 527 (2026-09-27), slice 1: advisers collect payment through their own processor, Stripe first --
// the tables, the Payments page with its connect cards, the Stripe connection (the processor's own
// sign-in and consent, no secret stored: the platform key acts for the connected account), the
// disconnect, and the webhook receiver that settles an invoice once, whatever Stripe resends.
// The signature check and the authorization URL are pinned without a network; the connection,
// the callback, the disconnect and the webhook run against MySQL with a stub for Stripe itself.
public class StripeConnect527Tests
{
    private const string WebhookSecret = "whsec_test_527";

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:BaseUrl"] = "https://app.example.test",
            ["App:TemporarySiteRootDomain"] = "247advisers.test"
        })
        .Build();

    [Fact]
    public void The_stripe_connection_is_wired()
    {
        Assert.Contains("interface IStripeConnectService", Read(@"src\IPRO.Utility\StripeConnectService.cs"));
        Assert.Contains("class StripeWebhookSignature", Read(@"src\IPRO.Utility\StripeConnectService.cs"));
        Assert.Contains("class AgentPaymentConnection", Read(@"src\IPRO.Entities\AgentPaymentConnection.cs"));
        Assert.Contains("class ClientInvoicePayment", Read(@"src\IPRO.Entities\ClientInvoicePayment.cs"));

        var controller = Read(@"src\IPRO.Web\Controllers\PaymentsController.cs");
        Assert.Contains("public async Task<IActionResult> StripeCallback(", controller);
        Assert.Contains("public async Task<IActionResult> StripeDisconnect(", controller);
        Assert.Contains("[HttpGet(\"/Payments/StripeCallback\")]", controller);
        Assert.Contains("[HttpPost(\"/payments/stripe/webhook\")]", Read(@"src\IPRO.Web\Controllers\PaymentsWebhookController.cs"));
        Assert.Contains("StripeRedirectUri", Read(@"src\IPRO.Web\Infrastructure\PortalUrlHelper.cs"));

        Assert.Contains("CREATE TABLE IF NOT EXISTS `AgentPaymentConnections`", Read(@"src\IPRO.DataAccess\StartupSchemaRepair.cs"));
        Assert.Contains("CREATE TABLE IF NOT EXISTS `ClientInvoicePayments`", Read(@"src\IPRO.DataAccess\StartupSchemaRepair.cs"));
        Assert.Contains("EnsurePaymentConnectionSchemaAsync", Read(@"src\IPRO.Web\Program.cs"));
        Assert.Contains("EnsurePaymentConnectionSchemaAsync", Read(@"src\IPRO.Admin\Program.cs"));
        Assert.Contains("IStripeConnectService, StripeConnectService", Read(@"src\IPRO.Web\Program.cs"));
        var eraser = Read(@"src\IPRO.DataAccess\AgentDataEraser.cs");
        Assert.Contains("(\"AgentPaymentConnections\"", eraser);
        Assert.Contains("(\"ClientInvoicePayments\"", eraser);

        // The page (rewritten 2026-09-28): a card per method by link or code; the Stripe Connect card stays, dormant.
        var view = Read(@"src\IPRO.Web\Views\Payments\Index.cshtml");
        Assert.Contains("/portal/Payments/StripeConnect", view);
        Assert.Contains("/portal/Payments/StripeDisconnect", view);
        Assert.Contains("js-confirm-submit", view);
        Assert.Contains("asp-for=\"Form.PayPal\"", view);
        Assert.Contains("asp-for=\"Form.SquareLink\"", view);
        Assert.Contains("/portal/Payments", Read(@"src\IPRO.Web\Views\ClientInvoices\Index.cshtml"));
        Assert.Contains("\"Stripe\"", Read(@"src\IPRO.Web\appsettings.json"));
    }

    // -- the signature, without a clock or a network ------------------------------------------

    [Fact]
    public void The_webhook_signature_round_trips_and_refuses_the_wrong_secret_a_stale_stamp_and_junk()
    {
        const string payload = "{\"id\":\"evt_1\",\"type\":\"checkout.session.completed\"}";
        const long now = 1_790_000_000;
        var header = StripeWebhookSignature.Header(payload, WebhookSecret, now);

        Assert.StartsWith($"t={now},v1=", header);
        Assert.True(StripeWebhookSignature.IsValid(payload, header, WebhookSecret, now));
        Assert.True(StripeWebhookSignature.IsValid(payload, header, WebhookSecret, now + StripeWebhookSignature.ToleranceSeconds));
        // Stripe lists an older key's signature first during a secret roll; any one match is enough.
        Assert.True(StripeWebhookSignature.IsValid(payload, header.Replace("v1=", "v1=0000,v1="), WebhookSecret, now));

        Assert.False(StripeWebhookSignature.IsValid(payload, header, "whsec_other", now));
        Assert.False(StripeWebhookSignature.IsValid(payload + " ", header, WebhookSecret, now));
        Assert.False(StripeWebhookSignature.IsValid(payload, header, WebhookSecret, now + StripeWebhookSignature.ToleranceSeconds + 1));
        Assert.False(StripeWebhookSignature.IsValid(payload, header, WebhookSecret, now - StripeWebhookSignature.ToleranceSeconds - 1));
        Assert.False(StripeWebhookSignature.IsValid(payload, "junk", WebhookSecret, now));
        Assert.False(StripeWebhookSignature.IsValid(payload, $"t={now}", WebhookSecret, now));
        Assert.False(StripeWebhookSignature.IsValid(payload, "v1=abcd", WebhookSecret, now));
        Assert.False(StripeWebhookSignature.IsValid(payload, null, WebhookSecret, now));
        Assert.False(StripeWebhookSignature.IsValid(payload, header, "", now));
    }

    [Fact]
    public void The_authorization_url_names_the_client_the_registered_redirect_and_the_state()
    {
        var url = StripeConnectService.AuthorizationUrl("ca_123", "https://app.iproadvisers.com/Payments/StripeCallback", "7|638000");
        Assert.StartsWith("https://connect.stripe.com/oauth/authorize?", url);
        var query = HttpUtility.ParseQueryString(new Uri(url).Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("ca_123", query["client_id"]);
        Assert.Equal("read_write", query["scope"]);
        Assert.Equal("https://app.iproadvisers.com/Payments/StripeCallback", query["redirect_uri"]);
        Assert.Equal("7|638000", query["state"]);

        Assert.Equal("https://app.example.test/Payments/StripeCallback", IPRO.Web.Infrastructure.PortalUrlHelper.StripeRedirectUri(Config()));
        Assert.True(new StripeSettings { SecretKey = "sk_test_x", ClientId = "ca_x" }.IsConfigured);
        Assert.False(new StripeSettings { SecretKey = "sk_test_x" }.IsConfigured);
        Assert.True(new StripeSettings { SecretKey = "sk_live_x", ClientId = "ca_x" }.IsLive);
        Assert.False(new StripeSettings { SecretKey = "sk_test_x", ClientId = "ca_x" }.IsLive);
    }

    // -- the connection, against MySQL -----------------------------------------------------------

    [Fact]
    public async Task Connect_sends_the_adviser_to_stripe_the_callback_records_the_account_and_disconnect_closes_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var stripe = new FakeStripe();
        var provider = new EphemeralDataProtectionProvider();

        var controller = NewController(db, stripe, provider, agentId);
        var before = Assert.IsType<ViewResult>(await controller.Index());
        var beforeModel = Assert.IsType<PaymentsViewModel>(before.Model);
        Assert.True(beforeModel.StripeConfigured);
        Assert.Null(beforeModel.Stripe);

        var redirect = Assert.IsType<RedirectResult>(await controller.StripeConnect());
        Assert.StartsWith("https://connect.stripe.com/oauth/authorize?", redirect.Url);
        var query = HttpUtility.ParseQueryString(new Uri(redirect.Url).Query);
        Assert.Equal("https://app.example.test/Payments/StripeCallback", query["redirect_uri"]);
        var state = query["state"];
        Assert.False(string.IsNullOrWhiteSpace(state));
        Assert.DoesNotContain(agentId.ToString() + "|", state);   // signed, not readable

        var back = Assert.IsType<RedirectToActionResult>(await controller.StripeCallback("ac_code_1", state, null));
        Assert.Equal("Index", back.ActionName);
        Assert.Equal("ac_code_1", stripe.ExchangedCode);
        Assert.Contains("Stripe connected as Fortress Connect", (string)controller.TempData["Success"]!);

        var row = await db.AgentPaymentConnections.AsNoTracking().SingleAsync(c => c.AgentUserId == agentId);
        Assert.Equal(PaymentProviders.Stripe, row.Provider);
        Assert.Equal("acct_1", row.ExternalAccountId);
        Assert.Equal("Fortress Connect", row.DisplayName);
        Assert.True(row.IsActive);
        Assert.False(row.IsLive);
        Assert.Null(row.DisconnectedAt);
        Assert.Equal(string.Empty, row.EncryptedTokens);          // nothing secret of the adviser's is stored

        var after = Assert.IsType<ViewResult>(await controller.Index());
        Assert.Equal("acct_1", Assert.IsType<PaymentsViewModel>(after.Model).Stripe?.ExternalAccountId);

        Assert.IsType<RedirectToActionResult>(await controller.StripeDisconnect());
        Assert.Equal("acct_1", stripe.Deauthorized);
        Assert.Contains("Stripe disconnected", (string)controller.TempData["Success"]!);
        var closed = await db.AgentPaymentConnections.AsNoTracking().SingleAsync(c => c.AgentUserId == agentId);
        Assert.False(closed.IsActive);
        Assert.NotNull(closed.DisconnectedAt);
        Assert.Null(Assert.IsType<PaymentsViewModel>(Assert.IsType<ViewResult>(await controller.Index()).Model).Stripe);

        // A second connect reopens the same row rather than adding one.
        var again = HttpUtility.ParseQueryString(new Uri(Assert.IsType<RedirectResult>(await controller.StripeConnect()).Url).Query)["state"];
        await controller.StripeCallback("ac_code_2", again, null);
        Assert.Equal(1, await db.AgentPaymentConnections.CountAsync(c => c.AgentUserId == agentId));
        Assert.True((await db.AgentPaymentConnections.AsNoTracking().SingleAsync(c => c.AgentUserId == agentId)).IsActive);
    }

    [Fact]
    public async Task A_callback_carrying_another_advisers_state_is_forbidden_and_a_tampered_or_refused_one_records_nothing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentA = await SeedAgentAsync(db);
        var agentB = await SeedAgentAsync(db);
        var stripe = new FakeStripe();
        var provider = new EphemeralDataProtectionProvider();

        var stateForA = HttpUtility.ParseQueryString(new Uri(
            Assert.IsType<RedirectResult>(await NewController(db, stripe, provider, agentA).StripeConnect()).Url).Query)["state"];

        var controllerB = NewController(db, stripe, provider, agentB);
        Assert.IsType<ForbidResult>(await controllerB.StripeCallback("ac_code_x", stateForA, null));
        Assert.Null(stripe.ExchangedCode);

        var tampered = Assert.IsType<RedirectToActionResult>(await controllerB.StripeCallback("ac_code_x", "not-a-signed-state", null));
        Assert.Equal("Index", tampered.ActionName);
        Assert.Contains("invalid", (string)controllerB.TempData["Error"]!);

        var refused = Assert.IsType<RedirectToActionResult>(await controllerB.StripeCallback(null, null, "access_denied"));
        Assert.Equal("Index", refused.ActionName);
        Assert.Contains("cancelled or refused", (string)controllerB.TempData["Error"]!);

        Assert.Null(stripe.ExchangedCode);
        Assert.Equal(0, await db.AgentPaymentConnections.CountAsync());

        // Disconnect with nothing connected is a message, not an error.
        Assert.IsType<RedirectToActionResult>(await controllerB.StripeDisconnect());
        Assert.Contains("not connected", (string)controllerB.TempData["Error"]!);
        Assert.Null(stripe.Deauthorized);
    }

    [Fact]
    public async Task Connect_bounces_a_session_on_a_temporary_host_to_the_canonical_host_and_stops_when_stripe_is_not_set_up()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var provider = new EphemeralDataProtectionProvider();

        // Stripe only redirects to the registered address, so the session goes canonical first (the Google precedent).
        var onSubdomain = NewController(db, new FakeStripe(), provider, agentId, host: "bob.247advisers.test", path: "/portal/Payments/StripeConnect");
        var bounce = Assert.IsType<RedirectResult>(await onSubdomain.StripeConnect());
        Assert.Equal("https://app.example.test/portal/Payments/StripeConnect", bounce.Url);

        var notConfigured = NewController(db, new FakeStripe { IsConfigured = false }, provider, agentId);
        var stopped = Assert.IsType<RedirectToActionResult>(await notConfigured.StripeConnect());
        Assert.Equal("Index", stopped.ActionName);
        Assert.Contains("not set up", (string)notConfigured.TempData["Error"]!);
        Assert.False(Assert.IsType<PaymentsViewModel>(Assert.IsType<ViewResult>(await notConfigured.Index()).Model).StripeConfigured);
    }

    // -- the webhook, against MySQL --------------------------------------------------------------

    [Fact]
    public async Task A_signed_checkout_completed_event_settles_the_invoice_once_however_often_stripe_resends_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        await SeedConnectionAsync(db, agentId, "acct_1");
        var invoiceId = await SeedInvoiceAsync(db, agentId, 250m);

        var payload = Event("evt_1", "acct_1", invoiceId, 25000, "cad", "pi_1");
        Assert.IsType<OkResult>(await WebhookAsync(db, payload, StripeWebhookSignature.Header(payload, WebhookSecret, Now())));

        var invoice = await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId);
        Assert.Equal(ClientInvoiceStatus.Paid, invoice.Status);
        Assert.Equal(ClientInvoicePaymentMethod.Online, invoice.PaidMethod);
        Assert.NotNull(invoice.PaidAt);
        var payment = await db.ClientInvoicePayments.AsNoTracking().SingleAsync(p => p.ClientInvoiceId == invoiceId);
        Assert.Equal(PaymentProviders.Stripe, payment.Provider);
        Assert.Equal("evt_1", payment.ProviderEventId);
        Assert.Equal("pi_1", payment.ProviderPaymentId);
        Assert.Equal(250m, payment.Amount);
        Assert.Equal("CAD", payment.Currency);
        Assert.Equal(agentId, payment.AgentUserId);

        // The resend: acknowledged, and nothing changes.
        Assert.IsType<OkResult>(await WebhookAsync(db, payload, StripeWebhookSignature.Header(payload, WebhookSecret, Now())));
        Assert.Equal(1, await db.ClientInvoicePayments.CountAsync(p => p.ClientInvoiceId == invoiceId));
        Assert.Equal(invoice.PaidAt, (await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId)).PaidAt);

        // The same session reported under a new event id is the same payment; Stripe does not do
        // this, but the guard is the event id, so pin what happens: a second row, the invoice stays paid.
        var duplicate = Event("evt_2", "acct_1", invoiceId, 25000, "cad", "pi_1");
        Assert.IsType<OkResult>(await WebhookAsync(db, duplicate, StripeWebhookSignature.Header(duplicate, WebhookSecret, Now())));
        Assert.Equal(2, await db.ClientInvoicePayments.CountAsync(p => p.ClientInvoiceId == invoiceId));
        Assert.Equal(invoice.PaidAt, (await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId)).PaidAt);
    }

    [Fact]
    public async Task A_bad_or_stale_signature_and_a_malformed_body_are_refused_and_settle_nothing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        await SeedConnectionAsync(db, agentId, "acct_1");
        var invoiceId = await SeedInvoiceAsync(db, agentId, 250m);
        var payload = Event("evt_1", "acct_1", invoiceId, 25000, "cad", "pi_1");

        Assert.IsType<BadRequestResult>(await WebhookAsync(db, payload, StripeWebhookSignature.Header(payload, "whsec_wrong", Now())));
        Assert.IsType<BadRequestResult>(await WebhookAsync(db, payload, StripeWebhookSignature.Header(payload, WebhookSecret, Now() - 3600)));
        Assert.IsType<BadRequestResult>(await WebhookAsync(db, payload, ""));
        Assert.IsType<BadRequestResult>(await WebhookAsync(db, "", StripeWebhookSignature.Header("", WebhookSecret, Now())));
        Assert.IsType<BadRequestResult>(await WebhookAsync(db, "{not json", StripeWebhookSignature.Header("{not json", WebhookSecret, Now())));
        var noId = "{\"type\":\"checkout.session.completed\"}";
        Assert.IsType<BadRequestResult>(await WebhookAsync(db, noId, StripeWebhookSignature.Header(noId, WebhookSecret, Now())));

        Assert.Equal(ClientInvoiceStatus.Sent, (await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId)).Status);
        Assert.Equal(0, await db.ClientInvoicePayments.CountAsync());
    }

    [Fact]
    public async Task Events_that_are_not_ours_are_acknowledged_and_ignored_and_a_short_payment_is_recorded_but_does_not_settle()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        await SeedConnectionAsync(db, agentId, "acct_1");
        var invoiceId = await SeedInvoiceAsync(db, agentId, 250m);
        var now = DateTime.UtcNow;

        // Another connected account naming our invoice: not the adviser's account, ignored.
        Assert.Equal(StripeWebhookOutcome.Ignored, await StripeWebhookSettlement.ApplyAsync(db, Event("evt_other", "acct_other", invoiceId, 25000, "cad", "pi_x"), now));
        // The invoice's adviser never connected: also ignored (a stray event cannot pay anything).
        var strangerId = await SeedInvoiceAsync(db, await SeedAgentAsync(db), 10m);
        Assert.Equal(StripeWebhookOutcome.Ignored, await StripeWebhookSettlement.ApplyAsync(db, Event("evt_stranger", "acct_1", strangerId, 1000, "cad", "pi_y"), now));
        // Not a session we created (no invoice id), not paid yet, another event type, an unknown invoice.
        Assert.Equal(StripeWebhookOutcome.Ignored, await StripeWebhookSettlement.ApplyAsync(db, Event("evt_nometa", "acct_1", null, 25000, "cad", "pi_z"), now));
        Assert.Equal(StripeWebhookOutcome.Ignored, await StripeWebhookSettlement.ApplyAsync(db, Event("evt_unpaid", "acct_1", invoiceId, 25000, "cad", "pi_u", paymentStatus: "unpaid"), now));
        Assert.Equal(StripeWebhookOutcome.Ignored, await StripeWebhookSettlement.ApplyAsync(db, Event("evt_type", "acct_1", invoiceId, 25000, "cad", "pi_t", type: "payment_intent.succeeded"), now));
        Assert.Equal(StripeWebhookOutcome.NotFound, await StripeWebhookSettlement.ApplyAsync(db, Event("evt_gone", "acct_1", 99_999_999, 25000, "cad", "pi_g"), now));
        Assert.Equal(StripeWebhookOutcome.Malformed, await StripeWebhookSettlement.ApplyAsync(db, "[1,2]", now));
        Assert.Equal(StripeWebhookOutcome.Malformed, await StripeWebhookSettlement.ApplyAsync(db, "{\"id\":\"evt_nodata\",\"type\":\"checkout.session.completed\"}", now));

        Assert.Equal(0, await db.ClientInvoicePayments.CountAsync());
        Assert.Equal(ClientInvoiceStatus.Sent, (await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId)).Status);

        // Through the controller, the same ignored event is still a 200: Stripe must not retry it.
        var other = Event("evt_other2", "acct_other", invoiceId, 25000, "cad", "pi_x");
        Assert.IsType<OkResult>(await WebhookAsync(db, other, StripeWebhookSignature.Header(other, WebhookSecret, Now())));

        // Short: recorded so the money is not lost from the books, the invoice stays open.
        Assert.Equal(StripeWebhookOutcome.RecordedShort, await StripeWebhookSettlement.ApplyAsync(db, Event("evt_short", "acct_1", invoiceId, 20000, "cad", "pi_s"), now));
        var shortRow = await db.ClientInvoicePayments.AsNoTracking().SingleAsync();
        Assert.Equal(200m, shortRow.Amount);
        Assert.Equal(invoiceId, shortRow.ClientInvoiceId);
        Assert.Equal(ClientInvoiceStatus.Sent, (await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId)).Status);

        // A session created before a disconnect still settles: the closed connection still names the account.
        await db.AgentPaymentConnections.Where(c => c.AgentUserId == agentId).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsActive, false).SetProperty(c => c.DisconnectedAt, now));
        Assert.Equal(StripeWebhookOutcome.Settled, await StripeWebhookSettlement.ApplyAsync(db, Event("evt_late", "acct_1", invoiceId, 25000, "cad", "pi_l"), now));
        Assert.Equal(ClientInvoiceStatus.Paid, (await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId)).Status);
    }

    // -- helpers -----------------------------------------------------------------------------------

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string Event(string id, string account, int? invoiceId, long amountTotal, string currency, string paymentIntent,
        string paymentStatus = "paid", string type = "checkout.session.completed")
    {
        var metadata = invoiceId.HasValue ? $",\"metadata\":{{\"{StripeWebhookSettlement.InvoiceMetadataKey}\":\"{invoiceId}\"}}" : ",\"metadata\":{}";
        return $"{{\"id\":\"{id}\",\"object\":\"event\",\"account\":\"{account}\",\"type\":\"{type}\",\"livemode\":false," +
               $"\"data\":{{\"object\":{{\"id\":\"cs_{id}\",\"object\":\"checkout.session\",\"payment_status\":\"{paymentStatus}\"," +
               $"\"amount_total\":{amountTotal},\"currency\":\"{currency}\",\"payment_intent\":\"{paymentIntent}\"{metadata}}}}}}}";
    }

    private static async Task<IActionResult> WebhookAsync(IPRODbContext db, string payload, string signatureHeader)
    {
        var controller = new IPRO.Web.Controllers.PaymentsWebhookController(
            db, Options.Create(new StripeSettings { WebhookSecret = WebhookSecret }), NullLogger<IPRO.Web.Controllers.PaymentsWebhookController>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        if (signatureHeader.Length > 0) ctx.Request.Headers["Stripe-Signature"] = signatureHeader;
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return await controller.Stripe();
    }

    private static IPRO.Web.Controllers.PaymentsController NewController(IPRODbContext db, IStripeConnectService stripe, IDataProtectionProvider provider, int agentId,
        string host = "app.example.test", string path = "/portal/Payments")
    {
        var controller = new IPRO.Web.Controllers.PaymentsController(db, stripe, new GrantAll(), Config(), provider);
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString(host);
        ctx.Request.Path = path;
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());
        return controller;
    }

    private static async Task<int> SeedAgentAsync(IPRODbContext db)
    {
        var agent = new AgentUser
        {
            UserName = $"t527-{Guid.NewGuid():N}"[..20],
            Email = $"t527-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Stripe", LastName = "Adviser",
            CompanyName = "Fortress Connect",
            DomainName = $"t527-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario"
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task SeedConnectionAsync(IPRODbContext db, int agentId, string accountId)
    {
        db.AgentPaymentConnections.Add(new AgentPaymentConnection
        {
            AgentUserId = agentId, Provider = PaymentProviders.Stripe, ExternalAccountId = accountId, DisplayName = "Fortress Connect", IsActive = true
        });
        await db.SaveChangesAsync();
    }

    private static async Task<int> SeedInvoiceAsync(IPRODbContext db, int agentId, decimal total)
    {
        var client = new Client
        {
            AgentUserId = agentId, FirstName = "Cli", LastName = "Ent",
            Email = $"{Guid.NewGuid():N}@example.test"
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var invoice = new ClientInvoice
        {
            AgentUserId = agentId, ClientId = client.Id,
            DocumentType = ClientInvoiceDocumentType.Invoice, Status = ClientInvoiceStatus.Sent,
            DocumentNumber = $"S-{Guid.NewGuid():N}"[..20], Total = total, Currency = "CAD",
            ViewToken = Guid.NewGuid().ToString("N"),
            IssueDate = DateTime.UtcNow.Date.AddDays(-7),
            DueDate = DateTime.UtcNow.Date.AddDays(23),
            SentAt = DateTime.UtcNow.AddDays(-7)
        };
        db.ClientInvoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice.Id;
    }

    private sealed class FakeStripe : IStripeConnectService
    {
        public bool IsConfigured { get; init; } = true;
        public string? ExchangedCode { get; private set; }
        public string? Deauthorized { get; private set; }
        public string BuildAuthorizationUrl(string redirectUri, string state) => StripeConnectService.AuthorizationUrl("ca_test", redirectUri, state);
        public Task<StripeConnectedAccount> ExchangeCodeAsync(string code)
        {
            ExchangedCode = code;
            return Task.FromResult(new StripeConnectedAccount("acct_1", false, "Fortress Connect"));
        }
        public Task DeauthorizeAsync(string accountId)
        {
            Deauthorized = accountId;
            return Task.CompletedTask;
        }
    }

    private sealed class GrantAll : IPRO.Business.Interfaces.IPackageEntitlementService
    {
        public Task<IPRO.Business.Interfaces.PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) =>
            Task.FromResult(new IPRO.Business.Interfaces.PackageFeatureAccess { FeatureCode = featureCode, IsIncluded = true });
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => Task.FromResult(true);
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode) =>
            Task.FromResult(agentIds.Distinct().ToDictionary(a => a, _ => true));
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(false);
    }

    private sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var path = Path.Combine(dir!, relative);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }
}
