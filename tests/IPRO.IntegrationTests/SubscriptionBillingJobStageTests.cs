using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using IPRO.Billing;
using IPRO.Scheduler;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// M19 (job half): SubscriptionBillingJob.RunAsync had no guard between its stages -- an exception
// in ProcessDueSubscriptionChangesAsync starved NotifyBillingIssuesAsync and both reconciles for
// that hour, and kept starving them for as long as the same failure recurred. Each stage must
// fail alone; the PayPal reconcile already had its own guard, the other three did not.
public class SubscriptionBillingJobStageTests
{
    [Fact]
    public async Task A_failing_stage_does_not_starve_the_stages_after_it()
    {
        var fake = new StageFake { ThrowInProcessDue = true, ThrowInNotify = true };
        var job = new SubscriptionBillingJob(fake, NullLogger<SubscriptionBillingJob>.Instance);

        var ex = await Record.ExceptionAsync(() => job.RunAsync());

        Assert.Null(ex);                                  // the job itself survives
        Assert.True(fake.NotifyCalled);                   // ran despite ProcessDue throwing
        Assert.True(fake.ReconcileDuplicatesCalled);      // ran despite Notify throwing
        Assert.True(fake.ReconcilePayPalCalled);
    }

    // 630: the lost-activation catch-up runs first, so a customer PayPal is already billing is active
    // before NotifyBillingIssuesAsync looks for checkouts Pending over 24 hours; and it fails alone.
    [Fact]
    public async Task The_lost_activation_catch_up_runs_first_and_fails_alone()
    {
        var fake = new StageFake();
        await new SubscriptionBillingJob(fake, NullLogger<SubscriptionBillingJob>.Instance).RunAsync();
        Assert.Equal(new[] { "CatchUp", "ProcessDue", "Notify", "ReconcileDuplicates", "ReconcilePayPal" }, fake.Order);

        var throwing = new StageFake { ThrowInCatchUp = true };
        Assert.Null(await Record.ExceptionAsync(() => new SubscriptionBillingJob(throwing, NullLogger<SubscriptionBillingJob>.Instance).RunAsync()));
        Assert.Equal(new[] { "CatchUp", "ProcessDue", "Notify", "ReconcileDuplicates", "ReconcilePayPal" }, throwing.Order);
    }

    private sealed class StageFake : IBillingService
    {
        public bool ThrowInCatchUp;
        public bool ThrowInProcessDue;
        public bool ThrowInNotify;
        public bool NotifyCalled;
        public bool ReconcileDuplicatesCalled;
        public bool ReconcilePayPalCalled;
        public readonly List<string> Order = new();

        public Task<int> CatchUpLostActivationsAsync()
        {
            Order.Add("CatchUp");
            return ThrowInCatchUp ? throw new InvalidOperationException("stage boom") : Task.FromResult(0);
        }
        public Task<int> ProcessDueSubscriptionChangesAsync()
        {
            Order.Add("ProcessDue");
            return ThrowInProcessDue ? throw new InvalidOperationException("stage boom") : Task.FromResult(0);
        }
        public Task<int> NotifyBillingIssuesAsync()
        {
            Order.Add("Notify");
            NotifyCalled = true;
            if (ThrowInNotify) throw new InvalidOperationException("stage boom");
            return Task.FromResult(0);
        }
        public Task<int> ReconcileDuplicateActiveSubscriptionsAsync()
        { Order.Add("ReconcileDuplicates"); ReconcileDuplicatesCalled = true; return Task.FromResult(0); }
        public Task<int> ReconcileActiveSubscriptionsWithPayPalAsync()
        { Order.Add("ReconcilePayPal"); ReconcilePayPalCalled = true; return Task.FromResult(0); }

        // Members the job never touches.
        public Task<IPRO.Entities.Billing?> GetActiveSubscriptionAsync(int userId) => throw new NotSupportedException();
        public Task<BillingChangeResult> CreateSubscriptionAsync(int userId, int billingRuleId, IPRO.Entities.BillingPeriod period, string returnUrl, string cancelUrl, string? downgradeMode = null) => throw new NotSupportedException();
        public Task<BillingChangeResult> ResumePaymentAsync(int userId, int invoiceId, string returnUrl, string cancelUrl) => throw new NotSupportedException();
        public Task<BillingChangeResult> CapturePaymentAsync(int userId, string orderId) => throw new NotSupportedException();
        public Task<IPRO.Entities.SubscriptionChange?> GetPendingChangeAsync(int userId) => throw new NotSupportedException();
        public Task<BillingChangeResult> CancelScheduledChangeAsync(int userId) => throw new NotSupportedException();
        public Task<BillingIssue?> GetBillingIssueAsync(int userId) => throw new NotSupportedException();
        public Task<bool> CancelPendingPaymentAsync(int userId, int invoiceId) => throw new NotSupportedException();
        public Task<bool> CancelPendingPaymentByOrderAsync(int userId, string orderId) => throw new NotSupportedException();
        public Task<bool> CancelSubscriptionAsync(int userId) => throw new NotSupportedException();
        public Task<bool> HandleWebhookAsync(string eventType, string payload, PayPalWebhookHeaders headers, decimal amount) => throw new NotSupportedException();
        public Task<PayPalPlanSyncResult> SyncPayPalPlansAsync(int billingRuleId) => throw new NotSupportedException();
        public Task<PayPalPlanSyncResult> SyncDailyTestPlanAsync(int billingRuleId) => throw new NotSupportedException();
        public Task<BillingChangeResult> EmailPaidInvoiceAsync(int invoiceId, bool force = false) => throw new NotSupportedException();
        public Task<List<IPRO.Entities.Invoice>> GetInvoicesAsync(int userId) => throw new NotSupportedException();
        public Task<IPRO.Entities.Invoice> GenerateInvoiceAsync(int userId, decimal amount, string description) => throw new NotSupportedException();
        public Task<List<IPRO.Entities.BillingRule>> GetPackagesAsync() => throw new NotSupportedException();
        public Task<IPRO.Entities.PromotionCode?> ValidatePromotionCodeAsync(string? code, int billingRuleId, int? agentId = null) => throw new NotSupportedException();
    }
}
