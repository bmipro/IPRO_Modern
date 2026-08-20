namespace IPRO.Entities;

public class Article
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    // A5-M-QUOTA: captured at upload so article images count against FileUploadCapacity like
    // everything else. Rows from before 2026-08-20 hold 0 (size never recorded); documented.
    public long ImageSizeBytes { get; set; }
    public bool IsPublished { get; set; } = false;
    public DateTime? PublishedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
