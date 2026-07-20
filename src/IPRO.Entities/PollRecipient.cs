namespace IPRO.Entities;

public enum PollRecipientStatus { Queued, Sent, Failed, Responded }

public class PollRecipient
{
    public int Id { get; set; }
    public int PollSurveyId { get; set; }
    public int? PollSendId { get; set; }
    public int? ClientId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string RecipientName { get; set; } = string.Empty;
    public PollRecipientStatus Status { get; set; } = PollRecipientStatus.Queued;
    public string SendGridMessageId { get; set; } = string.Empty;
    public string VoteToken { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;
    public DateTime? SentAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public DateTime? RespondedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
