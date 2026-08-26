using System;
using System.Collections.Generic;
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

// Billing wave 4 (2026-08-25) — the last four billing MEDIUMs from the four-auditor audit:
// state F6 (terminal-state relabels), state F5 (Keep-My-Plan vs in-flight converts), state
// F3c/jobs-4 (supersede outcomes), jobs-5 (stage-3 isolation). Every defect test here was run
// against the pre-fix code and observed to FAIL.
public class BillingWave4Tests
{
    // ------------------------------------------ F6: terminal states cannot be relabelled raw --

    [Fact]
    public async Task F6_a_late_suspended_delivery_cannot_relabel_a_cancelled_row_and_void_its_paid_through()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        // Cancelled-but-paid-through: the agent owns 5 more months. PayPal retries failed
        // deliveries for days, so a SUSPENDED event from BEFORE the cancel can land after it.
        var seed = await SeedAnnualAsync(db, amount: 600m, payPalSubscriptionId: "I-W4LATE",
            invoice: (600m, now.AddDays(-135), "TXN-W4-1"),
            status: BillingStatus.Cancelled, paidThroughAt: now.AddDays(150));

        Assert.True(await NewService(db).HandleSubscriptionCancelledWebhookAsync("I-W4LATE", BillingStatus.Failed));
        db.ChangeTracker.Clear();

