using IPRO.DataAccess;
using IPRO.Entities;

namespace IPRO.Web.Infrastructure;

// 508: what the registration page tells a prospect about the code they typed. Moved out of
// AccountController.ValidatePromoCode so the sentence can say which billing period the code works
// with -- and, once that is known, say "month" or "year" where it used to say "billing cycle".
public static class PromotionCodeText
{
    public static string Accepted(PromotionCode promo, BillingPeriod? limit)
    {
        var parts = new List<string>();
        if (promo.RecurringDiscountType != PromoDiscountType.None)
        {
            var unit = limit == BillingPeriod.Monthly ? "month" : limit == BillingPeriod.Annually ? "year" : null;
            var durationText = promo.RecurringDurationCycles == null
                ? "for the life of your subscription"
                : promo.RecurringDurationCycles == 1
                    ? (unit == null ? "on your first billing cycle only" : $"for your first {unit}")
                    : $"for your first {promo.RecurringDurationCycles} {(unit == null ? "billing cycles" : unit + "s")}";
            var discountText = promo.RecurringDiscountType == PromoDiscountType.PercentOff
                ? $"{promo.RecurringDiscountValue:0.##}% off"
                : $"${promo.RecurringDiscountValue:0.##} off";
            parts.Add($"{discountText} the recurring price {durationText}");
        }
        if (promo.SetupFeeDiscountType != PromoDiscountType.None)
        {
            var discountText = promo.SetupFeeDiscountType == PromoDiscountType.PercentOff
                ? $"{promo.SetupFeeDiscountValue:0.##}% off"
                : $"${promo.SetupFeeDiscountValue:0.##} off";
            parts.Add($"{discountText} the setup fee");
        }

        var with = limit == null ? "" : $", with {PromotionCodePeriod.Words(limit.Value)} only";
        return parts.Count == 0
            ? $"Code accepted{with}."
            : $"Code accepted: {string.Join(" and ", parts)}{with}.";
    }

    // The customer picked the other period: say which one the code needs, not just "not valid".
    public static string WrongPeriod(BillingPeriod limit) =>
        $"This code works with {PromotionCodePeriod.Words(limit)} only. Choose {(limit == BillingPeriod.Annually ? "Annual" : "Monthly")} above to use it, or remove the code.";
}
