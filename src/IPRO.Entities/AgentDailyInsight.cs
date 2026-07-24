namespace IPRO.Entities;

public static class AgentDailyInsightActionTypes
{
    public const string OverdueFollowUp = "OverdueFollowUp";
    public const string StaleLead = "StaleLead";
    public const string NoFollowUp = "NoFollowUp";
    public const string None = "None";
}

public class AgentDailyInsight
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public int NewLeadCount { get; set; }
    public int StaleLeadCount { get; set; }
    public int NoFollowUpClientCount { get; set; }
    public string SuggestedActionType { get; set; } = AgentDailyInsightActionTypes.None;
    public string SuggestedActionText { get; set; } = string.Empty;
    public string? SuggestedActionUrl { get; set; }
    public string? SuggestedActionReason { get; set; }
    // The ClientFollowUp/WebsiteLead/Client id the suggestion is about (matches SuggestedActionType),
    // so the dashboard can cheaply re-check whether it's still actionable without another AI call.
    public int? RelatedEntityId { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
