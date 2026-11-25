using System;

namespace IPRO.Entities;

// 678 (audit 2026-11-24 F-4): AI drafting per adviser and day. AiUsageDailyLog counts the platform as a whole, so one
// account's use could not be seen, let alone limited. One row per adviser and day; AiDraftAllowance counts a draft
// before it is asked for and refuses past the day's allowance.
public class AiAdviserDailyUsage
{
    public int AgentUserId { get; set; }
    public DateTime Date { get; set; }
    public int CallCount { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
