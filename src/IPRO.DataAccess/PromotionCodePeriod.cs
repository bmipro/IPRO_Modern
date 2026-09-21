using System;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// 508: the one billing period a promotion code works with (PromotionCodePeriodLimit). No row means
// both. Set-based, like FollowUpReminderPreference: nothing is tracked, and saving the same answer
// twice is not an error.
public static class PromotionCodePeriod
{
    public static async Task<BillingPeriod?> LimitAsync(IPRODbContext db, int promotionCodeId)
    {
        var rows = await db.PromotionCodePeriodLimits.AsNoTracking()
            .Where(l => l.PromotionCodeId == promotionCodeId)
            .Select(l => (BillingPeriod?)l.Period)
            .ToListAsync();
        return rows.Count == 0 ? null : rows[0];
    }

    public static async Task<bool> AllowsAsync(IPRODbContext db, int promotionCodeId, BillingPeriod period)
    {
        var limit = await LimitAsync(db, promotionCodeId);
        return limit == null || limit == period;
    }

    public static async Task SetAsync(IPRODbContext db, int promotionCodeId, BillingPeriod? limit)
    {
        if (limit == null)
        {
            await db.PromotionCodePeriodLimits.Where(l => l.PromotionCodeId == promotionCodeId).ExecuteDeleteAsync();
            return;
        }

        var updated = await db.PromotionCodePeriodLimits
            .Where(l => l.PromotionCodeId == promotionCodeId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Period, limit.Value));
        if (updated == 0)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO `PromotionCodePeriodLimits` (`PromotionCodeId`, `Period`) VALUES ({promotionCodeId}, {(int)limit.Value}) ON DUPLICATE KEY UPDATE `Period` = VALUES(`Period`)");
        }
    }

    // "monthly billing" / "annual billing": the words every screen uses for a limit.
    public static string Words(BillingPeriod period) => period == BillingPeriod.Annually ? "annual billing" : "monthly billing";

    // The form's value for a limit ("" = both), and back. Only the two periods the product sells.
    public static BillingPeriod? Parse(string? value) =>
        string.Equals(value?.Trim(), "Monthly", StringComparison.OrdinalIgnoreCase) ? BillingPeriod.Monthly
        : string.Equals(value?.Trim(), "Annually", StringComparison.OrdinalIgnoreCase) ? BillingPeriod.Annually
        : null;
}
