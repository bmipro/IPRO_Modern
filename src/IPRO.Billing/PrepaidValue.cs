namespace IPRO.Billing;

// The one place that answers "what happens to money an agent already paid" when they cancel or
// downgrade. Design + worked examples: DOCS/22_PREPAID_VALUE.md (owner-decided 2026-08-20).
// Everything here is pure: dates and amounts in, a decision out. Nothing touches the database or
// PayPal, which is what makes the whole policy unit-testable as a matrix.
public static class PrepaidValue
{
    /// Days of access an agent keeps after cancelling, past the paid-through moment.
    public const int CancellationGraceDays = 2;

    /// PayPal refuses refunds against transactions older than ~this. Planning number, verified
    /// against PayPal's policy at refund time by the human doing the refund; the queue shows a
    /// countdown so nobody attempts a refund the portal will reject.
    public const int PayPalRefundWindowDays = 180;

    public enum CancelKind
    {
        /// Monthly, or annual past the crossover: no money moves, access runs to PaidThroughAt.
        AccessUntilPaidThrough,
        /// Annual before the crossover: refund due, access to end of the current billing month.
        RefundAndEndOfMonth
    }

    public sealed record CancelOutcome(
        CancelKind Kind,
        int MonthsUsed,
        decimal RefundNet,
        decimal RefundTax,
        decimal RefundGross,
        DateTime PaidThroughAt);

    /// Monthly cancel: PayPal stops now, access to the next billing date + grace. When the row
    /// has no NextBillingDate (data gap), fall back to one period from the cancel moment rather
    /// than gating instantly -- the agent paid for the running month either way.
    public static CancelOutcome MonthlyCancel(DateTime? nextBillingDate, DateTime cancelAtUtc)
    {
        var paidThrough = (nextBillingDate ?? cancelAtUtc.AddMonths(1)).AddDays(CancellationGraceDays);
        return new CancelOutcome(CancelKind.AccessUntilPaidThrough, 0, 0m, 0m, 0m, paidThrough);
    }

    /// Annual cancel with the discount clawback. "The annual discount belongs to those who stay":
    /// months used revert to the monthly price; the remainder is refunded gross of the tax that
    /// was charged on it. From the crossover month on (month 10 for every "two months free"
    /// package) the refund is zero, so the agent keeps access to the anniversary instead -- the
    /// system always picks the agent-favouring outcome, never a worse-than-either middle.
    public static CancelOutcome AnnualCancel(
        decimal paidNet,
        decimal monthlyNetPrice,
        decimal taxRate,
        DateTime periodStartUtc,
        DateTime cancelAtUtc)
    {
        if (cancelAtUtc < periodStartUtc) cancelAtUtc = periodStartUtc;

        var monthsUsed = MonthsUsedRoundingUp(periodStartUtc, cancelAtUtc);
        // A cancel inside the final months of the year can't "use" more than the year.
        if (monthsUsed > 12) monthsUsed = 12;

        var refundNet = paidNet - monthlyNetPrice * monthsUsed;
        if (refundNet <= 0m || monthlyNetPrice <= 0m)
        {
            // Crossover or beyond: nothing to refund, so the paid year is honored in full.
            return new CancelOutcome(
                CancelKind.AccessUntilPaidThrough, monthsUsed, 0m, 0m, 0m,
                periodStartUtc.AddYears(1).AddDays(CancellationGraceDays));
        }

        refundNet = Math.Round(refundNet, 2, MidpointRounding.AwayFromZero);
        var refundTax = Math.Round(refundNet * taxRate, 2, MidpointRounding.AwayFromZero);
        return new CancelOutcome(
            CancelKind.RefundAndEndOfMonth,
            monthsUsed,
            refundNet,
            refundTax,
            refundNet + refundTax,
            // Access to the end of the month being used right now (the one they just paid the
            // monthly rate for via the clawback), plus grace.
            periodStartUtc.AddMonths(monthsUsed).AddDays(CancellationGraceDays));
    }

    /// Calendar months from periodStart to cancelAt where any started month counts as used --
    /// symmetric with the monthly rule (monthly cancellers also keep-and-pay-for the running
    /// month). Cancel at the very start of month 5 (4 whole months + 1 day) => 5.
    public static int MonthsUsedRoundingUp(DateTime periodStartUtc, DateTime cancelAtUtc)
    {
        if (cancelAtUtc <= periodStartUtc) return 1; // day one still consumes month one
        var whole = 0;
        var cursor = periodStartUtc;
        while (cursor.AddMonths(1) <= cancelAtUtc && whole < 12)
        {
            cursor = cursor.AddMonths(1);
            whole++;
        }
        var partial = cancelAtUtc > cursor ? 1 : 0;
        return Math.Min(12, Math.Max(1, whole + partial));
    }

    /// Downgrade "convert" credit: unused NET dollars become days on the new package, at the new
    /// package's NET daily rate, rounded UP (in the agent's favour). The remaining dollars are
    /// stored alongside the date so a later change can re-convert them (DOCS/22).
    public static int CreditDays(decimal remainingNet, decimal newMonthlyNetPrice)
    {
        if (remainingNet <= 0m || newMonthlyNetPrice <= 0m) return 0;
        var dailyRate = newMonthlyNetPrice * 12m / 365m;
        return (int)Math.Ceiling(remainingNet / dailyRate);
    }

    /// The last day a PayPal-portal refund against the original transaction is expected to work.
    public static DateTime RefundWindowEndsAt(DateTime originalPaymentUtc) =>
        originalPaymentUtc.AddDays(PayPalRefundWindowDays);
}
