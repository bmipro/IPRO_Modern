namespace IPRO.Web.Infrastructure;

// Canned copy for the AI Daily Assistant preview shown to prospects before they sign up.
// Deliberately never calls the real IAiSuggestionService/Anthropic API -- that draws on the same
// shared, monitored, prepaid budget real paying agents use (AiBillingSettings/AiUsageDailyLogs),
// with no natural per-visitor ceiling the way the real once-a-day Hangfire job has. This is a
// small, hand-written matrix instead: zero LLM cost, zero risk, styled to match the real widget
// (Views/Dashboard/Index.cshtml) exactly so it reads as an authentic screenshot.
public static class MockDailyInsightCatalog
{
    public sealed record Entry(
        int NewLeadCount,
        int StaleLeadCount,
        int NoFollowUpClientCount,
        string ActionText,
        string ActionReason);

    // One row per vertical -- always the OverdueFollowUp scenario for v1, since that is the single
    // most compelling example to lead with. All 3 action types exist here per vertical so a future
    // rotation/A-B pass is a trivial fast-follow rather than a redesign.
    private static readonly Dictionary<string, Dictionary<string, Entry>> Catalog = new()
    {
        ["Insurance / Financial"] = new()
        {
            ["OverdueFollowUp"] = new Entry(3, 5, 4,
                "Call Jennifer Walsh first — her life insurance policy review is 4 days overdue.",
                "She mentioned wanting to increase coverage before her policy renews next month."),
            ["StaleLead"] = new Entry(3, 5, 4,
                "Follow up with Marcus Chen — he requested a quote 2 days ago and hasn't heard back.",
                "Leads contacted within 48 hours are far more likely to convert."),
            ["NoFollowUp"] = new Entry(3, 5, 4,
                "Check in with the Hendersons — no follow-up has been scheduled since their last meeting.",
                "It's been over 90 days since your last contact.")
        },
        ["Mortgage"] = new()
        {
            ["OverdueFollowUp"] = new Entry(4, 6, 3,
                "Call David Park first — his pre-approval follow-up is 3 days overdue.",
                "His rate hold expires in two weeks."),
            ["StaleLead"] = new Entry(4, 6, 3,
                "Follow up with Priya Sharma — she inquired about refinancing yesterday.",
                "Rate-sensitive leads go cold fast — a same-day call makes the difference."),
            ["NoFollowUp"] = new Entry(4, 6, 3,
                "Check in with the Alvarez family — no follow-up scheduled since closing.",
                "A post-closing check-in is a natural opening for a referral ask.")
        },
        ["Accountants"] = new()
        {
            ["OverdueFollowUp"] = new Entry(3, 4, 5,
                "Call Robert Kim first — his outstanding tax documents follow-up is 5 days overdue.",
                "The filing deadline is approaching and his return is still incomplete."),
            ["StaleLead"] = new Entry(3, 4, 5,
                "Follow up with Green Valley Bakery — they requested a bookkeeping quote 3 days ago.",
                "Small business leads often compare a few firms quickly."),
            ["NoFollowUp"] = new Entry(3, 4, 5,
                "Check in with Diane Foster — no follow-up scheduled since her last filing.",
                "A mid-year check-in is a good moment to flag planning opportunities.")
        }
    };

    public static Entry Get(string businessType, string actionType = "OverdueFollowUp")
    {
        var vertical = Catalog.TryGetValue(businessType, out var entries) ? entries : Catalog["Insurance / Financial"];
        return vertical.TryGetValue(actionType, out var entry) ? entry : vertical["OverdueFollowUp"];
    }
}