        // Pre-fix the raw path relabelled Cancelled -> Failed: every gate honors PaidThroughAt on
        // Cancelled/Expired only, so the paid-up agent was locked out instantly -- and the
        // reconcile could not repair it (it inspects Active rows only).
        var billing = await db.Billings.AsNoTracking().SingleAsync(b => b.Id == seed.BillingId);
        Assert.Equal(BillingStatus.Cancelled, billing.Status);
        Assert.NotNull(billing.PaidThroughAt);
    }

    [Fact]
    public async Task F6_cancelling_a_suspended_subscription_still_earns_the_docs22_outcome()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        // Suspended (Failed) at month 5 of a paid annual year, then the agent cancels at PayPal.
        var seed = await SeedAnnualAsync(db, amount: 600m, payPalSubscriptionId: "I-W4SUSP",
            invoice: (600m, now.AddDays(-135), "TXN-W4-2"),
            status: BillingStatus.Failed, nextBillingDate: now.AddDays(-135).AddYears(1));

        Assert.True(await NewService(db).HandleSubscriptionCancelledWebhookAsync("I-W4SUSP", BillingStatus.Cancelled));
        db.ChangeTracker.Clear();

        // Pre-fix the M5 guard demanded Active, so Failed -> Cancelled took the raw path: no
        // PaidThroughAt, no refund row -- the suspension became an antechamber that forfeited the
        // whole DOCS/22 outcome for a year the agent had paid in full.
        var billing = await db.Billings.AsNoTracking().SingleAsync(b => b.Id == seed.BillingId);
        Assert.Equal(BillingStatus.Cancelled, billing.Status);
        Assert.NotNull(billing.PaidThroughAt);
        var change = await db.SubscriptionChanges.AsNoTracking()
            .SingleAsync(c => c.AgentUserId == seed.AgentId && c.ChangeType == SubscriptionChangeType.Cancel);
        Assert.Equal(300.00m, change.RefundNetAmount);   // month 5, the full outcome
    }

    // ------------------------------- F5(state): Keep-My-Plan versus an in-flight convert --

    [Fact]
    public async Task F5_keep_my_current_plan_voids_an_in_flight_convert_checkout_completely()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, changeId, promoId, billingId) = await SeedConvertCheckoutAsync(db, "I-W4CONV");

        var paypal = new ScriptedPayPal();
        paypal.Subscriptions["I-W4CONV"] = ("APPROVAL_PENDING", null);
        var result = await NewServiceScripted(db, paypal).CancelScheduledChangeAsync(agentId);
        db.ChangeTracker.Clear();

        // Pre-fix only the change row was cancelled: the Pending billing survived forever (no
        // sweeper matches change-Cancelled+billing-Pending), the promo slot stayed claimed, and
        // the PayPal approval link stayed LIVE -- approving it later executed the very change the
        // agent had just undone.
        Assert.True(result.Success);
        var change = await db.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.Id == changeId);
        Assert.Equal(SubscriptionChangeStatus.Cancelled, change.Status);
        var billing = await db.Billings.AsNoTracking().SingleAsync(b => b.Id == billingId);
        Assert.Equal(BillingStatus.Cancelled, billing.Status);
        var promo = await db.PromotionCodes.AsNoTracking().SingleAsync(p => p.Id == promoId);
        Assert.Equal(0, promo.RedemptionCount);
        Assert.Contains("I-W4CONV", paypal.CancelledSubscriptions);
    }

    [Fact]
    public async Task F5_keep_my_current_plan_still_leaves_a_scheduled_downgrades_active_billing_alone()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        var seed = await SeedAnnualAsync(db, amount: 600m, payPalSubscriptionId: "",
            invoice: (600m, now.AddDays(-30), "TXN-W4-SCHED"), period: BillingPeriod.Monthly,
            nextBillingDate: now.AddDays(20));
        db.Add(new SubscriptionChange
        {
            AgentUserId = seed.AgentId,
            CurrentBillingRuleId = null,
            RequestedBillingRuleId = (await db.Billings.AsNoTracking().SingleAsync(b => b.Id == seed.BillingId)).BillingRuleId,
            BillingId = seed.BillingId,                    // the ACTIVE row: a true scheduled change
            ChangeType = SubscriptionChangeType.Downgrade,
            Status = SubscriptionChangeStatus.Pending,
            EffectiveDate = now.AddDays(20)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.True((await NewService(db).CancelScheduledChangeAsync(seed.AgentId)).Success);
        db.ChangeTracker.Clear();

        var billing = await db.Billings.AsNoTracking().SingleAsync(b => b.Id == seed.BillingId);
        Assert.Equal(BillingStatus.Active, billing.Status);   // the subscription itself is untouched
    }

    // --------------------------- F3c / jobs-4: a superseded row never mints a second outcome --

    [Fact]
    public async Task F3c_a_superseded_rows_cancellation_flips_it_raw_instead_of_minting_a_refund()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        // The upgrade completed: a NEWER Active billing exists. Then the CANCELLED webhook for the
        // OLD subscription arrives (our own supersede cancel echoing back, or the reconcile
        // discovering it) while the old row is still locally Active -- the half-persisted window.
        var old = await SeedAnnualAsync(db, amount: 600m, payPalSubscriptionId: "I-W4OLD",
            invoice: (600m, now.AddDays(-135), "TXN-W4-OLDPAY"));
        await SeedAnnualAsync(db, amount: 1200m, payPalSubscriptionId: "I-W4NEW",
            invoice: (500m, now.AddDays(-10), "TXN-W4-UPGRADE"), agentId: old.AgentId);

        Assert.True(await NewService(db).HandleSubscriptionCancelledWebhookAsync("I-W4OLD", BillingStatus.Cancelled));
        db.ChangeTracker.Clear();

        // Pre-fix the M5 door minted a full month-5 clawback ($300 + tax) for a subscription
        // whose unused value had ALREADY moved into the upgrade as proration credit -- value
        // handed out twice, and the Applied Cancel row consumed the H6 setup-fee waiver.
        var billing = await db.Billings.AsNoTracking().SingleAsync(b => b.Id == old.BillingId);
        Assert.Equal(BillingStatus.Cancelled, billing.Status);
        Assert.Equal(0, await db.SubscriptionChanges.AsNoTracking()
            .CountAsync(c => c.AgentUserId == old.AgentId && c.ChangeType == SubscriptionChangeType.Cancel));
    }

    [Fact]
    public async Task F3c_the_downgrades_own_cancellation_row_does_not_consume_the_completion_waiver()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        // A scheduled downgrade applied (old billing cancelled), and the race left a Cancel-type
        // row REFERENCING THAT SAME BILLING (whichever door recorded the old subscription's
        // death). That row is the downgrade's own cancellation -- not the agent answering -- so
        // the completion waiver must survive it.
        var (agentId, targetRuleId, oldBillingId) = await SeedAppliedDowngradeWithOldBillingAsync(db);
        db.Add(new SubscriptionChange
        {
            AgentUserId = agentId,
            RequestedBillingRuleId = targetRuleId,
            BillingId = oldBillingId,                      // SAME billing as the applied downgrade
            ChangeType = SubscriptionChangeType.Cancel,
            Status = SubscriptionChangeStatus.Applied,
            EffectiveDate = DateTime.UtcNow.AddHours(-2),
            AppliedAt = DateTime.UtcNow.AddHours(-2),
            CreatedAt = DateTime.UtcNow.AddHours(-2)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await NewServiceWithPayPal(db).CreateSubscriptionAsync(
            agentId, targetRuleId, BillingPeriod.Monthly, "https://x/r", "https://x/c");
        db.ChangeTracker.Clear();

        // The offline PayPal leg fails after the checkout row + invoice are written (the same
        // observation point every H6 test uses) -- the invoice is the waiver's truth.
        var billing = await db.Billings.AsNoTracking()
            .Where(b => b.AgentUserId == agentId && b.BillingRuleId == targetRuleId && b.Id != oldBillingId)
            .OrderByDescending(b => b.Id).FirstAsync();
        var invoice = await db.Invoices.AsNoTracking()
            .Where(i => i.BillingId == billing.Id).OrderByDescending(i => i.Id).FirstAsync();
        Assert.Equal(40m, invoice.SubTotal);   // fee still WAIVED: completing our own change
    }

    // ----------------------------------------------- jobs-5: stage-3 fails per pair, not whole --

    [Fact]
    public async Task JOBS5_one_poisoned_duplicate_pair_does_not_abort_the_whole_convergence_sweep()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        // Two agents, each holding TWO Active rows (the earlier failed-cancel shape A2-H2 covers).
        var a1 = await SeedAnnualAsync(db, amount: 600m, payPalSubscriptionId: "", invoice: (600m, now.AddDays(-30), "TXN-J5-A1"));
        await SeedAnnualAsync(db, amount: 600m, payPalSubscriptionId: "", invoice: (600m, now.AddDays(-1), "TXN-J5-A2"), agentId: a1.AgentId);
        var b1 = await SeedAnnualAsync(db, amount: 600m, payPalSubscriptionId: "", invoice: (600m, now.AddDays(-30), "TXN-J5-B1"));
        await SeedAnnualAsync(db, amount: 600m, payPalSubscriptionId: "", invoice: (600m, now.AddDays(-1), "TXN-J5-B2"), agentId: b1.AgentId);

        // Poison the shared tracker: pre-fix stage 3 mutated every pair and saved ONCE at the end
        // -- the poisoned save discarded every convergence and left the tracker dirty for stage 4.
        db.Add(new SubscriptionChange
        {
            AgentUserId = a1.AgentId,
            RequestedBillingRuleId = 999_999_999,
            ChangeType = SubscriptionChangeType.Subscribe,
            Status = SubscriptionChangeStatus.Cancelled,
            EffectiveDate = now
        });

        await NewService(db).ReconcileDuplicateActiveSubscriptionsAsync();
        db.ChangeTracker.Clear();

        var converged = await db.Billings.AsNoTracking()
            .Where(b => b.AgentUserId == a1.AgentId || b.AgentUserId == b1.AgentId)
            .CountAsync(b => b.Status == BillingStatus.Cancelled);
        Assert.Equal(1, converged);   // the poisoned pair retries next hour; the other converges NOW
    }

    // ------------------------------------------------------------------------------ plumbing --

    private sealed record Seed(int AgentId, int BillingId);

    private static async Task<Seed> SeedAnnualAsync(
        IPRODbContext db, decimal amount, string payPalSubscriptionId,
        (decimal SubTotal, DateTime IssuedAt, string Txn) invoice,
        BillingStatus status = BillingStatus.Active,
        BillingPeriod period = BillingPeriod.Annually,
        DateTime? nextBillingDate = null,
        DateTime? paidThroughAt = null,
        int? agentId = null)
    {
        if (!await db.ProvinceTaxRates.AnyAsync(t => t.ProvinceCode == "ON"))
        {
            db.Add(new ProvinceTaxRate { ProvinceCode = "ON", ProvinceName = "Ontario", Rate = 0.13m, TaxLabel = "ON 13% HST", IsActive = true });
        }
        var rule = new BillingRule { PackageName = $"W4-{Guid.NewGuid():N}"[..20], MonthlyPrice = amount / 10m, AnnualPrice = amount };
        db.Add(rule);
        await db.SaveChangesAsync();

        int theAgentId;
        if (agentId.HasValue) theAgentId = agentId.Value;
        else
        {
            var agent = new AgentUser
            {
                UserName = $"w4-{Guid.NewGuid():N}"[..20],
                Email = $"w4-{Guid.NewGuid():N}"[..12] + "@example.test",
                FirstName = "Wave", LastName = "Four",
                DomainName = $"w4-{Guid.NewGuid():N}"[..24],
                Country = "Canada", Province = "Ontario", PackageId = rule.Id
            };
            db.Add(agent);
            await db.SaveChangesAsync();
            theAgentId = agent.Id;
        }

        var billing = new IPRO.Entities.Billing
        {
            AgentUserId = theAgentId,
            BillingRuleId = rule.Id,
            PayPalSubscriptionId = payPalSubscriptionId,
            Amount = amount,
            Status = status,
            Period = period,
            StartDate = DateTime.UtcNow.AddYears(-1),
            NextBillingDate = nextBillingDate ?? DateTime.UtcNow.AddDays(230),
            PaidThroughAt = paidThroughAt,
            CancelledAt = status is BillingStatus.Cancelled or BillingStatus.Expired ? DateTime.UtcNow.AddHours(-3) : null
        };
        db.Add(billing);
        await db.SaveChangesAsync();
        db.Add(new Invoice
        {
            BillingId = billing.Id,
            AgentUserId = theAgentId,
            InvoiceNumber = $"W4-{billing.Id}",
            SubTotal = invoice.SubTotal,
            TaxRate = 0.13m,
            TaxAmount = Math.Round(invoice.SubTotal * 0.13m, 2),
            Total = Math.Round(invoice.SubTotal * 1.13m, 2),
            TaxRegion = "ON 13% HST",
            PayPalTransactionId = invoice.Txn,
            IssuedAt = invoice.IssuedAt,
            IsPaid = true
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seed(theAgentId, billing.Id);
    }

    private static async Task<(int AgentId, int ChangeId, int PromoId, int BillingId)> SeedConvertCheckoutAsync(
        IPRODbContext db, string payPalSubscriptionId)
    {
        var rule = new BillingRule { PackageName = $"W4-{Guid.NewGuid():N}"[..20], MonthlyPrice = 30m, AnnualPrice = 300m };
        var promo = new PromotionCode { Code = $"W4{Guid.NewGuid():N}"[..10], MaxRedemptions = 5, RedemptionCount = 1, IsActive = true };
        db.AddRange(rule, promo);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"w4-{Guid.NewGuid():N}"[..20],
            Email = $"w4-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Convert", LastName = "Undo",
            DomainName = $"w4-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario", PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var billing = new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id,
            BillingRuleId = rule.Id,
            PayPalSubscriptionId = payPalSubscriptionId,
            Amount = 300m,
            Status = BillingStatus.Pending,                // the convert's NEW checkout row
            Period = BillingPeriod.Annually,
            StartDate = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow.AddHours(-1)
        };
        db.Add(billing);
        await db.SaveChangesAsync();
        var change = new SubscriptionChange
        {
            AgentUserId = agent.Id,
            RequestedBillingRuleId = rule.Id,
            BillingId = billing.Id,
            PromotionCodeId = promo.Id,
            ChangeType = SubscriptionChangeType.Downgrade, // convert shape
            Status = SubscriptionChangeStatus.Pending,
            ProratedCredit = 250m,
            EffectiveDate = DateTime.UtcNow,
            AmountDue = 0m,
            CreatedAt = DateTime.UtcNow.AddHours(-1)
        };
        db.Add(change);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (agent.Id, change.Id, promo.Id, billing.Id);
    }

    private static async Task<(int AgentId, int TargetRuleId, int OldBillingId)> SeedAppliedDowngradeWithOldBillingAsync(IPRODbContext db)
    {
        if (!await db.ProvinceTaxRates.AnyAsync(t => t.ProvinceCode == "ON"))
        {
            db.Add(new ProvinceTaxRate { ProvinceCode = "ON", ProvinceName = "Ontario", Rate = 0.13m, TaxLabel = "ON 13% HST", IsActive = true });
        }
        var target = new BillingRule
        {
            PackageName = $"W4-{Guid.NewGuid():N}"[..20],
            MonthlyPrice = 40m, AnnualPrice = 400m, SetupFee = 150m, IsActive = true,
            PayPalMonthlyPlanId = "P-W4-M", PayPalAnnualPlanId = "P-W4-A"
        };
        db.Add(target);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"w4-{Guid.NewGuid():N}"[..20],
            Email = $"w4-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Waiver", LastName = "Race",
            DomainName = $"w4-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario", PackageId = target.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var oldBilling = new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id,
            BillingRuleId = target.Id,
            Amount = 60m,
            Status = BillingStatus.Cancelled,              // the apply cancelled it
            Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddDays(-40),
            CancelledAt = DateTime.UtcNow.AddDays(-1),
            CreatedAt = DateTime.UtcNow.AddDays(-40)
        };
        db.Add(oldBilling);
        await db.SaveChangesAsync();
        db.Add(new SubscriptionChange
        {
            AgentUserId = agent.Id,
            RequestedBillingRuleId = target.Id,
            BillingId = oldBilling.Id,
            ChangeType = SubscriptionChangeType.Downgrade,
            Status = SubscriptionChangeStatus.Applied,
            ProratedCredit = 0m,                           // scheduled shape
            EffectiveDate = DateTime.UtcNow.AddDays(-1),
            AppliedAt = DateTime.UtcNow.AddDays(-1),
            CreatedAt = DateTime.UtcNow.AddDays(-8)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (agent.Id, target.Id, oldBilling.Id);
    }

    private static PayPalBillingService NewService(IPRODbContext db) => new(
        new UnitOfWork(db), db, new StubHttpClientFactory(), new StubEmailService(),
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

    private sealed class ScriptedPayPal : IHttpClientFactory
    {
        public readonly Dictionary<string, (string Status, DateTime? NextBilling)> Subscriptions = new();
        public readonly List<string> CancelledSubscriptions = new();
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
                {
                    _owner.CancelledSubscriptions.Add(url.Split('/')[^2]);
                    return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
                }
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
