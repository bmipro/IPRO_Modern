namespace IPRO.Entities;

public class AgentWebsite
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public int TemplateId { get; set; }
    public string CustomDomain { get; set; } = string.Empty;
    public string SiteTitle { get; set; } = string.Empty;
    public string TagLine { get; set; } = string.Empty;
    public string LogoUrl { get; set; } = string.Empty;
    public string ThemeColor { get; set; } = "#003366";
    public bool IsPublished { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public AgentUser AgentUser { get; set; } = null!;
    public WebsiteTemplate Template { get; set; } = null!;
    public ICollection<AgentDomain> Domains { get; set; } = new List<AgentDomain>();
    public ICollection<WebsitePage> Pages { get; set; } = new List<WebsitePage>();
}
