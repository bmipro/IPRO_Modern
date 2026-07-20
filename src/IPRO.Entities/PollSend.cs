namespace IPRO.Entities;

public enum PollSendStatus { Scheduled, Sending, Sent, Cancelled, Failed }

public class PollSend
{
    public int Id { get; set; }
    public int PollSurveyId { get; set; }
    public int AgentUserId { get; set; }
    public NewsLetterAudienceType AudienceType { get; set; } = NewsLetterAudienceType.AllSubscribers;
    public string AudienceLabel { get; set; } = "All newsletter subscribers";
    public int? ClientCategoryId { get; set; }
    public int? ClientId { get; set; }
    public PollSendStatus Status { get; set; } = PollSendStatus.Scheduled;
    public DateTime ScheduledAt { get; set; }
    public DateTime? SentAt { get; set; }
    public int TotalRecipients { get; set; }
    public int TotalSent { get; set; }
    public int TotalFailed { get; set; }
    public int TotalResponded { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
