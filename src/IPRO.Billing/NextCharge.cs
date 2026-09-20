using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Billing;

/// <summary>
/// What PayPal will take on the next billing date, before tax.
///
/// Billing.Amount is not that number for a promotion with a LIMITED run of discounted cycles: the
/// discounted price is written into it at signup and only catches up when the first full-price sale
/// arrives (the sale webhook's Amount sync) -- one charge too late for a customer reading "Next
/// billing: October 20 - $3.00" the day before PayPal bills $60.00 plus tax. Found in the owner's
/// first real-money test on live PayPal, 2026-09-20.
///
/// The PayPal plan behind such a code (CreatePromoPayPalPlanAsync) is N TRIAL cycles at the
/// discounted price followed by REGULAR cycles at the full one, and a cycle is spent on every
/// billing date whether or not money moves -- a free month sends no sale at all. So the cycle is
/// counted from DATES, never from invoices: the next billing date is the k-th one after the
/// start, and it is discounted while k is below N.
/// </summary>
public sealed record NextCharge(decimal Amount, decimal? RegularAmount, int? PromoCycles, int DiscountedCyclesLeft)
{
    /// <summary>The discounted run is used up and the next charge is the regular price, while the
    /// stored amount still shows the discount. False once the row has caught up: by then the
    /// customer has an invoice at the regular price and needs no explanation.</summary>
    public bool PromotionEnded => PromoCycles.HasValue && DiscountedCyclesLeft == 0 && RegularAmount.HasValue;

    public static NextCharge Compute(
        decimal storedAmount,
        DateTime startDate,
        DateTime? nextBillingDate,
        BillingPeriod period,
        int? promoCycles,
        decimal? regularAmount,
        DateTime nowUtc)
    {
        // No code, a forever code, or a row the sale webhook has already brought up to the regular
        // price: the stored amount IS the next charge.
        if (!promoCycles.HasValue || !regularAmount.HasValue || !nextBillingDate.HasValue ||
            regularAmount.Value <= storedAmount)
        {
            return new NextCharge(storedAmount, null, null, 0);
        }

        var monthsPerCycle = period == BillingPeriod.Annually ? 12 : period == BillingPeriod.Quarterly ? 3 : 1;
        var cycleDays = monthsPerCycle * 30.4375;

        // A first charge that has not happened yet -- re-subscribing inside a period already paid
        // for defers it to the end of that period -- is cycle 0 however far away it is. Same test
        // as the Billing page's own "already paid through" sentence. Known limit: AFTER such a
        // deferred first charge the count below runs ahead by the length of the deferral, so the
        // page can announce the regular price early. It errs toward the higher number, and it
        // needs a promotion code on a re-subscription, which only SuperAdmin can arrange.
        var firstChargeStillAhead = nextBillingDate.Value > nowUtc.AddDays(cycleDays * 1.5);

        // Nearest whole number of cycles: PayPal's own billing time drifts by hours, and a month
        // that starts on the 31st ends on the 28th.
        var cyclesSpent = firstChargeStillAhead
            ? 0
            : Math.Max(0, (int)Math.Round((nextBillingDate.Value - startDate).TotalDays / cycleDays, MidpointRounding.AwayFromZero));

        var left = Math.Max(0, promoCycles.Value - cyclesSpent);
        return new NextCharge(left > 0 ? storedAmount : regularAmount.Value, regularAmount, promoCycles, left);
    }

    /// <summary>
    /// The promotion is found through the applied change that created THIS billing row, never
    /// through the agent: a code spent on an earlier subscription must not follow them to the
    /// next one. The regular price is the one recorded at redemption -- what the PayPal plan was
    /// built with -- not today's package price, which the owner may have changed since.
    /// </summary>
    public static async Task<NextCharge> ForAsync(IPRODbContext db, IPRO.Entities.Billing subscription, DateTime nowUtc)
    {
        var promotionCodeId = await db.SubscriptionChanges.AsNoTracking()
            .Where(c => c.BillingId == subscription.Id &&
                        c.Status == SubscriptionChangeStatus.Applied &&
                        c.PromotionCodeId != null)
            .OrderByDescending(c => c.AppliedAt)
            .Select(c => c.PromotionCodeId)
            .FirstOrDefaultAsync();
        if (promotionCodeId == null)
        {
            return new NextCharge(subscription.Amount, null, null, 0);
        }

        var promoCycles = await db.PromotionCodes.AsNoTracking()
            .Where(p => p.Id == promotionCodeId.Value)
            .Select(p => p.RecurringDurationCycles)
            .FirstOrDefaultAsync();

        var regularAmount = await db.PromotionCodeRedemptions.AsNoTracking()
            .Where(r => r.PromotionCodeId == promotionCodeId.Value &&
                        r.AgentUserId == subscription.AgentUserId &&
                        r.BillingRuleId == subscription.BillingRuleId &&
                        r.Period == subscription.Period)
            .OrderByDescending(r => r.RedeemedAt)
            .Select(r => (decimal?)r.OriginalRecurringAmount)
            .FirstOrDefaultAsync();

        return Compute(subscription.Amount, subscription.StartDate, subscription.NextBillingDate,
            subscription.Period, promoCycles, regularAmount, nowUtc);
    }
}
