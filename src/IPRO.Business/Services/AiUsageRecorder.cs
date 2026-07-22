using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Business.Services;

public static class AiUsageRecorder
{
    // Haiku 4.5 base pricing as of 2026-07-21 (platform.claude.com/docs/en/about-claude/pricing): $1/MTok input, $5/MTok output.
    private const decimal InputCostPerMillionTokens = 1.00m;
    private const decimal OutputCostPerMillionTokens = 5.00m;

    public static async Task RecordAsync(IPRODbContext db, int callCount, long inputTokens, long outputTokens)
    {
        if (callCount <= 0) return;

        var estimatedCost = (inputTokens / 1_000_000m) * InputCostPerMillionTokens
                           + (outputTokens / 1_000_000m) * OutputCostPerMillionTokens;

        var today = DateTime.UtcNow.Date;
        var log = await db.AiUsageDailyLogs.FirstOrDefaultAsync(l => l.Date == today);
        if (log == null)
        {
            log = new AiUsageDailyLog { Date = today };
            db.AiUsageDailyLogs.Add(log);
        }

        log.CallCount += callCount;
        log.InputTokens += inputTokens;
        log.OutputTokens += outputTokens;
        log.EstimatedCostUsd += estimatedCost;
        log.UpdatedAt = DateTime.UtcNow;
    }
}
