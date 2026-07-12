namespace IPRO.Entities;

public class WebsiteMediaAsset
{
    public int Id { get; set; }
    public int AgentWebsiteId { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string BlobUrl { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public AgentWebsite AgentWebsite { get; set; } = null!;
}
