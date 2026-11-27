namespace IPRO.Entities;

public enum PortalAppointmentRequestStatus { Pending, Scheduled, Declined }

public class PortalAppointmentRequest
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    // 683 (audit 2026-11-26, W03-03): what a client may write with a request. The column takes 2,000 characters
    // but the follow-up the adviser's Schedule makes from it takes 1,000: a longer note could be sent and then
    // never scheduled (an error page every time; only Decline worked), and over 2,000 the request itself failed.
    public const int MaxNotesLength = 1000;

    public static string? FitNotes(string? notes)
    {
        var value = notes?.Trim();
        return value is { Length: > MaxNotesLength } ? value[..MaxNotesLength].TrimEnd() : value;
    }

    public string? Notes { get; set; } = string.Empty;
    public DateTime? PreferredDate { get; set; }
    public PortalAppointmentRequestStatus Status { get; set; } = PortalAppointmentRequestStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RespondedAt { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public int? ClientFollowUpId { get; set; }

    public Client Client { get; set; } = null!;
}
