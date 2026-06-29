namespace IPRO.Entities;

public class Testimonial
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int Rating { get; set; } = 5;
    public bool IsPublished { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
