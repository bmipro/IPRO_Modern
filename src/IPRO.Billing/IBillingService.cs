namespace IPRO.Billing;

public enum BillingPeriod { Monthly, Yearly }

public class Invoice 
{ 
    public int Id { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class BillingPackage 
{ 
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public interface IBillingService
{
    Task<BillingService?> GetActiveSubscriptionAsync(int userId);
    Task<bool> CreateSubscriptionAsync(int userId, int planId, BillingPeriod period);
    Task<bool> CancelSubscriptionAsync(int userId);
    Task<bool> HandleWebhookAsync(string eventType, string payload, string signature, decimal amount);
    Task<List<Invoice>> GetInvoicesAsync(int userId);
    Task<Invoice> GenerateInvoiceAsync(int userId, decimal amount, string description);
    Task<List<BillingPackage>> GetPackagesAsync();
}