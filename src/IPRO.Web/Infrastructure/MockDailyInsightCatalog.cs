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
                "Check in with Diane Foster — no follow-up scheduled since her annual review.",
                "A mid-year check-in is a good moment to flag planning opportunities.")
        },
        // 494 (2026-09-18): this catalog held ONE vertical, so the preview told an accountant, a
        // mortgage broker and anyone else that "her life insurance policy review is 4 days overdue".
        // Each type now has its own three lines, and Generic is the neutral set any other type gets.
        ["Accountants"] = new()
        {
            ["OverdueFollowUp"] = new Entry(3, 5, 4,
                "Call Jennifer Walsh first — her year-end planning call is 4 days overdue.",
                "She is weighing incorporating and wanted the numbers before the quarter closes."),
            ["StaleLead"] = new Entry(3, 5, 4,
                "Follow up with Marcus Chen — he asked about bookkeeping help 2 days ago and hasn't heard back.",
                "Leads contacted within 48 hours are far more likely to convert."),
            ["NoFollowUp"] = new Entry(3, 4, 5,
                "Check in with Diane Foster — no follow-up scheduled since her last filing.",
                "A mid-year check-in is a good moment to flag planning opportunities.")
        },
        ["Mortgage"] = new()
        {
            ["OverdueFollowUp"] = new Entry(3, 5, 4,
                "Call Jennifer Walsh first — her renewal conversation is 4 days overdue.",
                "Her term ends in four months and she wanted to compare rates before her lender's offer arrives."),
            ["StaleLead"] = new Entry(3, 5, 4,
                "Follow up with Marcus Chen — he requested a pre-approval 2 days ago and hasn't heard back.",
                "Leads contacted within 48 hours are far more likely to convert."),
            ["NoFollowUp"] = new Entry(3, 4, 5,
                "Check in with Diane Foster — no follow-up scheduled since her purchase closed.",
                "An annual check-in is where most refinance and referral conversations start.")
        },
        [IPRO.DataAccess.StarterBusinessTypes.Generic] = new()
        {
            ["OverdueFollowUp"] = new Entry(3, 5, 4,
                "Call Jennifer Walsh first — her follow-up is 4 days overdue.",
                "She asked for a proposal at your last meeting and is waiting to hear back."),
            ["StaleLead"] = new Entry(3, 5, 4,
                "Follow up with Marcus Chen — he sent an enquiry through your website 2 days ago and hasn't heard back.",
                "Leads contacted within 48 hours are far more likely to convert."),
            ["NoFollowUp"] = new Entry(3, 4, 5,
                "Check in with Diane Foster — nothing has been scheduled since her last appointment.",
                "A short check-in keeps the relationship warm and often surfaces new work.")
        }
    };

    public static Entry Get(string businessType, string actionType = "OverdueFollowUp")
    {
        // 494: a type with no copy of its own gets the neutral set, not another vertical's.
        var vertical = Catalog.TryGetValue(businessType ?? string.Empty, out var entries) ? entries : Catalog[IPRO.DataAccess.StarterBusinessTypes.Generic];
        return vertical.TryGetValue(actionType, out var entry) ? entry : vertical["OverdueFollowUp"];
    }
}
