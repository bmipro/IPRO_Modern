namespace IPRO.Billing;

public interface IBillingService
{
    Task<IPRO.Entities.Billing?> GetActiveSubscriptionAsync(int userId);
    Task<bool> CreateSubscriptionAsync(int userId, int billingRuleId, IPRO.Entities.BillingPeriod period);
    Task<IPRO.Entities.SubscriptionChange?> GetPendingChangeAsync(int userId);
    Task<bool> CancelSubscriptionAsync(int userId);
    Task<bool> HandleWebhookAsync(string eventType, string payload, string signature, decimal amount);
    Task<List<IPRO.Entities.Invoice>> GetInvoicesAsync(int userId);
    Task<IPRO.Entities.Invoice> GenerateInvoiceAsync(int userId, decimal amount, string description);
    Task<List<IPRO.Entities.BillingRule>> GetPackagesAsync();
}
