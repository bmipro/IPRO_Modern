namespace IPRO.Web.Models;

public class ClientTimelineItem
{
    public DateTime OccurredAt { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string ColorClass { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string? Url { get; set; }
}
