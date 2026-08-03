namespace IPRO.Entities;

public class WebsiteStarterArticle
{
    public int Id { get; set; }
    public string BusinessType { get; set; } = "All";
    // Null/empty = flat, one page per article under Resources (existing behavior).
    // Set = grouped, one Resources child page per distinct Category, articles stacked on it.
    public string? Category { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
