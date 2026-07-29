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
    public DateTime? SentAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ECard ECard { get; set; } = null!;
    public Client Client { get; set; } = null!;
}
