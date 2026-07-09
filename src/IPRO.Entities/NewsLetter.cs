namespace IPRO.Entities;

public enum NewsLetterStatus { Draft, Scheduled, Sending, Sent, Cancelled }

public class NewsLetter
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string HtmlBody { get; set; } = string.Empty;
    public string TextBody { get; set; } = string.Empty;
    public NewsLetterStatus Status { get; set; } = NewsLetterStatus.Draft;
    public DateTime? ScheduledAt { get; set; }
    public DateTime? SentAt { get; set; }
    public int TotalRecipients { get; set; }
    public int TotalSent { get; set; }
    public int TotalOpened { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public AgentUser AgentUser { get; set; } = null!;
    public ICollection<NewsLetterArticle> Articles { get; set; } = new List<NewsLetterArticle>();
    public ICollection<NewsLetterRecipient> Recipients { get; set; } = new List<NewsLetterRecipient>();
}
