namespace IPRO.Entities;

public class WebsiteTemplate
{
    public int Id { get; set; }
    public string TemplateKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string BusinessType { get; set; } = string.Empty;
    public string? PreviewImageUrl { get; set; }
    public string LayoutJson { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<AgentWebsite> Websites { get; set; } = new List<AgentWebsite>();
}
