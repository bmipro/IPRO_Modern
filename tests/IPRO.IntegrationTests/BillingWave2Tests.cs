using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using IPRO.Billing;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Scheduler;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// Billing wave 2 (2026-08-25) — fixes for the four-auditor billing audit's confirmed findings.
// Same discipline as waves 1 and the billing wave: every test here was run against the pre-fix
// code and observed to FAIL (or the fix reverted and the green observed to go red).
public class BillingWave2Tests
{
    // ---------------------------------------------------- C: drip batch survives a tracker clear --

    [Fact]
    public async Task C_a_mid_batch_tracker_clear_never_lets_a_step_send_without_its_advance_persisting()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        // Campaign with one step; two due enrollments — the POISON one ordered first (earlier
        // NextSendAt), the HEALTHY one second.
        var rule = new BillingRule { PackageName = $"W2-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"w2-{Guid.NewGuid():N}"[..20],
            Email = "w2.agent@example.test",
            FirstName = "W2", LastName = "Agent",
            DomainName = $"w2-{Guid.NewGuid():N}"[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var campaign = new DripCampaign { AgentUserId = agent.Id, Name = "W2 series", IsActive = true };
        db.Add(campaign);
        await db.SaveChangesAsync();
        db.Add(new DripCampaignStep { DripCampaignId = campaign.Id, SortOrder = 0, Subject = "Step 1", HtmlBody = "<p>hi</p>", DelayDays = 7 });
        await db.SaveChangesAsync();

        var poisonClient = new Client { AgentUserId = agent.Id, FirstName = "Poison", LastName = "First", Email = $"poison.{Guid.NewGuid():N}"[..20] + "@example.test", IsNewsletterSubscribed = true };
        var healthyClient = new Client { AgentUserId = agent.Id, FirstName = "Healthy", LastName = "Second", Email = $"healthy.{Guid.NewGuid():N}"[..20] + "@example.test", IsNewsletterSubscribed = true };
        db.AddRange(poisonClient, healthyClient);
        await db.SaveChangesAsync();
        var poisonEnrollment = new DripCampaignEnrollment
        {
            AgentUserId = agent.Id, DripCampaignId = campaign.Id, ClientId = poisonClient.Id,
            Status = DripCampaignEnrollmentStatus.Active, NextStepIndex = 0,
            StartedAt = DateTime.UtcNow, NextSendAt = DateTime.UtcNow.AddHours(-2),
            UnsubscribeToken = Guid.NewGuid().ToString("N")
        };
        var healthyEnrollment = new DripCampaignEnrollment
        {
            AgentUserId = agent.Id, DripCampaignId = campaign.Id, ClientId = healthyClient.Id,
            Status = DripCampaignEnrollmentStatus.Active, NextStepIndex = 0,
            StartedAt = DateTime.UtcNow, NextSendAt = DateTime.UtcNow.AddHours(-1),
            UnsubscribeToken = Guid.NewGuid().ToString("N")
        };
        db.AddRange(poisonEnrollment, healthyEnrollment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Poison the shared tracker the way a mid-batch DbUpdateException does: a tracked, unsaved
        // FK-violating row. It rides the FIRST save the poison enrollment's processing performs and
        // throws; the job's failure bookkeeping save then also throws, triggering the H13 tracker
        // clear. Pre-fix the loop then CONTINUED over the now-DETACHED healthy enrollment: its
        // email really went out, but its NextStepIndex++ no longer persisted — the identical step
        // re-sent every following hour.
        db.Add(new SubscriptionChange
        {
            AgentUserId = agent.Id,
            RequestedBillingRuleId = 999_999_999,   // no such BillingRule
            ChangeType = SubscriptionChangeType.Subscribe,
            Status = SubscriptionChangeStatus.Cancelled,
            EffectiveDate = DateTime.UtcNow
        });

        var email = new RecordingEmailService();
        var job = new DripCampaignJob(
            new UnitOfWork(db), db,
            new NewsLetterDispatcher(new UnitOfWork(db), db, email, new ConfigurationBuilder().Build(), NullLogger<NewsLetterDispatcher>.Instance),
            new NoConsentSweep(), NullLogger<DripCampaignJob>.Instance);

        await job.RunAsync();
        db.ChangeTracker.Clear();

        // THE INVARIANT: a delivered step implies a persisted advance. Pre-fix: 1 email to the
        // healthy client with NextStepIndex still 0 (send-without-advance = duplicate next tick).
        // Post-fix (batch breaks after the clear): 0 emails, index 0 — it simply runs next tick.
        var healthyAfter = await db.DripCampaignEnrollments.AsNoTracking().SingleAsync(e => e.Id == healthyEnrollment.Id);
        var sentToHealthy = email.SentTo.Count(a => a == healthyClient.Email);
        Assert.Equal(sentToHealthy, healthyAfter.NextStepIndex);
    }

    // ------------------------------- A: refunds derive from money actually settled on the row --

    [Fact]
    public async Task A_cancelling_a_never_billed_deferred_start_row_keeps_the_credit_and_refunds_nothing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        // A convert-downgrade's new billing during its credit window: annual period, first charge
        // ~14 months out, and the only settled invoice is the $0 conversion invoice. Not a cent
        // has been collected on this row.
        var now = DateTime.UtcNow;
        var seed = await SeedAnnualAsync(db, amount: 300m, nextBillingDate: now.AddDays(427),
            invoices: new[] { (0m, now.AddDays(-30), "I-CONVERTSUB") });

        Assert.True(await NewService(db).CancelSubscriptionAsync(seed.AgentId));
        db.ChangeTracker.Clear();

        var billing = await db.Billings.AsNoTracking().SingleAsync(b => b.Id == seed.BillingId);
        var change = await db.SubscriptionChanges.AsNoTracking()
            .SingleAsync(c => c.AgentUserId == seed.AgentId && c.ChangeType == SubscriptionChangeType.Cancel);

        // Pre-fix: Amount(300)/10 clawback against a FUTURE cycle start clamped to month 1 →
        // $270 phantom refund queued against the $0 invoice, and PaidThroughAt slashed from
        // now+427d to now+~94d — ~11 months of already-paid-for credit destroyed.
        Assert.Equal(0m, change.RefundGrossAmount);
        Assert.Equal(RefundStatus.None, change.RefundStatus);
        AssertClose(now.AddDays(427).AddDays(PrepaidValue.CancellationGraceDays), billing.PaidThroughAt);
    }

    [Fact]
    public async Task A_the_refund_never_exceeds_what_was_captured_on_the_row()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        // Annual→annual upgrade: the Platinum row's Amount is $1,200 but the only money PayPal
        // ever captured on it is the $500 proration difference. Cancel in month 4.
        var now = DateTime.UtcNow;
        var cycleAnchor = now.AddMonths(8).AddDays(10);
        var seed = await SeedAnnualAsync(db, amount: 1200m, nextBillingDate: cycleAnchor,
            invoices: new[] { (500m, cycleAnchor.AddYears(-1), "TXN-PRORATION") });

        Assert.True(await NewService(db).CancelSubscriptionAsync(seed.AgentId));
        db.ChangeTracker.Clear();

        var change = await db.SubscriptionChanges.AsNoTracking()
            .SingleAsync(c => c.AgentUserId == seed.AgentId && c.ChangeType == SubscriptionChangeType.Cancel);

        // POLICY REVISION (owner decision 2026-08-25, wave 3): unused value $1,200 - 4 x $120 =
        // $720, capped at everything settled this cycle across the agent's rows -- here only this
        // row's $500 exists, so $500 + $65 HST. The wave-2 interim assertion here was $20 (months
        // clawed against the capture); the owner chose the agent-favouring rule instead. The
        // original defect stays pinned by the cap: pre-wave-2 code instructed $813.60 gross
        // against a $565.00 capture, which PayPal cannot execute.
        Assert.Equal(500.00m, change.RefundNetAmount);
        Assert.Equal(65.00m, change.RefundTaxAmount);
        Assert.True(change.RefundGrossAmount <= 565m,
            $"refund {change.RefundGrossAmount} must never exceed the referenced capture");
        Assert.Equal("TXN-PRORATION", change.RefundPayPalTransactionId);
    }

