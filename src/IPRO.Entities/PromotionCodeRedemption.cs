namespace IPRO.Entities;

public class PromotionCodeRedemption
{
    public int Id { get; set; }
    public int PromotionCodeId { get; set; }
    public int AgentUserId { get; set; }
    public int BillingRuleId { get; set; }
    public BillingPeriod Period { get; set; }
    public decimal OriginalRecurringAmount { get; set; }
    public decimal DiscountedRecurringAmount { get; set; }
    public decimal OriginalSetupFee { get; set; }
    public decimal DiscountedSetupFee { get; set; }
    public DateTime RedeemedAt { get; set; } = DateTime.UtcNow;

    public PromotionCode PromotionCode { get; set; } = null!;
    public AgentUser AgentUser { get; set; } = null!;
    public BillingRule BillingRule { get; set; } = null!;
}
