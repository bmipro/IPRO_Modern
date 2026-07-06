namespace IPRO.Entities;

public enum SubscriptionChangeType { Subscribe, Upgrade, Downgrade }
public enum SubscriptionChangeStatus { Pending, Applied, Cancelled }

public class SubscriptionChange
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public int? CurrentBillingRuleId { get; set; }
    public int RequestedBillingRuleId { get; set; }
    public int? BillingId { get; set; }
    public SubscriptionChangeType ChangeType { get; set; }
    public SubscriptionChangeStatus Status { get; set; } = SubscriptionChangeStatus.Pending;
    public BillingPeriod Period { get; set; } = BillingPeriod.Monthly;
    public DateTime EffectiveDate { get; set; }
    public decimal ProratedCredit { get; set; }
    public decimal ProratedCharge { get; set; }
    public decimal AmountDue { get; set; }
    public string Currency { get; set; } = "CAD";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AppliedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public AgentUser AgentUser { get; set; } = null!;
    public BillingRule? CurrentBillingRule { get; set; }
    public BillingRule RequestedBillingRule { get; set; } = null!;
    public Billing? Billing { get; set; }
}