    [Fact]
    public async Task A_a_first_year_invoice_with_the_setup_fee_does_not_inflate_the_refund_base()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        // Regression pin: the first-year invoice's SubTotal includes the $150 setup fee. The base
        // must clamp to Amount, keeping the plain-annual refund identical to the billing wave's.
        var now = DateTime.UtcNow;
        var seed = await SeedAnnualAsync(db, amount: 600m, nextBillingDate: now.AddDays(-135).AddYears(1),
            invoices: new[] { (750m, now.AddDays(-135), "TXN-FIRSTYEAR") });

        Assert.True(await NewService(db).CancelSubscriptionAsync(seed.AgentId));
        db.ChangeTracker.Clear();

        var change = await db.SubscriptionChanges.AsNoTracking()
            .SingleAsync(c => c.AgentUserId == seed.AgentId && c.ChangeType == SubscriptionChangeType.Cancel);
        Assert.Equal(300.00m, change.RefundNetAmount);   // month 5: 600 - 5x60, NOT 750-based
    }

    // -------------------------------------- D: the cancellation outcome is minted exactly once --

    [Fact]
    public async Task D_two_racing_doors_mint_exactly_one_cancellation_outcome()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await using var racingDb = testDb.CreateContext();

        var now = DateTime.UtcNow;
        var seed = await SeedAnnualAsync(db, amount: 600m, nextBillingDate: now.AddDays(-135).AddYears(1),
            invoices: new[] { (600m, now.AddDays(-135), "TXN-RACE") });

        // The race, made deterministic with two real DbContexts (two doors): door 1 loads the row
        // while it is still Active — exactly what the hourly reconcile does minutes before acting,
        // or what the self-cancel holds when its own CANCELLED webhook lands first. Door 2 then
        // completes a full legitimate cancellation. Door 1 proceeds on its stale view.
        var doorOne = NewService(db);
        var stale = await db.Billings.SingleAsync(b => b.Id == seed.BillingId);
        Assert.Equal(BillingStatus.Active, stale.Status);

        Assert.True(await NewService(racingDb).CancelSubscriptionAsync(seed.AgentId));   // door 2 wins

        await doorOne.ApplyCancellationOutcomeAsync(stale, BillingStatus.Cancelled, now.AddDays(230), "race test");
        db.ChangeTracker.Clear();

        // Pre-fix both doors minted: two identical Pending rows in the manual refund queue, one
        // plausible double refund. The fence (a PK'd claim row committed WITH the outcome) lets
        // exactly one through and rolls the loser back whole.
        Assert.Equal(1, await db.SubscriptionChanges.AsNoTracking()
            .CountAsync(c => c.AgentUserId == seed.AgentId && c.ChangeType == SubscriptionChangeType.Cancel));
    }

    [Fact]
    public async Task TxnRef_a_comma_joined_failed_marker_list_stores_only_the_settling_transaction()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        // The failed-payment marker invoice appends every retry's transaction id; the settled one
        // is last. RefundPayPalTransactionId is varchar(64): storing the whole list overflows the
        // column (aborting the caller under strict mode) and points the refund queue at a FAILED
        // transaction. Only the settling id may be stored.
        var now = DateTime.UtcNow;
        var joined = "PAYPAL_FAILED:TX-FAIL-1,TX-FAIL-2,TX-SETTLED-9";
        var seed = await SeedAnnualAsync(db, amount: 600m, nextBillingDate: now.AddDays(-135).AddYears(1),
            invoices: new[] { (600m, now.AddDays(-135), joined) });

        Assert.True(await NewService(db).CancelSubscriptionAsync(seed.AgentId));
        db.ChangeTracker.Clear();

        var change = await db.SubscriptionChanges.AsNoTracking()
            .SingleAsync(c => c.AgentUserId == seed.AgentId && c.ChangeType == SubscriptionChangeType.Cancel);
        Assert.Equal("TX-SETTLED-9", change.RefundPayPalTransactionId);
        Assert.True(change.RefundPayPalTransactionId.Length <= 64);
    }

    // ------------------------------------------- B: the sweep consults PayPal before voiding --

    [Fact]
    public async Task B_sweep_leaves_paypal_backed_checkouts_alone_when_paypal_cannot_be_reached()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, changeId, _) = await SeedStaleCheckoutAsync(db, payPalSubscriptionId: "I-B1UNREACH");

        // No PayPal settings -> the snapshot cannot answer. Fail-safe: a checkout PayPal might
        // still honor is NOT voided blind; it waits for an hour when PayPal can be asked.
        await NewService(db).ProcessDueSubscriptionChangesAsync();
        db.ChangeTracker.Clear();

        var change = await db.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.Id == changeId);
        Assert.Equal(SubscriptionChangeStatus.Pending, change.Status);
    }

    [Fact]
    public async Task B_sweep_cancels_an_approval_pending_subscription_at_paypal_before_voiding()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, changeId, _) = await SeedStaleCheckoutAsync(db, payPalSubscriptionId: "I-B2PENDING");

        var paypal = new ScriptedPayPal();
        paypal.Subscriptions["I-B2PENDING"] = ("APPROVAL_PENDING", null);
        await NewServiceScripted(db, paypal).ProcessDueSubscriptionChangesAsync();
        db.ChangeTracker.Clear();

        // Pre-fix the sweep voided locally and LEFT THE APPROVAL LINK LIVE: an agent approving on
        // day 3 was charged at activation and then auto-cancelled with no refund trail.
        var change = await db.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.Id == changeId);
        Assert.Equal(SubscriptionChangeStatus.Cancelled, change.Status);
        Assert.Contains("I-B2PENDING", paypal.CancelledSubscriptions);
    }

    [Fact]
    public async Task B_sweep_never_voids_a_checkout_paypal_reports_active()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, changeId, _) = await SeedStaleCheckoutAsync(db, payPalSubscriptionId: "I-B3ACTIVE");

        var paypal = new ScriptedPayPal();
        paypal.Subscriptions["I-B3ACTIVE"] = ("ACTIVE", DateTime.UtcNow.AddDays(20));
        await NewServiceScripted(db, paypal).ProcessDueSubscriptionChangesAsync();
        db.ChangeTracker.Clear();

        // ACTIVE at PayPal means the agent COMPLETED this checkout and our activation was lost --
        // voiding it locally guarantees "PayPal bills forever against a Cancelled row".
        var change = await db.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.Id == changeId);
        Assert.Equal(SubscriptionChangeStatus.Pending, change.Status);
        Assert.Empty(paypal.CancelledSubscriptions);
    }

    [Fact]
    public async Task B_a_payment_captured_on_an_ended_subscription_lands_in_the_refund_queue()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        var seed = await SeedAnnualAsync(db, amount: 60m, nextBillingDate: now.AddDays(20),
            invoices: Array.Empty<(decimal, DateTime, string)>(), payPalSubscriptionId: "I-B4DEAD",
            period: BillingPeriod.Monthly, status: BillingStatus.Cancelled);

        // PayPal captures a cycle against a subscription we consider ended (swept checkout whose
        // link was approved late, lost-cancel row, resurrect-guard cases). Pre-fix: an error log
        // and a paid invoice -- nobody ever refunds money that only a log line knows about.
        Assert.True(await NewService(db).HandleSubscriptionPaymentCompletedWebhookAsync("I-B4DEAD", "TX-B4", 67.80m));
        db.ChangeTracker.Clear();

        var row = await db.SubscriptionChanges.AsNoTracking()
            .SingleAsync(c => c.AgentUserId == seed.AgentId && c.ChangeType == SubscriptionChangeType.Cancel);
        Assert.Equal(RefundStatus.Pending, row.RefundStatus);
        Assert.Equal(67.80m, row.RefundGrossAmount);
        Assert.Equal("TX-B4", row.RefundPayPalTransactionId);
        // AppliedAt stays NULL on purpose: this row is a money-recovery marker, not an agent
        // action -- the H6 waiver consumption and M16 dunning suppression both key on AppliedAt.
        Assert.Null(row.AppliedAt);

        // A replayed delivery must not mint a second one (the txn-id replay guard owns this).
        Assert.True(await NewService(db).HandleSubscriptionPaymentCompletedWebhookAsync("I-B4DEAD", "TX-B4", 67.80m));
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.SubscriptionChanges.AsNoTracking()
            .CountAsync(c => c.AgentUserId == seed.AgentId && c.ChangeType == SubscriptionChangeType.Cancel));
    }

    [Fact]
    public async Task B_a_stale_approval_points_at_the_refund_queue_instead_of_promising_no_charge()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        var seed = await SeedAnnualAsync(db, amount: 60m, nextBillingDate: now.AddDays(20),
            invoices: Array.Empty<(decimal, DateTime, string)>(), payPalSubscriptionId: "I-B5STALE",
            period: BillingPeriod.Monthly, status: BillingStatus.Cancelled);
        // The stale-approval guard is reached via the invoice-first lookup (deliberately status-
        // unfiltered): the superseded checkout's unpaid invoice still carries the approval id.
        db.Add(new Invoice
        {
            BillingId = seed.BillingId,
            AgentUserId = seed.AgentId,
            InvoiceNumber = $"W2-B5-{seed.BillingId}",
            SubTotal = 60m, TaxRate = 0.13m, TaxAmount = 7.80m, Total = 67.80m,
            PayPalTransactionId = "I-B5STALE",
            IssuedAt = now.AddDays(-3),
            IsPaid = false
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await NewService(db).CapturePaymentAsync(seed.AgentId, "I-B5STALE");

        // Activation may already have captured the setup fee and first cycle -- "you will not be
        // charged for it" was a promise the code could not keep. The truthful version points at
        // the refund flagging the sale handler now performs.
        Assert.False(result.Success);
        Assert.DoesNotContain("you will not be charged", result.Message);
        Assert.Contains("refund", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ----------------------------------- M5 reconcile leg + per-row isolation (audit caveats) --

    [Fact]
    public async Task M5R_the_reconcile_door_applies_the_full_cancellation_outcome()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        var seed = await SeedAnnualAsync(db, amount: 600m, nextBillingDate: now.AddDays(-135).AddYears(1),
            invoices: new[] { (600m, now.AddDays(-135), "TXN-M5R") }, payPalSubscriptionId: "I-M5RECON");

        var paypal = new ScriptedPayPal();
        paypal.Subscriptions["I-M5RECON"] = ("CANCELLED", null);
        var corrected = await NewServiceScripted(db, paypal).ReconcileActiveSubscriptionsWithPayPalAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(1, corrected);
        var billing = await db.Billings.AsNoTracking().SingleAsync(b => b.Id == seed.BillingId);
        Assert.Equal(BillingStatus.Cancelled, billing.Status);
        Assert.NotNull(billing.PaidThroughAt);
        var change = await db.SubscriptionChanges.AsNoTracking()
            .SingleAsync(c => c.AgentUserId == seed.AgentId && c.ChangeType == SubscriptionChangeType.Cancel);
        Assert.Equal(300.00m, change.RefundNetAmount);   // month 5: the DOCS/22 outcome, not a raw flip
    }

    [Fact]
    public async Task ISO_one_poisoned_row_does_not_abort_the_rest_of_the_reconcile_sweep()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        var first = await SeedAnnualAsync(db, amount: 600m, nextBillingDate: now.AddDays(-135).AddYears(1),
            invoices: new[] { (600m, now.AddDays(-135), "TXN-ISO1") }, payPalSubscriptionId: "I-ISO-1");
        var second = await SeedAnnualAsync(db, amount: 600m, nextBillingDate: now.AddDays(-135).AddYears(1),
            invoices: new[] { (600m, now.AddDays(-135), "TXN-ISO2") }, payPalSubscriptionId: "I-ISO-2");

        var paypal = new ScriptedPayPal();
        paypal.Subscriptions["I-ISO-1"] = ("CANCELLED", null);
        paypal.Subscriptions["I-ISO-2"] = ("CANCELLED", null);

        // Poison the tracker so the FIRST row's outcome save throws (an FK-violating row rides
        // it). Pre-fix nothing isolated the outcome leg, so the whole hourly sweep aborted and
        // every remaining drifted subscription kept full access for another hour, indefinitely
        // while the same row kept failing first.
        db.Add(new SubscriptionChange
        {
            AgentUserId = first.AgentId,
            RequestedBillingRuleId = 999_999_999,
            ChangeType = SubscriptionChangeType.Subscribe,
            Status = SubscriptionChangeStatus.Cancelled,
            EffectiveDate = now
        });

        await NewServiceScripted(db, paypal).ReconcileActiveSubscriptionsWithPayPalAsync();
        db.ChangeTracker.Clear();

        var survivors = await db.Billings.AsNoTracking()
            .Where(b => b.Id == first.BillingId || b.Id == second.BillingId)
            .CountAsync(b => b.Status == BillingStatus.Cancelled);
        Assert.Equal(1, survivors);   // whichever ate the poison retries next hour; the other lands
    }

    // ------------------------------------------------- E / drift / slot / queue-copy cleanups --

    [Fact]
    public async Task E_an_expired_row_with_paid_through_time_is_honored_by_the_gates()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        var seed = await SeedAnnualAsync(db, amount: 600m, nextBillingDate: now.AddDays(20),
            invoices: new[] { (600m, now.AddDays(-345), "TXN-EXP") },
            status: BillingStatus.Expired, paidThroughAt: now.AddDays(22), featureCode: "w2_exp_feature");

        var entitlements = new IPRO.Business.Services.PackageEntitlementService(new UnitOfWork(db), db);
        // The webhook/reconcile doors write PaidThroughAt on Expired rows since the billing wave;
        // pre-fix every gate honored it on Cancelled only -- instant lockout on paid-up time.
        Assert.False(await entitlements.IsAccessGatedAsync(seed.AgentId));
        var bulk = await entitlements.HasAccessBulkAsync(new[] { seed.AgentId }, "w2_exp_feature");
        Assert.True(bulk[seed.AgentId]);

        var billing = await db.Billings.SingleAsync(b => b.Id == seed.BillingId);
        billing.PaidThroughAt = now.AddMinutes(-5);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.True(await new IPRO.Business.Services.PackageEntitlementService(new UnitOfWork(db), db).IsAccessGatedAsync(seed.AgentId));
    }

    [Fact]
    public void DRIFT_month_end_cycle_starts_count_calendar_months_not_clamped_cursor_hops()
    {
        // .NET clamps Jan 31 -> Feb 28 -> (cursor) Mar 28: the pre-fix cursor iteration drifted to
        // the 28th and counted Mar 29 as a THIRD month for a Jan 31 cycle -- under-refunding by a
        // month ($60 + tax on the worked example) for anyone whose annual started on the 29th-31st.
        var jan31 = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(2, PrepaidValue.MonthsUsedRoundingUp(jan31, new DateTime(2026, 3, 29, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(2, PrepaidValue.MonthsUsedRoundingUp(jan31, new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(3, PrepaidValue.MonthsUsedRoundingUp(jan31, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)));
        // The Jan-1 matrix the suite always used stays exact.
        var jan1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(5, PrepaidValue.MonthsUsedRoundingUp(jan1, jan1.AddMonths(4).AddDays(1)));
    }

    [Fact]
    public async Task SLOT_the_promo_slot_is_released_only_after_the_void_commits()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, changeId, promoId) = await SeedStaleCheckoutAsync(db, payPalSubscriptionId: "");

        // Poison the save: pre-fix the slot decrement (an immediate ExecuteUpdate) had already
        // committed when the void's SaveChangesAsync failed -- the still-Pending row was re-swept
        // next hour and decremented AGAIN, freeing capacity claimed by real checkouts.
        db.Add(new SubscriptionChange
        {
            AgentUserId = agentId,
            RequestedBillingRuleId = 999_999_999,
            ChangeType = SubscriptionChangeType.Subscribe,
            Status = SubscriptionChangeStatus.Cancelled,
            EffectiveDate = DateTime.UtcNow
        });

        await NewService(db).ProcessDueSubscriptionChangesAsync();
        db.ChangeTracker.Clear();

        var promo = await db.PromotionCodes.AsNoTracking().SingleAsync(p => p.Id == promoId);
        Assert.Equal(1, promo.RedemptionCount);   // nothing committed -> nothing released
        var change = await db.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.Id == changeId);
        Assert.Equal(SubscriptionChangeStatus.Pending, change.Status);   // retried next hour whole
    }

    [Fact]
    public async Task SLOT_cancel_checkout_also_releases_only_after_its_void_commits()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, changeId, promoId) = await SeedStaleCheckoutAsync(db, payPalSubscriptionId: "");
        var invoice = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.AgentUserId == agentId);
        // The stale seed has no invoice; the cancel-checkout path needs one to void.
        db.Add(new Invoice
        {
            BillingId = (await db.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.Id == changeId)).BillingId!.Value,
            AgentUserId = agentId,
            InvoiceNumber = $"W2-SL-{changeId}",
            SubTotal = 60m, TaxRate = 0.13m, TaxAmount = 7.80m, Total = 67.80m,
            IssuedAt = DateTime.UtcNow.AddHours(-1),
            IsPaid = false
        });
        await db.SaveChangesAsync();
        var invoiceId = (await db.Invoices.AsNoTracking().SingleAsync(i => i.AgentUserId == agentId && !i.IsPaid)).Id;
        db.ChangeTracker.Clear();

        // Poison the save: pre-fix the slot was released BEFORE the void's SaveChangesAsync, so a
        // failing save released a claim whose checkout stayed alive -- and the retry released it
        // again.
        db.Add(new SubscriptionChange
        {
            AgentUserId = agentId,
            RequestedBillingRuleId = 999_999_999,
            ChangeType = SubscriptionChangeType.Subscribe,
            Status = SubscriptionChangeStatus.Cancelled,
            EffectiveDate = DateTime.UtcNow
        });

        var ex = await Record.ExceptionAsync(() => NewService(db).CancelPendingPaymentAsync(agentId, invoiceId));
        Assert.NotNull(ex);   // the void genuinely failed
        db.ChangeTracker.Clear();

        var promo = await db.PromotionCodes.AsNoTracking().SingleAsync(p => p.Id == promoId);
        Assert.Equal(1, promo.RedemptionCount);   // nothing committed -> nothing released
    }

    [Fact]
    public void QUEUE_the_refund_screen_no_longer_instructs_an_action_that_does_not_exist()
    {
        // M6 (truth audit): RefundStatus.ConvertedToCredit is enum-only -- nothing sets it -- yet
        // the queue told the operator to "convert to credit". Instructing a nonexistent action is
        // a live UI defect regardless of the feature's backlog status.
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var view = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "src", "IPRO.Admin", "Views", "Refunds", "Index.cshtml"));
        Assert.DoesNotContain("convert to credit", view, StringComparison.OrdinalIgnoreCase);
    }

    /// A checkout abandoned 72h ago: Pending change + Pending billing + one claimed promo slot.
    private static async Task<(int AgentId, int ChangeId, int PromoId)> SeedStaleCheckoutAsync(
        IPRODbContext db, string payPalSubscriptionId)
    {
        var rule = new BillingRule { PackageName = $"W2-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        var promo = new PromotionCode { Code = $"W2{Guid.NewGuid():N}"[..10], MaxRedemptions = 5, RedemptionCount = 1, IsActive = true };
        db.AddRange(rule, promo);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"w2-{Guid.NewGuid():N}"[..20],
            Email = $"w2-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Stale", LastName = "Checkout",
            DomainName = $"w2-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario", PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var billing = new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id,
            BillingRuleId = rule.Id,
            PayPalSubscriptionId = payPalSubscriptionId,
            Amount = 60m,
            Status = BillingStatus.Pending,
            Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddHours(-72),
            CreatedAt = DateTime.UtcNow.AddHours(-72)
        };
        db.Add(billing);
        await db.SaveChangesAsync();
        var change = new SubscriptionChange
        {
            AgentUserId = agent.Id,
            RequestedBillingRuleId = rule.Id,
            BillingId = billing.Id,
            PromotionCodeId = promo.Id,
            ChangeType = SubscriptionChangeType.Subscribe,
            Status = SubscriptionChangeStatus.Pending,
            EffectiveDate = DateTime.UtcNow.AddHours(-72),
            AmountDue = 67.80m,
            CreatedAt = DateTime.UtcNow.AddHours(-72)
        };
        db.Add(change);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (agent.Id, change.Id, promo.Id);
    }

    private static PayPalBillingService NewServiceScripted(IPRODbContext db, ScriptedPayPal paypal) => new(
        new UnitOfWork(db),
        db,
        paypal,
        new StubEmailService(),
        Options.Create(new PayPalSettings { ClientId = "test-client", ClientSecret = "test-secret" }),
        new ConfigurationBuilder().Build(),
        NullLogger<PayPalBillingService>.Instance);

    /// A deterministic PayPal: answers the token endpoint, per-id subscription GETs, and records
    /// every cancel POST. No network.
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
                {
                    return Task.FromResult(Json("{\"access_token\":\"scripted-token\",\"expires_in\":3600}"));
                }
                if (request.Method == HttpMethod.Post && url.Contains("/cancel"))
                {
                    var id = url.Split('/')[^2];
                    _owner.CancelledSubscriptions.Add(id);
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

    // ------------------------------------------------------------------------------ plumbing --

    private sealed record AnnualSeed(int AgentId, int BillingId);

    private static async Task<AnnualSeed> SeedAnnualAsync(
        IPRODbContext db, decimal amount, DateTime nextBillingDate,
        (decimal SubTotal, DateTime IssuedAt, string Txn)[] invoices,
        string payPalSubscriptionId = "",
        BillingPeriod period = BillingPeriod.Annually,
        BillingStatus status = BillingStatus.Active,
        DateTime? paidThroughAt = null,
        string? featureCode = null)
    {
        if (!await db.ProvinceTaxRates.AnyAsync(t => t.ProvinceCode == "ON"))
        {
            db.Add(new ProvinceTaxRate { ProvinceCode = "ON", ProvinceName = "Ontario", Rate = 0.13m, TaxLabel = "ON 13% HST", IsActive = true });
        }
        var rule = new BillingRule { PackageName = $"W2-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        await db.SaveChangesAsync();
        if (featureCode != null)
        {
            db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = featureCode, FeatureName = "W2", IsIncluded = true });
        }
        var agent = new AgentUser
        {
            UserName = $"w2-{Guid.NewGuid():N}"[..20],
            Email = $"w2-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Wave", LastName = "Two",
            DomainName = $"w2-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario", PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var billing = new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id,
            BillingRuleId = rule.Id,
            PayPalSubscriptionId = payPalSubscriptionId,
            Amount = amount,
            Status = status,
            Period = period,
            StartDate = DateTime.UtcNow.AddYears(-1),
            NextBillingDate = nextBillingDate,
            PaidThroughAt = paidThroughAt,
            CancelledAt = status is BillingStatus.Cancelled or BillingStatus.Expired ? DateTime.UtcNow.AddHours(-1) : null
        };
        db.Add(billing);
        await db.SaveChangesAsync();
        foreach (var (subTotal, issuedAt, txn) in invoices)
        {
            db.Add(new Invoice
            {
                BillingId = billing.Id,
                AgentUserId = agent.Id,
                InvoiceNumber = $"W2-{billing.Id}-{Guid.NewGuid():N}"[..18],
                SubTotal = subTotal,
                TaxRate = 0.13m,
                TaxAmount = Math.Round(subTotal * 0.13m, 2),
                Total = Math.Round(subTotal * 1.13m, 2),
                PayPalTransactionId = txn,
                IssuedAt = issuedAt,
                IsPaid = true
            });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new AnnualSeed(agent.Id, billing.Id);
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

    private sealed class StubEmailService : IEmailService
    {
        public Task<bool> SendAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(true);
        public Task<EmailSendResult> SendDetailedAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(EmailSendResult.Sent());
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
    }

    private sealed class RecordingEmailService : IEmailService
    {
        public readonly List<string> SentTo = new();
        public Task<bool> SendAsync(string to, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null)
        { SentTo.Add(to); return Task.FromResult(true); }
        public Task<EmailSendResult> SendDetailedAsync(string to, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null)
        { SentTo.Add(to); return Task.FromResult(EmailSendResult.Sent()); }
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
    }

    private sealed class NoConsentSweep : IPRO.Business.Services.IEmailConsentService
    {
        public bool IsSuppressed(Client client, IPRO.Business.Services.EmailChannel channel, bool designSurvivesOptOut = false) => false;
        public Task<IPRO.Business.Services.SuppressionResult> SuppressAllAsync(Client client, string source) => throw new NotSupportedException();
        public Task ResubscribeAsync(Client client) => throw new NotSupportedException();
        public Task<int> CancelSuppressedDripEnrollmentsAsync(int batchLimit = 500) => Task.FromResult(0);
        public Task<string> GetOrCreateTokenAsync(Client client) => Task.FromResult("tok");
        public string BuildPreferencesUrl(string token) => $"https://example.test/prefs/{token}";
    }
}
