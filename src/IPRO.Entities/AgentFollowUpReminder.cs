namespace IPRO.Entities;

// 498 (2026-09-18): the adviser's morning follow-ups email (FollowUpReminderJob) -- whether they want
// it, and the last LOCAL day the job made its decision for. No row means "wants it, never decided".
//
// Its own small table, deliberately NOT two columns on AgentUsers. A column the model expects and the
// table lacks fails EVERY AgentUsers query -- sign-in included -- and StartupGuard starts the app even
// when a repair step could not run (a held metadata lock costs that step, not the site). An ALTER on
// the busiest table three days before launch is that risk; a CREATE TABLE nobody else reads is not:
// if it ever fails, the reminder email and its profile switch fail, and nothing else does.
public class AgentFollowUpReminder
{
    public int AgentUserId { get; set; }
    public bool IsEnabled { get; set; } = true;
    // The adviser's local calendar date (midnight, no time of day) the job last decided for.
    public DateTime? LastDecidedOn { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public AgentUser AgentUser { get; set; } = null!;
}
