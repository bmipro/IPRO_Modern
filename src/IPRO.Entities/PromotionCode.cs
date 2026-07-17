namespace IPRO.Entities;

public enum PromoDiscountType { None, PercentOff, FlatAmountOff }

public class PromotionCode
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime? ExpiresAt { get; set; }
    public int? MaxRedemptions { get; set; }
    public int RedemptionCount { get; set; }

    public int? RestrictedBillingRuleId { get; set; }

    public PromoDiscountType RecurringDiscountType { get; set; } = PromoDiscountType.None;
    public decimal RecurringDiscountValue { get; set; }
    public int? RecurringDurationCycles { get; set; }

    public PromoDiscountType SetupFeeDiscountType { get; set; } = PromoDiscountType.None;
    public decimal SetupFeeDiscountValue { get; set; }

    public string? PayPalPromoPlanIdMonthly { get; set; } = string.Empty;
    public string? PayPalPromoPlanIdAnnual { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public BillingRule? RestrictedBillingRule { get; set; }
}
