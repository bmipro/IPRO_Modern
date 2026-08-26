using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
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

// Billing wave 5 (2026-08-25) — the LOW sweep, in the owner-approved ranking: #2/#3 first (small
// fixes closing real correctness gaps), #1/#4/#5 next, #6–#10 last. Every defect test here was
// run against the pre-fix code and observed to FAIL.
public class BillingWave5Tests
{
    // ---- #2: webhook amounts parse invariant of the host culture -----------------------------

    [Fact]
    public void LOW2_paypal_amounts_parse_the_same_on_every_host_culture()
    {
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            // de-DE reads '.' as a thousands separator: the culture-sensitive parse turned
            // "678.00" into 67800 — a $60,000-class invoice minted from a $678 charge the day the
            // hosting culture ever changes. Latent on Azure's en-US; a time bomb, not a bug today.
            System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Assert.True(IPRO.Web.Controllers.BillingController.TryParsePayPalAmount("678.00", out var amount));
            Assert.Equal(678.00m, amount);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }

    // ---- #3: the Quebec precision fix reaches the sibling table ------------------------------

    [Fact]
    public async Task LOW3_client_invoice_tax_rate_column_is_repaired_to_five_decimals()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        // Recreate production's shape: the raw-DDL column at decimal(6,4) — 0.14975 stored as
        // 0.1498, so Quebec's client-facing invoices print "14.980 %". The identical defect was
        // fixed on `Invoices` on 2026-08-10; this is its sibling.
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE `ClientInvoices` MODIFY COLUMN `TaxRate` decimal(6,4) NOT NULL DEFAULT 0");

