namespace IPRO.Entities;

public enum SubscriptionChangeType { Subscribe, Upgrade, Downgrade, Cancel }
public enum RefundStatus { None, Pending, Refunded, ConvertedToCredit, Waived }
public enum SubscriptionChangeStatus { Pending, Applied, Cancelled }

public class SubscriptionChange
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public int? CurrentBillingRuleId { get; set; }
    public int RequestedBillingRuleId { get; set; }
    public int? BillingId { get; set; }
    public int? PromotionCodeId { get; set; }
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

    // Refund bookkeeping for ChangeType.Cancel rows (DOCS/22). Money never moves in code: the
    // owner refunds manually at PayPal and flips RefundStatus in the SuperAdmin queue. Retained
    // by the eraser like every financial record.
    public decimal RefundNetAmount { get; set; }
    public decimal RefundTaxAmount { get; set; }
    public decimal RefundGrossAmount { get; set; }
    public RefundStatus RefundStatus { get; set; } = RefundStatus.None;
    public string RefundPayPalTransactionId { get; set; } = string.Empty;
    public DateTime? RefundWindowEndsAt { get; set; }
    public DateTime? RefundResolvedAt { get; set; }
    public string RefundResolutionNote { get; set; } = string.Empty;

    public AgentUser AgentUser { get; set; } = null!;
    public BillingRule? CurrentBillingRule { get; set; }
    public BillingRule RequestedBillingRule { get; set; } = null!;
    public Billing? Billing { get; set; }
}
