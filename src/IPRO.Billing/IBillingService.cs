namespace IPRO.Billing;

public interface IBillingService
{
    Task<IPRO.Entities.Billing?> GetActiveSubscriptionAsync(int userId);
    Task<BillingChangeResult> CreateSubscriptionAsync(int userId, int billingRuleId, IPRO.Entities.BillingPeriod period, string returnUrl, string cancelUrl);
    Task<BillingChangeResult> ResumePaymentAsync(int userId, int invoiceId, string returnUrl, string cancelUrl);
    Task<BillingChangeResult> CapturePaymentAsync(int userId, string orderId);
    Task<IPRO.Entities.SubscriptionChange?> GetPendingChangeAsync(int userId);
    Task<BillingIssue?> GetBillingIssueAsync(int userId);
    Task<bool> CancelPendingPaymentAsync(int userId, int invoiceId);
    Task<bool> CancelPendingPaymentByOrderAsync(int userId, string orderId);
    Task<bool> CancelSubscriptionAsync(int userId);
    Task<int> ProcessDueSubscriptionChangesAsync();
    Task<int> NotifyBillingIssuesAsync();
    Task<int> ReconcileDuplicateActiveSubscriptionsAsync();

    // Ask PayPal the true state of every Active subscription and correct any that PayPal has stopped.
    // Nothing else closes this gap: we learn a subscription ended only from a webhook, so a single
    // lost CANCELLED/EXPIRED delivery -- or a buyer cancelling inside PayPal's own UI, which we may
    // never be told about -- leaves an Active row granting full access forever. Returns how many rows
    // were corrected.
    Task<int> ReconcileActiveSubscriptionsWithPayPalAsync();
    Task<bool> HandleWebhookAsync(string eventType, string payload, PayPalWebhookHeaders headers, decimal amount);
    Task<PayPalPlanSyncResult> SyncPayPalPlansAsync(int billingRuleId);
    Task<PayPalPlanSyncResult> SyncDailyTestPlanAsync(int billingRuleId);
    Task<BillingChangeResult> EmailPaidInvoiceAsync(int invoiceId, bool force = false);
    Task<List<IPRO.Entities.Invoice>> GetInvoicesAsync(int userId);
    Task<IPRO.Entities.Invoice> GenerateInvoiceAsync(int userId, decimal amount, string description);
    Task<List<IPRO.Entities.BillingRule>> GetPackagesAsync();
    Task<IPRO.Entities.PromotionCode?> ValidatePromotionCodeAsync(string? code, int billingRuleId, int? agentId = null);
}
