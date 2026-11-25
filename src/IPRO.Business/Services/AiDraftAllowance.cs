using IPRO.DataAccess;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Business.Services;

// 678 (audit 2026-11-24 F-4): "Draft with AI" (newsletters, articles, social posts) sent whatever topic was posted,
// up to the 4 MB form limit, as often as one address could post: one account could run up the Anthropic bill by tens
// of dollars a minute, and nothing showed which. Now a topic is at most MaxTopicLength characters, each adviser has
// PerAdviserPerDay drafts a day (counted before the call, so a burst cannot run past it), and what each adviser used
// is recorded (AiAdviserDailyUsages) beside the platform's total (AiUsageDailyLog). A spend limit in the Anthropic
// console is the outer fence (the owner's setting).
public static class AiDraftAllowance
{
    public const int MaxTopicLength = 500;
    public const int PerAdviserPerDay = 40;

    public static string? TopicProblem(string? topic) =>
        string.IsNullOrWhiteSpace(topic) ? "Enter a topic first, then draft with AI."
        : topic.Trim().Length > MaxTopicLength ? $"Keep the topic under {MaxTopicLength} characters: a sentence or two is plenty."
        : null;

    public static string UsedUpMessage =>
        $"You have used today's {PerAdviserPerDay} AI drafts. More are available tomorrow; meanwhile you can write it yourself.";

    // Takes one of the adviser's drafts for today (UTC), or false when the day's are used.
    public static async Task<bool> TryTakeAsync(IPRODbContext db, int agentUserId, DateTime nowUtc)
    {
        var day = nowUtc.Date;
        await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO `AiAdviserDailyUsages`
            (`AgentUserId`, `Date`, `CallCount`, `InputTokens`, `OutputTokens`, `UpdatedAt`)
            VALUES ({agentUserId}, {day}, 1, 0, 0, {nowUtc})
            ON DUPLICATE KEY UPDATE `CallCount` = `CallCount` + 1, `UpdatedAt` = {nowUtc}");
        var count = await db.AiAdviserDailyUsages.AsNoTracking()
            .Where(u => u.AgentUserId == agentUserId && u.Date == day)
            .Select(u => u.CallCount)
            .FirstOrDefaultAsync();
        return count <= PerAdviserPerDay;
    }

    // What the draft used, for the adviser's row and the platform's day.
    public static async Task RecordAsync(IPRODbContext db, int agentUserId, long inputTokens, long outputTokens, DateTime nowUtc)
    {
        if (inputTokens <= 0 && outputTokens <= 0) return;
        var day = nowUtc.Date;
        await db.AiAdviserDailyUsages
            .Where(u => u.AgentUserId == agentUserId && u.Date == day)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.InputTokens, x => x.InputTokens + inputTokens)
                .SetProperty(x => x.OutputTokens, x => x.OutputTokens + outputTokens)
                .SetProperty(x => x.UpdatedAt, nowUtc));
        await AiUsageRecorder.RecordAsync(db, 1, inputTokens, outputTokens);
        await db.SaveChangesAsync();
    }
}
