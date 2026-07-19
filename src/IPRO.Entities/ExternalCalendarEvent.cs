namespace IPRO.Entities;

public class ExternalCalendarEvent
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public string GoogleEventId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime StartAt { get; set; }
    public DateTime? EndAt { get; set; }
    public DateTime LastSyncedAt { get; set; } = DateTime.UtcNow;
}
