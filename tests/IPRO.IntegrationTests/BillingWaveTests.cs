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

// Billing wave (2026-08-25) — the PayPal/billing cluster from DOCS/AUDIT_2026-08-20_POST_SWEEP.md.
// Every test here was first run against the PRE-FIX code and observed to FAIL; the finding id each
// one pins is in its name. The chronic trap these guard against: fixtures that only ever model the
// FIRST year/cycle, which is exactly why C2 shipped invisible.
public class BillingWaveTests
{
    // ------------------------------------------------------------------ C2 / M3 / M4: refunds --

    [Fact]
    public async Task C2_annual_cancel_after_renewal_measures_from_the_renewed_cycle_not_activation()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        // Activated 17 months ago; renewed ~5 months ago (anniversary anchored 10 days off the
        // month boundary so clock drift between seed and cancel cannot flip the month count).
        var now = DateTime.UtcNow;
        var nextAnniversary = now.AddMonths(7).AddDays(10);
        var seed = await SeedBillingAsync(db,
            startDate: now.AddMonths(-17),
            nextBillingDate: nextAnniversary,
            invoiceIssuedAt: nextAnniversary.AddYears(-1),   // the RENEWAL payment
            invoiceTxn: "TXN-RENEWAL-1");

        Assert.True(await NewService(db).CancelSubscriptionAsync(seed.AgentId));
        db.ChangeTracker.Clear();

        var billing = await db.Billings.AsNoTracking().SingleAsync(b => b.Id == seed.BillingId);
        var change = await db.SubscriptionChanges.AsNoTracking()
            .SingleAsync(c => c.AgentUserId == seed.AgentId && c.ChangeType == SubscriptionChangeType.Cancel);

