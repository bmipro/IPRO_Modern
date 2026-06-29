namespace IPRO.Entities;

public enum SchedulerStatus { Pending, Running, Completed, Failed }
public enum SchedulerType { Newsletter, DripCampaign, ENote, CalendarReminder }

public class Scheduler
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public SchedulerType Type { get; set; }
    public int ReferenceId { get; set; }
    public SchedulerStatus Status { get; set; } = SchedulerStatus.Pending;
    public DateTime ScheduledAt { get; set; }
    public DateTime? ExecutedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
