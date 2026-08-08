namespace IPRO.Entities;

public static class ECardRecipientStatuses
{
    public const string Queued = "Queued";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
}

public class ECardRecipient
{
    public int Id { get; set; }
    public int ECardId { get; set; }
    public int ClientId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string RecipientName { get; set; } = string.Empty;
    public string Status { get; set; } = ECardRecipientStatuses.Queued;
    public string SendGridMessageId { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;
    // Delivery tracking, written by the SendGrid event webhook. ECardDispatcher has always tagged
    // ecard_recipient_id in customArgs, but until 2026-08-08 the webhook read only
    // newsletter_recipient_id and dropped the rest -- so the events arrived and were discarded, and
    // "Delivered" on the SuperAdmin Card & Letter Activity screen was permanently blank.
    public string LastEvent { get; set; } = string.Empty;
    public DateTime? SentAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? OpenedAt { get; set; }
    public DateTime? ClickedAt { get; set; }
    public DateTime? BouncedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ECard ECard { get; set; } = null!;
    public Client Client { get; set; } = null!;
}
