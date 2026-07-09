namespace IPRO.Entities;

public enum NewsLetterRecipientStatus
{
    Queued,
    Sent,
    Delivered,
    Opened,
    Clicked,
    Bounced,
    Failed,
    Dropped,
    Deferred,
    Unsubscribed
}

public class NewsLetterRecipient
{
    public int Id { get; set; }
    public int NewsLetterId { get; set; }
    public int? NewsLetterSendId { get; set; }
    public int? ClientId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string RecipientName { get; set; } = string.Empty;
    public NewsLetterRecipientStatus Status { get; set; } = NewsLetterRecipientStatus.Queued;
    public string SendGridMessageId { get; set; } = string.Empty;
    public string LastEvent { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;
    public DateTime? SentAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? OpenedAt { get; set; }
    public DateTime? ClickedAt { get; set; }
    public DateTime? BouncedAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public NewsLetter NewsLetter { get; set; } = null!;
    public NewsLetterSend? NewsLetterSend { get; set; }
    public Client? Client { get; set; }
}
