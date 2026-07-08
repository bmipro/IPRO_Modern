namespace IPRO.Billing;

public interface IBillingService
{
    Task<IPRO.Entities.Billing?> GetActiveSubscriptionAsync(int userId);
    Task<BillingChangeResult> CreateSubscriptionAsync(int userId, int billingRuleId, IPRO.Entities.BillingPeriod period, string returnUrl, string cancelUrl);
    Task<BillingChangeResult> ResumePaymentAsync(int userId, int invoiceId, string returnUrl, string cancelUrl);
    Task<BillingChangeResult> CapturePaymentAsync(int userId, string orderId);
    Task<IPRO.Entities.SubscriptionChange?> GetPendingChangeAsync(int userId);
    Task<bool> CancelPendingPaymentAsync(int userId, int invoiceId);
    Task<bool> CancelPendingPaymentByOrderAsync(int userId, string orderId);
    Task<bool> CancelSubscriptionAsync(int userId);
    Task<int> ProcessDueSubscriptionChangesAsync();
    Task<bool> HandleWebhookAsync(string eventType, string payload, string signature, decimal amount);
    Task<List<IPRO.Entities.Invoice>> GetInvoicesAsync(int userId);
    Task<IPRO.Entities.Invoice> GenerateInvoiceAsync(int userId, decimal amount, string description);
    Task<List<IPRO.Entities.BillingRule>> GetPackagesAsync();
}
