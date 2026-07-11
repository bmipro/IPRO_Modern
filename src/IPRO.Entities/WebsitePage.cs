namespace IPRO.Entities;

public class WebsitePage
{
    public int Id { get; set; }
    public int AgentWebsiteId { get; set; }
    public int? ParentPageId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string NavigationLabel { get; set; } = string.Empty;
    public string MetaTitle { get; set; } = string.Empty;
    public string MetaDescription { get; set; } = string.Empty;
    public bool IsHomePage { get; set; }
    public bool ShowInNavigation { get; set; } = true;
    public bool IsPublished { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public AgentWebsite AgentWebsite { get; set; } = null!;
    public WebsitePage? ParentPage { get; set; }
    public ICollection<WebsitePage> ChildPages { get; set; } = new List<WebsitePage>();
    public ICollection<WebsiteContentBlock> Blocks { get; set; } = new List<WebsiteContentBlock>();
}
