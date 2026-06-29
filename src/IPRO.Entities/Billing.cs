namespace IPRO.Entities;

public enum BillingStatus { Pending, Active, Cancelled, Expired, Failed }
public enum BillingPeriod { Monthly, Quarterly, Annually }

public class Billing
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public int BillingRuleId { get; set; }
    public string PayPalSubscriptionId { get; set; } = string.Empty;
    public string PayPalPlanId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "CAD";
    public BillingStatus Status { get; set; } = BillingStatus.Pending;
    public BillingPeriod Period { get; set; } = BillingPeriod.Monthly;
    public DateTime StartDate { get; set; }
    public DateTime? NextBillingDate { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public AgentUser AgentUser { get; set; } = null!;
    public BillingRule BillingRule { get; set; } = null!;
    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
}