        await StartupSchemaRepair.EnsureClientInvoiceSchemaAsync(db);

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT COALESCE(MAX(NUMERIC_SCALE), -1) FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'ClientInvoices' AND COLUMN_NAME = 'TaxRate';";
        await db.Database.OpenConnectionAsync();
        var scale = Convert.ToInt32(await command.ExecuteScalarAsync());
        await db.Database.CloseConnectionAsync();
        Assert.True(scale >= 5, $"ClientInvoices.TaxRate scale is {scale}; 0.14975 needs 5");
    }

    // ---- #1: a convert derives credit only from money actually paid --------------------------

    [Fact]
    public async Task LOW1_a_fully_comped_subscription_cannot_convert_unpaid_value_into_free_time()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        // A 100%-promo annual: Amount is 0 and no money has ever settled on the row.
        var seed = await SeedActiveAnnualAsync(db, amount: 0m, nextBillingDate: now.AddMonths(7), paidNet: 0m);
        var cheaper = await SeedTargetRuleAsync(db, monthly: 30m, annual: 300m, setupFee: 0m);

        var result = await NewServiceWithPayPal(db).CreateSubscriptionAsync(
            seed.AgentId, cheaper, BillingPeriod.Monthly, "https://x/r", "https://x/c", "convert");
        db.ChangeTracker.Clear();

        // Pre-fix the fallback priced the credit at the FULL LIST annual — DOCS/22:80 says credit
        // derives from "the rate actually paid ... from invoices", and $0 paid converts to $0.
        Assert.False(result.Success);
        // "No Pending rows" is satisfiable by broken code (the failure exit voids its own rows):
        // the true pin is that no convert row was EVER created -- the refusal must come first.
        Assert.Equal(0, await db.SubscriptionChanges.AsNoTracking().CountAsync(c =>
            c.AgentUserId == seed.AgentId && c.ChangeType == SubscriptionChangeType.Downgrade));
    }

    // ---- #4: Resume on an abandoned convert stays a convert ----------------------------------

    [Fact]
    public async Task LOW4_resuming_an_abandoned_convert_recreates_the_convert_not_a_scheduled_downgrade()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        var seed = await SeedActiveAnnualAsync(db, amount: 600m, nextBillingDate: now.AddMonths(7).AddDays(10), paidNet: 600m);
        var cheaper = await SeedTargetRuleAsync(db, monthly: 30m, annual: 300m, setupFee: 0m);

        // The abandoned convert checkout: Pending billing + unpaid invoice + Pending change.
        var checkout = new IPRO.Entities.Billing
        {
            AgentUserId = seed.AgentId, BillingRuleId = cheaper, Amount = 300m,
            Status = BillingStatus.Pending, Period = BillingPeriod.Monthly,
            StartDate = now, CreatedAt = now.AddHours(-2)
        };
        db.Add(checkout);
        await db.SaveChangesAsync();
        var invoice = new Invoice
        {
            BillingId = checkout.Id, AgentUserId = seed.AgentId,
            InvoiceNumber = $"W5-L4-{checkout.Id}", SubTotal = 0m, TaxRate = 0m, TaxAmount = 0m, Total = 0m,
            IssuedAt = now.AddHours(-2), IsPaid = false
        };
        db.Add(invoice);
        var abandoned = new SubscriptionChange
        {
            AgentUserId = seed.AgentId, RequestedBillingRuleId = cheaper, BillingId = checkout.Id,
            ChangeType = SubscriptionChangeType.Downgrade, Status = SubscriptionChangeStatus.Pending,
            ProratedCredit = 250m, EffectiveDate = now, AmountDue = 0m, CreatedAt = now.AddHours(-2)
        };
        db.Add(abandoned);
        await db.SaveChangesAsync();
        var invoiceId = invoice.Id;
        var abandonedChangeId = abandoned.Id;
        db.ChangeTracker.Clear();

        await NewServiceWithPayPal(db).ResumePaymentAsync(seed.AgentId, invoiceId, "https://x/r", "https://x/c");
        db.ChangeTracker.Clear();

        // Pre-fix the delegate dropped downgradeMode: the agent who chose switch-now-with-credit
        // silently got a scheduled end-of-period downgrade instead (a Pending change with ZERO
        // credit riding the ACTIVE billing).
        // The offline PayPal leg voids the freshly created checkout after its rows are written
        // (same observation point as every H6/H2 test) -- so assert on the NEWEST convert row
        // regardless of status, excluding the one this test seeded.
        var newest = await db.SubscriptionChanges.AsNoTracking()
            .Where(c => c.AgentUserId == seed.AgentId && c.ChangeType == SubscriptionChangeType.Downgrade
                        && c.Id != abandonedChangeId)
            .OrderByDescending(c => c.Id).FirstAsync();
        Assert.True(newest.ProratedCredit > 0m,
            $"resume must recreate the CONVERT (credit > 0), not a scheduled downgrade; credit was {newest.ProratedCredit}");
    }

    // ---- #5: a webhook cancel of a Pending checkout takes the whole checkout with it ---------

    [Fact]
    public async Task LOW5_a_cancelled_webhook_for_a_pending_checkout_voids_its_change_and_frees_the_slot()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        var rule = await SeedTargetRuleAsync(db, monthly: 60m, annual: 600m, setupFee: 0m);
        var promo = new PromotionCode { Code = $"W5{Guid.NewGuid():N}"[..10], MaxRedemptions = 5, RedemptionCount = 1, IsActive = true };
        db.Add(promo);
        await db.SaveChangesAsync();
        var agent = await SeedAgentAsync(db, rule);
        var checkout = new IPRO.Entities.Billing
        {
            AgentUserId = agent, BillingRuleId = rule, Amount = 60m,
            PayPalSubscriptionId = "I-W5DEAD",
            Status = BillingStatus.Pending, Period = BillingPeriod.Monthly,
            StartDate = now, CreatedAt = now.AddHours(-1)
        };
        db.Add(checkout);
        await db.SaveChangesAsync();
        var change = new SubscriptionChange
        {
            AgentUserId = agent, RequestedBillingRuleId = rule, BillingId = checkout.Id,
            PromotionCodeId = promo.Id,
            ChangeType = SubscriptionChangeType.Subscribe, Status = SubscriptionChangeStatus.Pending,
            EffectiveDate = now, AmountDue = 67.80m, CreatedAt = now.AddHours(-1)
        };
        db.Add(change);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.True(await NewService(db).HandleSubscriptionCancelledWebhookAsync("I-W5DEAD", BillingStatus.Cancelled));
        db.ChangeTracker.Clear();

        // Pre-fix the raw path flipped only the billing: the Pending change survived forever
        // holding its claimed promo slot (the 48h sweep skips non-Pending billings), plus a stale
        // "pending change" banner until the agent's next subscribe action.
        var after = await db.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.Id == change.Id);
        Assert.Equal(SubscriptionChangeStatus.Cancelled, after.Status);
        var promoAfter = await db.PromotionCodes.AsNoTracking().SingleAsync(p => p.Id == promo.Id);
        Assert.Equal(0, promoAfter.RedemptionCount);
    }

    // ---- #6: recovery from Failed clears the suspension-era CancelledAt ----------------------

    [Fact]
    public async Task LOW6_a_failed_to_active_recovery_clears_the_stale_cancelled_at()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        var seed = await SeedActiveAnnualAsync(db, amount: 60m, nextBillingDate: now.AddDays(20), paidNet: 60m,
            period: BillingPeriod.Monthly, status: BillingStatus.Failed, payPalSubscriptionId: "I-W5REC",
            cancelledAt: now.AddDays(-3));

        Assert.True(await NewService(db).HandleSubscriptionPaymentCompletedWebhookAsync("I-W5REC", "TX-W5-REC", 67.80m));
        db.ChangeTracker.Clear();

        // Pre-fix the suspension-era CancelledAt survived the recovery, so a REAL cancellation
        // months later kept showing the old date in Admin (every later door writes with ??=).
        var billing = await db.Billings.AsNoTracking().SingleAsync(b => b.Id == seed.BillingId);
        Assert.Equal(BillingStatus.Active, billing.Status);
        Assert.Null(billing.CancelledAt);
    }

    // ---- #7: the suspension email stops promising a retry that does not exist ----------------

    [Fact]
    public async Task LOW7_the_suspended_dunning_email_tells_the_truth_about_what_to_do()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        var seed = await SeedActiveAnnualAsync(db, amount: 60m, nextBillingDate: now.AddDays(20), paidNet: 60m,
            period: BillingPeriod.Monthly, status: BillingStatus.Failed, payPalSubscriptionId: "I-W5SUSP");
        db.Add(new Invoice
        {
            BillingId = seed.BillingId, AgentUserId = seed.AgentId,
            InvoiceNumber = $"W5-L7-{seed.BillingId}", SubTotal = 60m, TaxRate = 0.13m,
            TaxAmount = 7.80m, Total = 67.80m,
            PayPalTransactionId = "PAYPAL_FAILED:TX-W5-F1",
            IssuedAt = now.AddHours(-30), IsPaid = false
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var recorder = new RecordingEmailService();
        await NewServiceRecording(db, recorder).NotifyBillingIssuesAsync();

        // Pre-fix the email said "update or retry your payment" — but no retry path exists for a
        // PayPal-side suspension; the only real actions are subscribing again from Billing or
        // contacting support, and telling an agent to do something impossible is churn.
        var mail = recorder.Sent.Single(m => m.To.Contains("@example.test"));
        Assert.DoesNotContain("retry your payment", mail.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("suspend", mail.Html, StringComparison.OrdinalIgnoreCase);
    }

    // ---- #8: the day-3 touch is not silently skipped -----------------------------------------

    [Fact]
    public async Task LOW8_a_late_first_dunning_run_still_sends_the_day3_touch_first()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, changeId) = await SeedStaleAppliedDowngradeAsync(db, daysAgo: 8, period: BillingPeriod.Monthly);

        var service = NewService(db);
        await service.NotifyBillingIssuesAsync();
        db.ChangeTracker.Clear();

        // Pre-fix: with no successful run inside the day 3-7 window, the bucket jumped straight
        // to 7 and the Day:3 touch never went out — the two-touch design degraded to one with no
        // record. Now the touches stay ordered: first overdue run sends Day:3, the next sends 7.
        Assert.True(await db.OperateLogs.AsNoTracking().AnyAsync(l =>
            l.AgentUserId == agentId && l.Action == "DowngradeCompletionReminder" && l.Description == $"Change:{changeId}:Day:3"));
        Assert.False(await db.OperateLogs.AsNoTracking().AnyAsync(l =>
            l.AgentUserId == agentId && l.Action == "DowngradeCompletionReminder" && l.Description == $"Change:{changeId}:Day:7"));

        await service.NotifyBillingIssuesAsync();
        db.ChangeTracker.Clear();
        Assert.True(await db.OperateLogs.AsNoTracking().AnyAsync(l =>
            l.AgentUserId == agentId && l.Action == "DowngradeCompletionReminder" && l.Description == $"Change:{changeId}:Day:7"));
    }

    // ---- #9: completion emails name the term the agent picked --------------------------------

    [Fact]
    public async Task LOW9_the_completion_reminder_names_the_billing_period_the_agent_chose()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, _) = await SeedStaleAppliedDowngradeAsync(db, daysAgo: 4, period: BillingPeriod.Annually);

        var recorder = new RecordingEmailService();
        await NewServiceRecording(db, recorder).NotifyBillingIssuesAsync();

        // A term switch (same package, new period) sent an email naming only the package — the
        // agent could complete on their OLD term and the fee-waiver matches on package id alone.
        var mail = recorder.Sent.Single(m => m.To.Contains("@example.test"));
        Assert.Contains("annual", mail.Html, StringComparison.OrdinalIgnoreCase);
    }

    // ---- #10: a data-gap row never earns a year of access from a fallback --------------------

    [Fact]
    public async Task LOW10_reconciling_an_ended_row_with_no_billing_date_flips_raw_instead_of_guessing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        var seed = await SeedActiveAnnualAsync(db, amount: 600m, nextBillingDate: null, paidNet: 600m,
            payPalSubscriptionId: "I-W5GAP");

        var paypal = new ScriptedPayPal();
        paypal.Subscriptions["I-W5GAP"] = ("CANCELLED", null);   // PayPal has no next date either
        await NewServiceScripted(db, paypal).ReconcileActiveSubscriptionsWithPayPalAsync();
        db.ChangeTracker.Clear();

        // Pre-fix the fallback computed paidThroughEnd = now + 1 year: cycleStart = now, and the
        // access branch granted a data-gap row a FULL YEAR of paid-through honor from thin air.
        var billing = await db.Billings.AsNoTracking().SingleAsync(b => b.Id == seed.BillingId);
        Assert.Equal(BillingStatus.Cancelled, billing.Status);
        Assert.Null(billing.PaidThroughAt);
        Assert.Equal(0, await db.SubscriptionChanges.AsNoTracking().CountAsync(c =>
            c.AgentUserId == seed.AgentId && c.ChangeType == SubscriptionChangeType.Cancel));
    }

    // ------------------------------------------------------------------------------ plumbing --

    private sealed record Seed(int AgentId, int BillingId);

    private static async Task<int> SeedTargetRuleAsync(IPRODbContext db, decimal monthly, decimal annual, decimal setupFee)
    {
        var rule = new BillingRule
        {
            PackageName = $"W5-{Guid.NewGuid():N}"[..20],
            MonthlyPrice = monthly, AnnualPrice = annual, SetupFee = setupFee, IsActive = true,
            PayPalMonthlyPlanId = $"P-{Guid.NewGuid():N}"[..12], PayPalAnnualPlanId = $"P-{Guid.NewGuid():N}"[..12]
        };
        db.Add(rule);
        await db.SaveChangesAsync();
        return rule.Id;
    }

    private static async Task<int> SeedAgentAsync(IPRODbContext db, int ruleId)
    {
        var agent = new AgentUser
        {
            UserName = $"w5-{Guid.NewGuid():N}"[..20],
            Email = $"w5-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Wave", LastName = "Five",
            DomainName = $"w5-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario", PackageId = ruleId
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<Seed> SeedActiveAnnualAsync(
        IPRODbContext db, decimal amount, DateTime? nextBillingDate, decimal paidNet,
        BillingPeriod period = BillingPeriod.Annually,
        BillingStatus status = BillingStatus.Active,
        string payPalSubscriptionId = "",
        DateTime? cancelledAt = null)
    {
        if (!await db.ProvinceTaxRates.AnyAsync(t => t.ProvinceCode == "ON"))
        {
            db.Add(new ProvinceTaxRate { ProvinceCode = "ON", ProvinceName = "Ontario", Rate = 0.13m, TaxLabel = "ON 13% HST", IsActive = true });
        }
        var basePrice = amount > 0m ? amount : 600m;
        var rule = new BillingRule { PackageName = $"W5-{Guid.NewGuid():N}"[..20], MonthlyPrice = basePrice / 10m, AnnualPrice = basePrice };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agentId = await SeedAgentAsync(db, rule.Id);
        var billing = new IPRO.Entities.Billing
        {
            AgentUserId = agentId,
            BillingRuleId = rule.Id,
            PayPalSubscriptionId = payPalSubscriptionId,
            Amount = amount,
            Status = status,
            Period = period,
            StartDate = DateTime.UtcNow.AddMonths(-5),
            NextBillingDate = nextBillingDate,
            CancelledAt = cancelledAt
        };
        db.Add(billing);
        await db.SaveChangesAsync();
        if (paidNet > 0m)
        {
            db.Add(new Invoice
            {
                BillingId = billing.Id, AgentUserId = agentId,
                InvoiceNumber = $"W5-{billing.Id}", SubTotal = paidNet, TaxRate = 0.13m,
                TaxAmount = Math.Round(paidNet * 0.13m, 2), Total = Math.Round(paidNet * 1.13m, 2),
                TaxRegion = "ON 13% HST",
                PayPalTransactionId = $"TXN-W5-{billing.Id}",
                IssuedAt = DateTime.UtcNow.AddMonths(-5),
                IsPaid = true
            });
            await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear();
        return new Seed(agentId, billing.Id);
    }

    private static async Task<(int AgentId, int ChangeId)> SeedStaleAppliedDowngradeAsync(
        IPRODbContext db, int daysAgo, BillingPeriod period)
    {
        var ruleId = await SeedTargetRuleAsync(db, monthly: 40m, annual: 400m, setupFee: 150m);
        var agentId = await SeedAgentAsync(db, ruleId);
        var change = new SubscriptionChange
        {
            AgentUserId = agentId,
            CurrentBillingRuleId = ruleId,
            RequestedBillingRuleId = ruleId,          // term-switch shape for LOW9; harmless for LOW8
            ChangeType = SubscriptionChangeType.Downgrade,
            Status = SubscriptionChangeStatus.Applied,
            ProratedCredit = 0m,
            Period = period,
            EffectiveDate = DateTime.UtcNow.AddDays(-daysAgo),
            AppliedAt = DateTime.UtcNow.AddDays(-daysAgo),
            CreatedAt = DateTime.UtcNow.AddDays(-daysAgo - 7)
        };
        db.Add(change);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (agentId, change.Id);
    }

    private static PayPalBillingService NewService(IPRODbContext db) => new(
        new UnitOfWork(db), db, new StubHttpClientFactory(), new StubEmailService(),
        Options.Create(new PayPalSettings()),
        new ConfigurationBuilder().Build(),
        NullLogger<PayPalBillingService>.Instance);

    private static PayPalBillingService NewServiceRecording(IPRODbContext db, RecordingEmailService recorder) => new(
        new UnitOfWork(db), db, new StubHttpClientFactory(), recorder,
        Options.Create(new PayPalSettings()),
        new ConfigurationBuilder().Build(),
        NullLogger<PayPalBillingService>.Instance);

    private static PayPalBillingService NewServiceWithPayPal(IPRODbContext db) => new(
        new UnitOfWork(db), db, new OfflineHttpClientFactory(), new StubEmailService(),
        Options.Create(new PayPalSettings { ClientId = "test-client", ClientSecret = "test-secret" }),
        new ConfigurationBuilder().Build(),
        NullLogger<PayPalBillingService>.Instance);

    private static PayPalBillingService NewServiceScripted(IPRODbContext db, ScriptedPayPal paypal) => new(
        new UnitOfWork(db), db, paypal, new StubEmailService(),
        Options.Create(new PayPalSettings { ClientId = "test-client", ClientSecret = "test-secret" }),
        new ConfigurationBuilder().Build(),
        NullLogger<PayPalBillingService>.Instance);

    private sealed class RecordingEmailService : IPRO.Email.IEmailService
    {
        public sealed record Message(string To, string Subject, string Html);
        public readonly List<Message> Sent = new();
        public Task<bool> SendAsync(string to, string name, string subject, string html, string? text = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null)
        { Sent.Add(new Message(to, subject, html)); return Task.FromResult(true); }
        public Task<IPRO.Email.EmailSendResult> SendDetailedAsync(string to, string name, string subject, string html, string? text = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null)
        { Sent.Add(new Message(to, subject, html)); return Task.FromResult(IPRO.Email.EmailSendResult.Sent()); }
        public Task<bool> SendBulkAsync(IEnumerable<IPRO.Email.EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
    }

    private sealed class ScriptedPayPal : IHttpClientFactory
    {
        public readonly Dictionary<string, (string Status, DateTime? NextBilling)> Subscriptions = new();
        public HttpClient CreateClient(string name) => new(new Handler(this));
        private sealed class Handler : HttpMessageHandler
        {
            private readonly ScriptedPayPal _owner;
            public Handler(ScriptedPayPal owner) => _owner = owner;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken ct)
            {
                var url = request.RequestUri!.AbsoluteUri;
                if (url.Contains("/v1/oauth2/token"))
                    return Task.FromResult(Json("{\"access_token\":\"scripted-token\",\"expires_in\":3600}"));
                if (request.Method == HttpMethod.Post && url.Contains("/cancel"))
                    return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
                if (request.Method == HttpMethod.Get && url.Contains("/v1/billing/subscriptions/"))
                {
                    var id = url.Split('/')[^1];
                    if (_owner.Subscriptions.TryGetValue(id, out var sub))
                    {
                        var billingInfo = sub.NextBilling.HasValue
                            ? $",\"billing_info\":{{\"next_billing_time\":\"{sub.NextBilling:yyyy-MM-ddTHH:mm:ssZ}\"}}"
                            : string.Empty;
                        return Task.FromResult(Json($"{{\"status\":\"{sub.Status}\"{billingInfo}}}"));
                    }
                    return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
                }
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            }
            private static HttpResponseMessage Json(string body) => new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class OfflineHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new RefuseHandler());
        private sealed class RefuseHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken ct)
                => throw new HttpRequestException("offline test handler");
        }
    }

    private sealed class StubEmailService : IPRO.Email.IEmailService
    {
        public Task<bool> SendAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(true);
        public Task<IPRO.Email.EmailSendResult> SendDetailedAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(IPRO.Email.EmailSendResult.Sent());
        public Task<bool> SendBulkAsync(IEnumerable<IPRO.Email.EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
    }
}
