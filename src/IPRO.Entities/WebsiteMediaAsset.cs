namespace IPRO.Entities;

public class WebsiteMediaAsset
{
    public int Id { get; set; }
    public int AgentWebsiteId { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string BlobUrl { get; set; } = string.Empty;
    // 578: what the picture shows, for people who cannot see it (its alt text on the website). Empty
    // means the picture is decoration and is skipped by a screen reader.
    public string Description { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    // 571: SHA-256 of the file (lower-case hex), set for images that arrive in a Template Builder
    // package. An image whose bytes the site already stores is linked, not stored again. Empty for
    // images uploaded any other way.
    public string ContentSha256 { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public AgentWebsite AgentWebsite { get; set; } = null!;
}
