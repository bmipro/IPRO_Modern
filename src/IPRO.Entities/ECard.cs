namespace IPRO.Entities;

public static class ECardStatuses
{
    public const string Draft = "Draft";
    public const string Scheduled = "Scheduled";
    public const string Sending = "Sending";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
}

public class ECard
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    // Holds an ECardTemplateCatalog key (e.g. "halloween-3"). Column kept as `Occasion` from the
    // original schema so no migration is needed; the catalog maps the key to its occasion.
    public string Occasion { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Status { get; set; } = ECardStatuses.Draft;
    public DateTime ScheduledAt { get; set; }
    public DateTime? SentAt { get; set; }
    public int TotalRecipients { get; set; }
    public int TotalSent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public AgentUser AgentUser { get; set; } = null!;
    public ICollection<ECardRecipient> Recipients { get; set; } = new List<ECardRecipient>();
}
