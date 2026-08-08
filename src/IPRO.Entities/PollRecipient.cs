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
    // Delivery tracking -- see the note on ECardRecipient. Polls were worse off than cards: the
    // dispatcher sent no customArgs at all, so there was nothing for the webhook to match even if
    // it had looked.
    public string LastEvent { get; set; } = string.Empty;
    public DateTime? SentAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? OpenedAt { get; set; }
    public DateTime? ClickedAt { get; set; }
    public DateTime? BouncedAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public DateTime? RespondedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
