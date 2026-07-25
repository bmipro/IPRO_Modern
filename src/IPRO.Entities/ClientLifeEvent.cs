namespace IPRO.Entities;

public enum ClientLifeEventType
{
    PolicyRenewal,
    Anniversary,
    Other
}

public class ClientLifeEvent
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public ClientLifeEventType EventType { get; set; }
    public string Label { get; set; } = string.Empty;
    public DateTime EventDate { get; set; }
    public int ReminderDaysBefore { get; set; } = 7;
    public bool IsActive { get; set; } = true;
    public int? LastReminderYear { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Fair-rotation marker, distinct from LastReminderYear (which tracks whether THIS year's
    // reminder was already sent). Updated every time the job evaluates this row, whether or not
    // a reminder was due, so an unbounded Take() ordered by this column can't permanently starve
    // rows past the cutoff once there are more active rows than the cap.
    public DateTime? LastCheckedAt { get; set; }
    public Client Client { get; set; } = null!;
}
