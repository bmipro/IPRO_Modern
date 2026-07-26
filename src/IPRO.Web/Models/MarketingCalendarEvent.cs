namespace IPRO.Web.Models;

public class MarketingCalendarEvent
{
    public DateTime Date { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
}
