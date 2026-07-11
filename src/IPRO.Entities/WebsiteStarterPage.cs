namespace IPRO.Entities;

public class WebsiteStarterPage
{
    public int Id { get; set; }
    public int? BillingRuleId { get; set; }
    public string BusinessType { get; set; } = "All";
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string NavigationLabel { get; set; } = string.Empty;
    public string MetaTitle { get; set; } = string.Empty;
    public string MetaDescription { get; set; } = string.Empty;
    public bool IsHomePage { get; set; }
    public bool ShowInNavigation { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public BillingRule? BillingRule { get; set; }
    public ICollection<WebsiteStarterBlock> Blocks { get; set; } = new List<WebsiteStarterBlock>();
}
