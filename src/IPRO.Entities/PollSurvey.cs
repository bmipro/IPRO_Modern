namespace IPRO.Entities;

public enum PollSurveyStatus { Draft, Scheduled, Sending, Sent }

public class PollSurvey
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string IntroText { get; set; } = string.Empty;
    public PollSurveyStatus Status { get; set; } = PollSurveyStatus.Draft;
    public DateTime? ScheduledAt { get; set; }
    public DateTime? SentAt { get; set; }
    public int TotalRecipients { get; set; }
    public int TotalSent { get; set; }
    public int TotalFailed { get; set; }
    public int TotalResponded { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
