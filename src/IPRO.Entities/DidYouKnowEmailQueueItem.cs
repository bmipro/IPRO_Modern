namespace IPRO.Entities;

public class DidYouKnowEmailQueueItem
{
    public int Id { get; set; }
    public int ArticleId { get; set; }
    public int ClientId { get; set; }
    public DateTime ScheduledForUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
