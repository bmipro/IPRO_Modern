namespace IPRO.Entities;

public enum NewsLetterSendStatus
{
    Scheduled,
    Sending,
    Sent,
    Cancelled,
    Failed
}

public enum NewsLetterAudienceType
{
    AllSubscribers,
    AccountType,
    SelectedClients,
    IndividualClient
}

public class NewsLetterSend
{
    public int Id { get; set; }
    public int NewsLetterId { get; set; }
    public int AgentUserId { get; set; }
    public NewsLetterAudienceType AudienceType { get; set; } = NewsLetterAudienceType.AllSubscribers;
    public string AudienceLabel { get; set; } = "All newsletter subscribers";
    public int? ClientCategoryId { get; set; }
    public int? ClientId { get; set; }
    public NewsLetterSendStatus Status { get; set; } = NewsLetterSendStatus.Scheduled;
    public DateTime ScheduledAt { get; set; }
    public DateTime? SentAt { get; set; }
    public int TotalRecipients { get; set; }
    public int TotalSent { get; set; }
    public int TotalOpened { get; set; }
    public int TotalClicked { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public NewsLetter NewsLetter { get; set; } = null!;
    public AgentUser AgentUser { get; set; } = null!;
    public ClientCategory? ClientCategory { get; set; }
    public Client? Client { get; set; }
    public ICollection<NewsLetterRecipient> Recipients { get; set; } = new List<NewsLetterRecipient>();
}
