namespace IPRO.Entities;

public class WebsitePageView
{
    public long Id { get; set; }
    public int AgentWebsiteId { get; set; }
    public int? WebsitePageId { get; set; }
    public string SourceDomain { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string ReferrerHost { get; set; } = string.Empty;
    public string VisitorHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public AgentWebsite AgentWebsite { get; set; } = null!;
    public WebsitePage? WebsitePage { get; set; }
}
