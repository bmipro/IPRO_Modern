namespace IPRO.Entities;

public enum ClientLifeEventType
{
    PolicyRenewal,
    Anniversary,
    Other
}

public class ClientLifeEvent
{
    // 683 (audit 2026-11-26, W02-02 and S01-01): the form's limits, kept by the server too. They were only the
    // browser's: a reminder of two billion days, or a label that made the follow-up's title too long for its column,
    // stopped the nightly reminder job for every adviser.
    public const int MaxLabelLength = 200;
    public const int MaxReminderDays = 90;
    public const int DefaultReminderDays = 7;

    public static int ReminderDays(int asked) => asked <= 0 ? DefaultReminderDays : Math.Min(asked, MaxReminderDays);

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
