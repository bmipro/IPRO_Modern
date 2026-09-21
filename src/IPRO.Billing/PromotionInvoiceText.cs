using IPRO.Entities;

namespace IPRO.Billing;

// 510 (2026-09-21): what a promotion code did, in the words of the invoice line it touched. The
// first live free-month sign-up produced an invoice whose only line read "IPro Gold subscription
// adjustment - $0.00" -- true, and no way to greet every prospect who was sent a code. A line now
// says what was given, for how long, and by which code; a setup fee a code took off is shown as a
// line of its own instead of silently missing.
public static class PromotionInvoiceText
{
    // The recurring line of the FIRST invoice. A code that leaves the recurring price alone (a
    // setup-fee code) leaves this line exactly as it always read.
    public static string Recurring(string packageName, BillingPeriod period, PromotionCode promo, decimal discountedAmount)
    {
        var periodWord = PeriodWord(period);
        if (promo.RecurringDiscountType == PromoDiscountType.None)
        {
            return $"{packageName} {periodWord} recurring subscription";
        }

        var unit = period == BillingPeriod.Annually ? "year" : period == BillingPeriod.Quarterly ? "quarter" : "month";
        var cycles = promo.RecurringDurationCycles;
        var span = cycles == null ? null : cycles == 1 ? $"first {unit}" : $"first {cycles} {unit}s";

        if (discountedAmount <= 0)
        {
            return span == null
                ? $"{packageName} {periodWord} subscription - free with promotion code {promo.Code}"
                : $"{packageName} {periodWord} subscription - {span} free with promotion code {promo.Code}";
        }

        var length = span == null ? "for the life of the subscription" : $"for the {span}";
        return $"{packageName} {periodWord} recurring subscription - {Discount(promo.RecurringDiscountType, promo.RecurringDiscountValue)} {length} with promotion code {promo.Code}";
    }

    // The setup line, only when THIS code took something off a fee that was really due: a fee the
    // package itself had already waived (BillingRule.EffectiveSetupFee) is not the code's doing.
    public static string? Setup(string packageName, PromotionCode promo, decimal baseFee, decimal discountedFee)
    {
        if (promo.SetupFeeDiscountType == PromoDiscountType.None || baseFee <= 0 || discountedFee >= baseFee)
        {
            return null;
        }

        return discountedFee <= 0
            ? $"{packageName} one-time setup fee - waived with promotion code {promo.Code}"
            : $"{packageName} one-time setup fee - {Discount(promo.SetupFeeDiscountType, promo.SetupFeeDiscountValue)} with promotion code {promo.Code}";
    }

    private static string Discount(PromoDiscountType type, decimal value) =>
        type == PromoDiscountType.PercentOff ? $"{value:0.##}% off" : $"${value:0.##} off";

    private static string PeriodWord(BillingPeriod period) => period switch
    {
        BillingPeriod.Annually => "annual",
        BillingPeriod.Quarterly => "quarterly",
        _ => "monthly"
    };
}
