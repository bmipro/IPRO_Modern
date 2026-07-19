namespace IPRO.Entities;

public enum SocialPostStatus
{
    Draft,
    Posted
}

public class SocialPostDraft
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public SocialPostStatus Status { get; set; } = SocialPostStatus.Draft;
    public DateTime? PostedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
