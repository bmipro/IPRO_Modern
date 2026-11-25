using IPRO.DataAccess;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Business.Services;

// 678 (audit 2026-11-24 F-4): "Draft with AI" (newsletters, articles, social posts) sent whatever topic was posted,
// up to the 4 MB form limit, as often as one address could post: one account could run up the Anthropic bill by tens
// of dollars a minute, and nothing showed which. Now a topic is at most MaxTopicLength characters, each account has
// a number of drafts a day (counted before the call, so a burst cannot run past it), and what each account used is
// recorded (AiAdviserDailyUsages) beside the platform's total (AiUsageDailyLog). A spend limit in the Anthropic
// console is the outer fence (the owner's setting).
//
// 679 (the owner, 2026-11-25: "one number for everyone", set in SuperAdmin): the number a day is
// AiBillingSettings.DraftsPerAccountPerDay, on SuperAdmin's AI Usage page (DefaultPerDay until changed). The day is the
// account's own (its time zone), and a draft the AI service did not answer is given back.
public static class AiDraftAllowance
{
    public const int MaxTopicLength = 500;
    public const int DefaultPerDay = 40;
    public const int MinPerDay = 1;
    public const int MaxPerDay = 1000;

    public sealed record Allowance(bool Allowed, int Limit);

    public static string? TopicProblem(string? topic) =>
        string.IsNullOrWhiteSpace(topic) ? "Enter a topic first, then draft with AI."
        : topic.Trim().Length > MaxTopicLength ? $"Keep the topic under {MaxTopicLength} characters: a sentence or two is plenty."
        : null;

    public static string UsedUpMessage(int limit) =>
        $"You have used today's {limit} AI drafts. More are available tomorrow; meanwhile you can write it yourself.";

    public static string? LimitProblem(int perDay) =>
        perDay is < MinPerDay or > MaxPerDay ? $"Enter a number from {MinPerDay} to {MaxPerDay}." : null;

    // SuperAdmin's number, or the default when it was never set.
    public static async Task<int> LimitAsync(IPRODbContext db)
    {
        var limit = await db.AiBillingSettings.AsNoTracking().Where(s => s.Id == 1).Select(s => (int?)s.DraftsPerAccountPerDay).FirstOrDefaultAsync();
        return limit is >= MinPerDay and <= MaxPerDay ? limit.Value : DefaultPerDay;
    }

    // The account's own date today (its time zone), which is the day its drafts are counted in.
    public static async Task<DateTime> DayAsync(IPRODbContext db, int agentUserId, DateTime nowUtc)
    {
        var zone = await db.AgentUsers.AsNoTracking().Where(a => a.Id == agentUserId).Select(a => a.TimeZone).FirstOrDefaultAsync();
        return AgentLocalTime.FromUtc(nowUtc, zone).Date;
    }

    // Takes one of the account's drafts for its day, or refuses when the day's are used.
    public static async Task<Allowance> TryTakeAsync(IPRODbContext db, int agentUserId, DateTime nowUtc)
    {
        var limit = await LimitAsync(db);
        var day = await DayAsync(db, agentUserId, nowUtc);
        await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO `AiAdviserDailyUsages`
            (`AgentUserId`, `Date`, `CallCount`, `InputTokens`, `OutputTokens`, `UpdatedAt`)
            VALUES ({agentUserId}, {day}, 1, 0, 0, {nowUtc})
            ON DUPLICATE KEY UPDATE `CallCount` = `CallCount` + 1, `UpdatedAt` = {nowUtc}");
        var count = await db.AiAdviserDailyUsages.AsNoTracking()
            .Where(u => u.AgentUserId == agentUserId && u.Date == day)
            .Select(u => u.CallCount)
            .FirstOrDefaultAsync();
        if (count <= limit) return new Allowance(true, limit);
        await GiveBackAsync(db, agentUserId, day, nowUtc);   // a refused try is not a draft
        return new Allowance(false, limit);
    }

    // After the call: what it used, for the account's row and the platform's day; a call the AI service did not
    // answer (nothing used) gives its draft back.
    public static async Task FinishAsync(IPRODbContext db, int agentUserId, long inputTokens, long outputTokens, DateTime nowUtc)
    {
        var day = await DayAsync(db, agentUserId, nowUtc);
        if (inputTokens <= 0 && outputTokens <= 0)
        {
            await GiveBackAsync(db, agentUserId, day, nowUtc);
            return;
        }
        await db.AiAdviserDailyUsages
            .Where(u => u.AgentUserId == agentUserId && u.Date == day)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.InputTokens, x => x.InputTokens + inputTokens)
                .SetProperty(x => x.OutputTokens, x => x.OutputTokens + outputTokens)
                .SetProperty(x => x.UpdatedAt, nowUtc));
        await AiUsageRecorder.RecordAsync(db, 1, inputTokens, outputTokens);
        await db.SaveChangesAsync();
    }

    private static Task<int> GiveBackAsync(IPRODbContext db, int agentUserId, DateTime day, DateTime nowUtc) =>
        db.AiAdviserDailyUsages
            .Where(u => u.AgentUserId == agentUserId && u.Date == day && u.CallCount > 0)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.CallCount, x => x.CallCount - 1).SetProperty(x => x.UpdatedAt, nowUtc));
}