        // Month 5 of the RENEWED year: $600 - 5 x $60 = $300 net, $39 HST. The pre-fix code
        // measured from activation (month 17 -> capped at 12), decided "past the crossover",
        // refunded nothing, and set PaidThroughAt five months in the past — gating instantly.
        Assert.Equal(300.00m, change.RefundNetAmount);
        Assert.Equal(39.00m, change.RefundTaxAmount);
        Assert.Equal(RefundStatus.Pending, change.RefundStatus);
        Assert.Equal("TXN-RENEWAL-1", change.RefundPayPalTransactionId);
        Assert.NotNull(billing.PaidThroughAt);
        Assert.True(billing.PaidThroughAt > now,
            $"PaidThroughAt {billing.PaidThroughAt:yyyy-MM-dd} must be in the future; a past date gates a paying customer instantly.");
        AssertClose(nextAnniversary.AddYears(-1).AddMonths(5).AddDays(PrepaidValue.CancellationGraceDays),
            billing.PaidThroughAt);
    }

    [Fact]
    public async Task M3_refund_tax_uses_the_rate_actually_charged_on_the_invoice_not_todays_province()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        // Paid in Ontario (13% on the invoice); has since moved to Alberta (5%). CRA already has
        // the 13% — the refund must return what was charged, not reprice it at today's province.
        var now = DateTime.UtcNow;
        var seed = await SeedBillingAsync(db,
            startDate: now.AddDays(-135),
            nextBillingDate: now.AddDays(-135).AddYears(1),
            invoiceIssuedAt: now.AddDays(-135),
            invoiceTxn: "TXN-ON-1");
        if (!await db.ProvinceTaxRates.AnyAsync(t => t.ProvinceCode == "AB"))
        {
            db.Add(new ProvinceTaxRate { ProvinceCode = "AB", ProvinceName = "Alberta", Rate = 0.05m, TaxLabel = "AB 5% GST", IsActive = true });
        }
        var agent = await db.AgentUsers.SingleAsync(a => a.Id == seed.AgentId);
        agent.Province = "Alberta";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.True(await NewService(db).CancelSubscriptionAsync(seed.AgentId));
        db.ChangeTracker.Clear();

        var change = await db.SubscriptionChanges.AsNoTracking()
            .SingleAsync(c => c.AgentUserId == seed.AgentId && c.ChangeType == SubscriptionChangeType.Cancel);
        Assert.Equal(300.00m, change.RefundNetAmount);          // month 5, $600 - 5 x $60
        Assert.Equal(39.00m, change.RefundTaxAmount);           // 13% as invoiced — NOT 5% ($15)
        Assert.Equal(339.00m, change.RefundGrossAmount);
    }

    [Fact]
    public async Task M4_refund_derives_from_the_amount_actually_paid_not_todays_package_price()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var now = DateTime.UtcNow;
        var seed = await SeedBillingAsync(db,
            startDate: now.AddDays(-135),
            nextBillingDate: now.AddDays(-135).AddYears(1),
            invoiceIssuedAt: now.AddDays(-135),
            invoiceTxn: "TXN-PRICE-1");

        // The owner raises the package price AFTER this agent bought their year.
        var rule = await db.BillingRules.SingleAsync(r => r.Id == seed.RuleId);
        rule.MonthlyPrice = 75m;
        rule.AnnualPrice = 750m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.True(await NewService(db).CancelSubscriptionAsync(seed.AgentId));
        db.ChangeTracker.Clear();

        var change = await db.SubscriptionChanges.AsNoTracking()
            .SingleAsync(c => c.AgentUserId == seed.AgentId && c.ChangeType == SubscriptionChangeType.Cancel);
        // They paid $600 for the year (a month is $600/10 under the documented two-months-free
        // structure): month 5 refunds $300. The pre-fix code clawed back at TODAY'S $75 and
        // refunded only $225 — a price rise silently shrank money already owed.
        Assert.Equal(300.00m, change.RefundNetAmount);
        Assert.Equal(RefundStatus.Pending, change.RefundStatus);
    }

    // ------------------------------------------------------- M5: PayPal-initiated cancellation --

    [Fact]
    public async Task M5_paypal_initiated_cancel_honors_paid_through_and_queues_the_refund()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        // Month 5 of an annual year; the agent cancels inside PayPal's own interface (which our
        // shipped ToS explicitly invites). The only signal we get is the CANCELLED webhook.
        var now = DateTime.UtcNow;
        var seed = await SeedBillingAsync(db,
            startDate: now.AddDays(-135),
            nextBillingDate: now.AddDays(-135).AddYears(1),
            invoiceIssuedAt: now.AddDays(-135),
            invoiceTxn: "TXN-M5-1",
            payPalSubscriptionId: "I-M5TEST");

        Assert.True(await NewService(db).HandleSubscriptionCancelledWebhookAsync("I-M5TEST", BillingStatus.Cancelled));
        db.ChangeTracker.Clear();

        var billing = await db.Billings.AsNoTracking().SingleAsync(b => b.Id == seed.BillingId);
        Assert.Equal(BillingStatus.Cancelled, billing.Status);
        // Pre-fix: PaidThroughAt stayed null (old-style immediate gate) and no refund row was
        // written -- instant lockout and nobody ever learns $339 is owed.
        Assert.NotNull(billing.PaidThroughAt);
        Assert.True(billing.PaidThroughAt > now);
        var change = await db.SubscriptionChanges.AsNoTracking()
            .SingleAsync(c => c.AgentUserId == seed.AgentId && c.ChangeType == SubscriptionChangeType.Cancel);
        Assert.Equal(300.00m, change.RefundNetAmount);
        Assert.Equal(RefundStatus.Pending, change.RefundStatus);
        Assert.Equal("TXN-M5-1", change.RefundPayPalTransactionId);

        // And the same webhook arriving for an ALREADY-cancelled row (supersede, replay, or a
        // self-cancel racing its own webhook) must not mint a second refund row.
        Assert.True(await NewService(db).HandleSubscriptionCancelledWebhookAsync("I-M5TEST", BillingStatus.Cancelled));
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.SubscriptionChanges.AsNoTracking()
            .CountAsync(c => c.AgentUserId == seed.AgentId && c.ChangeType == SubscriptionChangeType.Cancel));
    }

    [Fact]
    public async Task M5_suspension_still_gates_immediately_it_is_a_payment_problem_not_a_cancel()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        var seed = await SeedBillingAsync(db,
            startDate: now.AddDays(-135),
            nextBillingDate: now.AddDays(-135).AddYears(1),
            invoiceIssuedAt: now.AddDays(-135),
            invoiceTxn: "TXN-M5-2",
            payPalSubscriptionId: "I-M5SUSP");

        Assert.True(await NewService(db).HandleSubscriptionCancelledWebhookAsync("I-M5SUSP", BillingStatus.Failed));
        db.ChangeTracker.Clear();

        var billing = await db.Billings.AsNoTracking().SingleAsync(b => b.Id == seed.BillingId);
        Assert.Equal(BillingStatus.Failed, billing.Status);
        Assert.Null(billing.PaidThroughAt);   // no paid-through grace for a suspension
        Assert.Equal(0, await db.SubscriptionChanges.AsNoTracking()
            .CountAsync(c => c.AgentUserId == seed.AgentId && c.ChangeType == SubscriptionChangeType.Cancel));
    }

    // ------------------------------------------- H5 / H12: promo slots and in-flight checkouts --

    [Fact]
    public async Task H5_cancelling_a_pending_checkout_releases_the_promo_slot()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, invoiceId, promoId, _) = await SeedPendingCheckoutAsync(db, changeAgeHours: 0);

        Assert.True(await NewService(db).CancelPendingPaymentAsync(agentId, invoiceId));
        db.ChangeTracker.Clear();

        // Pre-fix: CancelPendingPaymentAsync cancelled the change WITHOUT the release its sibling
        // CancelPendingChangesAsync performs -- the slot leaked permanently, and the agent's own
        // retry then found "redemption limit reached" and silently paid full price.
        var promo = await db.PromotionCodes.AsNoTracking().SingleAsync(p => p.Id == promoId);
        Assert.Equal(0, promo.RedemptionCount);
    }

    [Fact]
    public async Task H5_abandoned_checkouts_are_swept_after_48h_and_release_their_slots()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, _, promoId, changeId) = await SeedPendingCheckoutAsync(db, changeAgeHours: 72);

        // A scheduled downgrade rides an ACTIVE billing and may legitimately wait months -- the
        // sweep must never touch it, however old the row is.
        var scheduledSeed = await SeedBillingAsync(db,
            startDate: DateTime.UtcNow.AddDays(-30),
            nextBillingDate: DateTime.UtcNow.AddDays(20),
            invoiceIssuedAt: DateTime.UtcNow.AddDays(-30),
            invoiceTxn: "TXN-SCHED-1",
            period: BillingPeriod.Monthly,
            amount: 60m);
        db.Add(new SubscriptionChange
        {
            AgentUserId = scheduledSeed.AgentId,
            CurrentBillingRuleId = scheduledSeed.RuleId,
            RequestedBillingRuleId = scheduledSeed.RuleId,
            BillingId = scheduledSeed.BillingId,           // the ACTIVE row
            ChangeType = SubscriptionChangeType.Downgrade,
            Status = SubscriptionChangeStatus.Pending,
            EffectiveDate = DateTime.UtcNow.AddDays(20),
            CreatedAt = DateTime.UtcNow.AddDays(-30)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await NewService(db).ProcessDueSubscriptionChangesAsync();
        db.ChangeTracker.Clear();

        var change = await db.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.Id == changeId);
        Assert.Equal(SubscriptionChangeStatus.Cancelled, change.Status);
        var billing = await db.Billings.AsNoTracking().SingleAsync(b => b.Id == change.BillingId);
        Assert.Equal(BillingStatus.Cancelled, billing.Status);
        var promo = await db.PromotionCodes.AsNoTracking().SingleAsync(p => p.Id == promoId);
        Assert.Equal(0, promo.RedemptionCount);

        var scheduled = await db.SubscriptionChanges.AsNoTracking()
            .SingleAsync(c => c.AgentUserId == scheduledSeed.AgentId && c.ChangeType == SubscriptionChangeType.Downgrade);
        Assert.Equal(SubscriptionChangeStatus.Pending, scheduled.Status);
    }

    [Fact]
    public async Task H12_an_in_flight_convert_checkout_is_not_cancelled_as_stale()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        // Convert-downgrade shape as BeginPaidChangeAsync writes it: ChangeType=Downgrade,
        // EffectiveDate=now (immediately "due"), BillingId = the NEW Pending billing the agent is
        // still approving at PayPal. Pre-fix the hourly sweeper read that Pending billing as "no
        // longer Active", declared the change stale, and cancelled it out from under the agent
        // within the hour -- which also wrongly lifted the UX-TERMSWITCH guard.
        var (agentId, _, _, changeId) = await SeedPendingCheckoutAsync(
            db, changeAgeHours: 0, changeType: SubscriptionChangeType.Downgrade, effectiveNow: true);

        await NewService(db).ProcessDueSubscriptionChangesAsync();
        db.ChangeTracker.Clear();

        var change = await db.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.Id == changeId);
        Assert.Equal(SubscriptionChangeStatus.Pending, change.Status);
    }

    [Fact]
    public async Task H12_H5_an_abandoned_convert_checkout_is_swept_after_48h()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, _, promoId, changeId) = await SeedPendingCheckoutAsync(
            db, changeAgeHours: 72, changeType: SubscriptionChangeType.Downgrade, effectiveNow: true);

        await NewService(db).ProcessDueSubscriptionChangesAsync();
        db.ChangeTracker.Clear();

        var change = await db.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.Id == changeId);
        Assert.Equal(SubscriptionChangeStatus.Cancelled, change.Status);
        var promo = await db.PromotionCodes.AsNoTracking().SingleAsync(p => p.Id == promoId);
        Assert.Equal(0, promo.RedemptionCount);
    }

    /// A checkout mid-flight: Pending billing + unpaid invoice + Pending change holding one
    /// claimed slot of a capped promo code.
    private static async Task<(int AgentId, int InvoiceId, int PromoId, int ChangeId)> SeedPendingCheckoutAsync(
        IPRODbContext db, int changeAgeHours,
        SubscriptionChangeType changeType = SubscriptionChangeType.Subscribe,
        bool effectiveNow = false)
    {
        var rule = new BillingRule { PackageName = $"BW-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        var promo = new PromotionCode
        {
            Code = $"BW{Guid.NewGuid():N}"[..10],
            MaxRedemptions = 5,
            RedemptionCount = 1,     // this checkout's claimed slot
            IsActive = true
        };
        db.Add(promo);
        await db.SaveChangesAsync();

        var agent = new AgentUser
        {
            UserName = $"bw-{Guid.NewGuid():N}"[..20],
            Email = $"bw-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Pending",
            LastName = "Checkout",
            DomainName = $"bw-{Guid.NewGuid():N}"[..24],
            Country = "Canada",
            Province = "Ontario",
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();

        var billing = new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id,
            BillingRuleId = rule.Id,
            Amount = 60m,
            Status = BillingStatus.Pending,
            Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow.AddHours(-changeAgeHours)
        };
        db.Add(billing);
        await db.SaveChangesAsync();

        var invoice = new Invoice
        {
            BillingId = billing.Id,
            AgentUserId = agent.Id,
            InvoiceNumber = $"BW-P-{billing.Id}",
            SubTotal = 60m,
            TaxRate = 0.13m,
            TaxAmount = 7.80m,
            Total = 67.80m,
            IssuedAt = DateTime.UtcNow.AddHours(-changeAgeHours),
            IsPaid = false
        };
        db.Add(invoice);
        var change = new SubscriptionChange
        {
            AgentUserId = agent.Id,
            RequestedBillingRuleId = rule.Id,
            BillingId = billing.Id,
            PromotionCodeId = promo.Id,
            ChangeType = changeType,
            Status = SubscriptionChangeStatus.Pending,
            EffectiveDate = effectiveNow ? DateTime.UtcNow : DateTime.UtcNow.AddDays(30),
            AmountDue = changeType == SubscriptionChangeType.Downgrade ? 0m : 67.80m,
            CreatedAt = DateTime.UtcNow.AddHours(-changeAgeHours)
        };
        db.Add(change);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (agent.Id, invoice.Id, promo.Id, change.Id);
    }

    // ----------------------------------------------------------- H6: the setup-fee waiver door --

    [Fact]
    public async Task H6_a_completed_convert_does_not_waive_the_setup_fee_on_a_later_resignup()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        // A convert-downgrade that already completed: its Applied row carries the credit it
        // converted (ProratedCredit > 0) and its own billing WAS the new subscription. The waiver
        // exists for agents finishing a SCHEDULED change we cancelled out from under them --
        // pre-fix it keyed on "any Applied Downgrade", so a voluntary re-signup months after a
        // finished convert got $150-$400 waived.
        var (agentId, targetRuleId) = await SeedAppliedDowngradeAsync(db, proratedCredit: 250m);

        await NewServiceWithPayPal(db).CreateSubscriptionAsync(
            agentId, targetRuleId, BillingPeriod.Monthly, "https://x/r", "https://x/c");
        db.ChangeTracker.Clear();

        var invoice = await LatestInvoiceAsync(db, agentId, targetRuleId);
        Assert.Equal(190m, invoice.SubTotal);   // $40 package + $150 setup fee, NOT waived
    }

    [Fact]
    public async Task H6_completing_a_scheduled_downgrade_still_waives_the_fee()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, targetRuleId) = await SeedAppliedDowngradeAsync(db, proratedCredit: 0m);

        await NewServiceWithPayPal(db).CreateSubscriptionAsync(
            agentId, targetRuleId, BillingPeriod.Monthly, "https://x/r", "https://x/c");
        db.ChangeTracker.Clear();

        var invoice = await LatestInvoiceAsync(db, agentId, targetRuleId);
        Assert.Equal(40m, invoice.SubTotal);    // completion of OUR scheduled change: fee waived
    }

    [Fact]
    public async Task H6_a_cancel_after_the_applied_downgrade_consumes_the_waiver()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        // The agent's scheduled downgrade applied, then they ANSWERED it -- by cancelling
        // outright. A re-signup after that is a voluntary re-entry, not a completion.
        var (agentId, targetRuleId) = await SeedAppliedDowngradeAsync(db, proratedCredit: 0m);
        db.Add(new SubscriptionChange
        {
            AgentUserId = agentId,
            RequestedBillingRuleId = targetRuleId,
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

        var invoice = await LatestInvoiceAsync(db, agentId, targetRuleId);
        Assert.Equal(190m, invoice.SubTotal);
    }

    private static async Task<(int AgentId, int TargetRuleId)> SeedAppliedDowngradeAsync(
        IPRODbContext db, decimal proratedCredit)
    {
        if (!await db.ProvinceTaxRates.AnyAsync(t => t.ProvinceCode == "ON"))
        {
            db.Add(new ProvinceTaxRate { ProvinceCode = "ON", ProvinceName = "Ontario", Rate = 0.13m, TaxLabel = "ON 13% HST", IsActive = true });
        }
        var target = new BillingRule
        {
            PackageName = $"BW-{Guid.NewGuid():N}"[..20],
            MonthlyPrice = 40m,
            AnnualPrice = 400m,
            SetupFee = 150m,
            IsActive = true,
            PayPalMonthlyPlanId = "P-H6-M",
            PayPalAnnualPlanId = "P-H6-A"
        };
        db.Add(target);
        await db.SaveChangesAsync();

        var agent = new AgentUser
        {
            UserName = $"bw-{Guid.NewGuid():N}"[..20],
            Email = $"bw-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Waiver",
            LastName = "Door",
            DomainName = $"bw-{Guid.NewGuid():N}"[..24],
            Country = "Canada",
            Province = "Ontario",
            PackageId = target.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();

        db.Add(new SubscriptionChange
        {
            AgentUserId = agent.Id,
            RequestedBillingRuleId = target.Id,
            ChangeType = SubscriptionChangeType.Downgrade,
            Status = SubscriptionChangeStatus.Applied,
            ProratedCredit = proratedCredit,
            EffectiveDate = DateTime.UtcNow.AddDays(-1),
            AppliedAt = DateTime.UtcNow.AddDays(-1),
            CreatedAt = DateTime.UtcNow.AddDays(-2)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (agent.Id, target.Id);
    }

    private static async Task<Invoice> LatestInvoiceAsync(IPRODbContext db, int agentId, int ruleId)
    {
        var billing = await db.Billings.AsNoTracking()
            .Where(b => b.AgentUserId == agentId && b.BillingRuleId == ruleId)
            .OrderByDescending(b => b.Id)
            .FirstOrDefaultAsync();
        Assert.NotNull(billing);
        var invoice = await db.Invoices.AsNoTracking()
            .Where(i => i.BillingId == billing!.Id)
            .OrderByDescending(i => i.Id)
            .FirstOrDefaultAsync();
        Assert.NotNull(invoice);
        return invoice!;
    }

    private static PayPalBillingService NewServiceWithPayPal(IPRODbContext db) => new(
        new UnitOfWork(db),
        db,
        new OfflineHttpClientFactory(),
        new StubEmailService(),
        Options.Create(new PayPalSettings { ClientId = "test-client", ClientSecret = "test-secret" }),
        new ConfigurationBuilder().Build(),
        NullLogger<PayPalBillingService>.Instance);

    /// Settings present (so the checkout row IS written) but every HTTP call fails instantly and
    /// offline -- no dependency on PayPal's sandbox being reachable from a test run.
    private sealed class OfflineHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new RefuseHandler());

        private sealed class RefuseHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
                => throw new HttpRequestException("offline test handler");
        }
    }

    // ------------------------------------------- M7 / M16 / M19: batch survival and dunning --

    [Fact]
    public async Task M7_one_agents_poisoned_save_does_not_take_down_the_rest_of_the_batch()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        // Two agents, each with a due scheduled downgrade ready to apply.
        var seedA = await SeedDueScheduledDowngradeAsync(db);
        var seedB = await SeedDueScheduledDowngradeAsync(db);

        var service = NewService(db);

        // Poison the SHARED change tracker the way a mid-batch DbUpdateException does: a tracked,
        // unsaved row that violates a foreign key. It rides the first agent's SaveChangesAsync and
        // throws; pre-fix the per-agent catch continued WITHOUT clearing, so every following
        // agent's save failed identically and the whole hour applied nothing.
        db.Add(new SubscriptionChange
        {
            AgentUserId = seedA.AgentId,
            RequestedBillingRuleId = 999_999_999,   // no such BillingRule
            ChangeType = SubscriptionChangeType.Subscribe,
            Status = SubscriptionChangeStatus.Cancelled,
            EffectiveDate = DateTime.UtcNow
        });

        await service.ProcessDueSubscriptionChangesAsync();
        db.ChangeTracker.Clear();

        var applied = await db.SubscriptionChanges.AsNoTracking().CountAsync(c =>
            (c.AgentUserId == seedA.AgentId || c.AgentUserId == seedB.AgentId) &&
            c.ChangeType == SubscriptionChangeType.Downgrade &&
            c.Status == SubscriptionChangeStatus.Applied);
        // Whichever agent ran first ate the poison and stays Pending for the next hourly retry;
        // the OTHER one must still apply. Pre-fix: zero applied.
        Assert.Equal(1, applied);
    }

    [Fact]
    public async Task M16_a_completed_convert_followed_by_a_cancel_is_not_dunned_to_complete_anything()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        // A convert-downgrade that completed (its Applied row carries credit; its billing predates
        // AppliedAt, as every convert's does), which the agent later cancelled -- keeping their
        // paid-through time. There is nothing left to "complete"; pre-fix the day-3 reminder fired
        // anyway because the acted-on check only reads Billing rows, and the convert's own billing
        // was created BEFORE the change applied.
        var now = DateTime.UtcNow;
        var seed = await SeedBillingAsync(db,
            startDate: now.AddDays(-10),
            nextBillingDate: now.AddDays(20),
            invoiceIssuedAt: now.AddDays(-10),
            invoiceTxn: "TXN-M16-1",
            period: BillingPeriod.Monthly,
            amount: 40m);
        var billing = await db.Billings.SingleAsync(b => b.Id == seed.BillingId);
        billing.Status = BillingStatus.Cancelled;
        billing.CancelledAt = now.AddDays(-3);
        billing.PaidThroughAt = now.AddDays(20);
        billing.CreatedAt = now.AddDays(-10);
        db.Add(new SubscriptionChange
        {
            AgentUserId = seed.AgentId,
            RequestedBillingRuleId = seed.RuleId,
            BillingId = seed.BillingId,
            ChangeType = SubscriptionChangeType.Downgrade,
            Status = SubscriptionChangeStatus.Applied,
            ProratedCredit = 250m,                      // convert shape
            EffectiveDate = now.AddDays(-4),
            AppliedAt = now.AddDays(-4),
            CreatedAt = now.AddDays(-10)
        });
        db.Add(new SubscriptionChange
        {
            AgentUserId = seed.AgentId,
            RequestedBillingRuleId = seed.RuleId,
            ChangeType = SubscriptionChangeType.Cancel,
            Status = SubscriptionChangeStatus.Applied,
            EffectiveDate = now.AddDays(-3),
            AppliedAt = now.AddDays(-3),
            CreatedAt = now.AddDays(-3)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await NewService(db).NotifyBillingIssuesAsync();
        db.ChangeTracker.Clear();

        Assert.False(await db.OperateLogs.AsNoTracking().AnyAsync(l =>
            l.AgentUserId == seed.AgentId && l.Action == "DowngradeCompletionReminder"));
    }

    [Fact]
    public async Task M16_a_scheduled_downgrade_left_unanswered_is_still_dunned()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var rule = new BillingRule { PackageName = $"BW-{Guid.NewGuid():N}"[..20], MonthlyPrice = 40m, AnnualPrice = 400m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"bw-{Guid.NewGuid():N}"[..20],
            Email = $"bw-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Still", LastName = "Dunned",
            DomainName = $"bw-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario", PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        db.Add(new SubscriptionChange
        {
            AgentUserId = agent.Id,
            RequestedBillingRuleId = rule.Id,
            ChangeType = SubscriptionChangeType.Downgrade,
            Status = SubscriptionChangeStatus.Applied,
            ProratedCredit = 0m,                        // scheduled shape
            EffectiveDate = DateTime.UtcNow.AddDays(-4),
            AppliedAt = DateTime.UtcNow.AddDays(-4),
            CreatedAt = DateTime.UtcNow.AddDays(-10)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await NewService(db).NotifyBillingIssuesAsync();
        db.ChangeTracker.Clear();

        Assert.True(await db.OperateLogs.AsNoTracking().AnyAsync(l =>
            l.AgentUserId == agent.Id && l.Action == "DowngradeCompletionReminder"));
    }

    [Fact]
    public async Task M19_one_agents_failing_email_does_not_starve_the_rest_of_the_dunning_run()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var agentIds = new List<int>();
        foreach (var marker in new[] { "poison", "healthy" })
        {
            var rule = new BillingRule { PackageName = $"BW-{Guid.NewGuid():N}"[..20], MonthlyPrice = 40m, AnnualPrice = 400m };
            db.Add(rule);
            await db.SaveChangesAsync();
            var agent = new AgentUser
            {
                UserName = $"bw-{Guid.NewGuid():N}"[..20],
                Email = $"{marker}-{Guid.NewGuid():N}"[..16] + "@example.test",
                FirstName = marker, LastName = "Isolation",
                DomainName = $"bw-{Guid.NewGuid():N}"[..24],
                Country = "Canada", Province = "Ontario", PackageId = rule.Id
            };
            db.Add(agent);
            await db.SaveChangesAsync();
            db.Add(new SubscriptionChange
            {
                AgentUserId = agent.Id,
                RequestedBillingRuleId = rule.Id,
                ChangeType = SubscriptionChangeType.Downgrade,
                Status = SubscriptionChangeStatus.Applied,
                ProratedCredit = 0m,
                EffectiveDate = DateTime.UtcNow.AddDays(-4),
                AppliedAt = DateTime.UtcNow.AddDays(-4),
                CreatedAt = DateTime.UtcNow.AddDays(-10)
            });
            await db.SaveChangesAsync();
            agentIds.Add(agent.Id);
        }
        db.ChangeTracker.Clear();

        // The first agent's email THROWS (not "returns false" -- throws, like a serializer or
        // template bug would). Pre-fix that exception aborted the whole loop and the job.
        var service = new PayPalBillingService(
            new UnitOfWork(db), db, new StubHttpClientFactory(),
            new ThrowingEmailService(emailPrefix: "poison"),
            Options.Create(new PayPalSettings()),
            new ConfigurationBuilder().Build(),
            NullLogger<PayPalBillingService>.Instance);

        var ex = await Record.ExceptionAsync(() => service.NotifyBillingIssuesAsync());
        Assert.Null(ex);
        db.ChangeTracker.Clear();

        Assert.True(await db.OperateLogs.AsNoTracking().AnyAsync(l =>
            l.AgentUserId == agentIds[1] && l.Action == "DowngradeCompletionReminder"));
        Assert.False(await db.OperateLogs.AsNoTracking().AnyAsync(l =>
            l.AgentUserId == agentIds[0] && l.Action == "DowngradeCompletionReminder"));
    }

    private sealed record DueSeed(int AgentId, int BillingId);

    private static async Task<DueSeed> SeedDueScheduledDowngradeAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = $"BW-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"bw-{Guid.NewGuid():N}"[..20],
            Email = $"bw-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Due", LastName = "Downgrade",
            DomainName = $"bw-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario", PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var billing = new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id,
            BillingRuleId = rule.Id,
            PayPalSubscriptionId = string.Empty,      // no-PayPal cancel is a documented clean no-op
            Amount = 60m,
            Status = BillingStatus.Active,
            Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddDays(-30),
            NextBillingDate = DateTime.UtcNow.AddHours(2)
        };
        db.Add(billing);
        await db.SaveChangesAsync();
        db.Add(new SubscriptionChange
        {
            AgentUserId = agent.Id,
            CurrentBillingRuleId = rule.Id,
            RequestedBillingRuleId = rule.Id,
            BillingId = billing.Id,
            ChangeType = SubscriptionChangeType.Downgrade,
            Status = SubscriptionChangeStatus.Pending,
            EffectiveDate = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow.AddDays(-7)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new DueSeed(agent.Id, billing.Id);
    }

    private sealed class ThrowingEmailService : IPRO.Email.IEmailService
    {
        private readonly string _emailPrefix;
        public ThrowingEmailService(string emailPrefix) => _emailPrefix = emailPrefix;
        private void MaybeThrow(string to)
        {
            if (to.StartsWith(_emailPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"simulated email failure for {to}");
        }
        public Task<bool> SendAsync(string to, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null)
        { MaybeThrow(to); return Task.FromResult(true); }
        public Task<IPRO.Email.EmailSendResult> SendDetailedAsync(string to, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null)
        { MaybeThrow(to); return Task.FromResult(IPRO.Email.EmailSendResult.Sent()); }
        public Task<bool> SendBulkAsync(IEnumerable<IPRO.Email.EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
    }

    // -------------------------------------------------- H15 / New A: promises that match code --

    [Fact]
    public async Task H15_the_cancel_message_does_not_promise_an_automatic_refund_the_process_is_manual()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        var seed = await SeedBillingAsync(db,
            startDate: now.AddDays(-135),
            nextBillingDate: now.AddDays(-135).AddYears(1),
            invoiceIssuedAt: now.AddDays(-135),
            invoiceTxn: "TXN-H15-1");

        var msg = await CancelViaControllerAsync(db, seed.AgentId);

        // $339 is genuinely owed and the queue row exists -- but no code moves money, so the old
        // unconditional "will be sent to your PayPal account within a few business days" was a
        // promise about an entirely manual process.
        Assert.Contains("339", msg);
        Assert.DoesNotContain("will be sent to your PayPal account within a few business days", msg);
        Assert.Contains("manually", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task H15_a_refund_past_paypals_window_says_we_will_arrange_it_not_business_days()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        // Month 8: refund still due ($120 net), but the payment is ~240 days old -- PayPal's
        // ~180-day portal-refund window has CLOSED and the queue shows EXPIRED.
        var seed = await SeedBillingAsync(db,
            startDate: now.AddDays(-240),
            nextBillingDate: now.AddDays(-240).AddYears(1),
            invoiceIssuedAt: now.AddDays(-240),
            invoiceTxn: "TXN-H15-2");

        var msg = await CancelViaControllerAsync(db, seed.AgentId);

        Assert.DoesNotContain("within a few business days", msg);
        Assert.Contains("contact", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NewA_the_scheduled_downgrade_message_discloses_the_access_interruption()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        var seed = await SeedBillingAsync(db,
            startDate: now.AddDays(-10),
            nextBillingDate: now.AddDays(20),
            invoiceIssuedAt: now.AddDays(-10),
            invoiceTxn: "TXN-NEWA-1",
            period: BillingPeriod.Monthly,
            amount: 60m);
        var cheaper = new BillingRule
        {
            PackageName = $"BW-{Guid.NewGuid():N}"[..20],
            MonthlyPrice = 40m, AnnualPrice = 400m, IsActive = true,
            PayPalMonthlyPlanId = "P-NEWA-M", PayPalAnnualPlanId = "P-NEWA-A"
        };
        db.Add(cheaper);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await NewService(db).CreateSubscriptionAsync(
            seed.AgentId, cheaper.Id, BillingPeriod.Monthly, "https://x/r", "https://x/c");

        Assert.True(result.Success);
        // Pre-fix the message said only "scheduled for <date>" -- the agent learned about the
        // subscription ending, the PayPal re-approval, and the access pause from an email sent
        // AFTER it happened (or never, if delivery failed).
        Assert.Contains("PayPal approval", result.Message);
        Assert.Contains("pause", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NewA_the_billing_page_banner_discloses_the_access_interruption_too()
    {
        var view = System.IO.File.ReadAllText(FindBillingViewPath());
        // The persistent banner is what the agent re-reads days later; it must carry the same
        // disclosure as the one-time message, anchored inside the scheduled-change branch.
        Assert.Contains("PayPal approval", view);
    }

    private static async Task<string> CancelViaControllerAsync(IPRODbContext db, int agentId)
    {
        var controller = new IPRO.Web.Controllers.BillingController(
            NewService(db),
            new UnitOfWork(db),
            new ConfigurationBuilder().Build(),
            new IPRO.Business.Services.PackageEntitlementService(new UnitOfWork(db), db),
            db,
            NullLogger<IPRO.Web.Controllers.BillingController>.Instance);
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NullTempData());

        await controller.CancelSubscription();
        db.ChangeTracker.Clear();
        var msg = controller.TempData["Success"] as string;
        Assert.NotNull(msg);
        return msg!;
    }

    private sealed class NullTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(Microsoft.AspNetCore.Http.HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(Microsoft.AspNetCore.Http.HttpContext context, IDictionary<string, object> values) { }
    }

    private static string FindBillingViewPath()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return System.IO.Path.Combine(dir!.FullName, "src", "IPRO.Web", "Views", "Billing", "Index.cshtml");
    }

    // ------------------------------------------------------------------------------ plumbing --

    private sealed record Seed(int AgentId, int BillingId, int RuleId);

    private static async Task<Seed> SeedBillingAsync(
        IPRODbContext db,
        DateTime startDate,
        DateTime nextBillingDate,
        DateTime invoiceIssuedAt,
        string invoiceTxn,
        BillingPeriod period = BillingPeriod.Annually,
        decimal amount = 600m,
        string payPalSubscriptionId = "")
    {
        if (!await db.ProvinceTaxRates.AnyAsync(t => t.ProvinceCode == "ON"))
        {
            db.Add(new ProvinceTaxRate { ProvinceCode = "ON", ProvinceName = "Ontario", Rate = 0.13m, TaxLabel = "ON 13% HST", IsActive = true });
        }
        var rule = new BillingRule { PackageName = $"BW-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        await db.SaveChangesAsync();
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = "bw_feature", FeatureName = "BW", IsIncluded = true });

        var agent = new AgentUser
        {
            UserName = $"bw-{Guid.NewGuid():N}"[..20],
            Email = $"bw-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Billing",
            LastName = "Wave",
            DomainName = $"bw-{Guid.NewGuid():N}"[..24],
            Country = "Canada",
            Province = "Ontario",
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();

        var billing = new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id,
            BillingRuleId = rule.Id,
            PayPalSubscriptionId = payPalSubscriptionId,
            PayPalPlanId = string.Empty,
            Amount = amount,
            Currency = "CAD",
            Status = BillingStatus.Active,
            Period = period,
            StartDate = startDate,
            NextBillingDate = nextBillingDate
        };
        db.Add(billing);
        await db.SaveChangesAsync();

        db.Add(new Invoice
        {
            BillingId = billing.Id,
            AgentUserId = agent.Id,
            InvoiceNumber = $"BW-{billing.Id}",
            SubTotal = amount,
            TaxRate = 0.13m,
            TaxAmount = Math.Round(amount * 0.13m, 2),
            Total = Math.Round(amount * 1.13m, 2),
            PayPalTransactionId = invoiceTxn,
            IssuedAt = invoiceIssuedAt,
            IsPaid = true
        });
        await db.SaveChangesAsync();
        return new Seed(agent.Id, billing.Id, rule.Id);
    }

    private static PayPalBillingService NewService(IPRODbContext db) => new(
        new UnitOfWork(db),
        db,
        new StubHttpClientFactory(),
        new StubEmailService(),
        Options.Create(new PayPalSettings()),
        new ConfigurationBuilder().Build(),
        NullLogger<PayPalBillingService>.Instance);

    private static void AssertClose(DateTime expected, DateTime? actual)
    {
        Assert.NotNull(actual);
        Assert.True(Math.Abs((expected - actual!.Value).TotalMinutes) < 5,
            $"expected ~{expected:O} but was {actual:O}");
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StubEmailService : IPRO.Email.IEmailService
    {
        public Task<bool> SendAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(true);
        public Task<IPRO.Email.EmailSendResult> SendDetailedAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(IPRO.Email.EmailSendResult.Sent());
        public Task<bool> SendBulkAsync(IEnumerable<IPRO.Email.EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
    }
}
