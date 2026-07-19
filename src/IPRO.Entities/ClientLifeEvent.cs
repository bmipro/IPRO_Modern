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
    public Client Client { get; set; } = null!;
}
