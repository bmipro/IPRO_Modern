namespace IPRO.Entities;

public enum PortalAppointmentRequestStatus { Pending, Scheduled, Declined }

public class PortalAppointmentRequest
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string? Notes { get; set; } = string.Empty;
    public DateTime? PreferredDate { get; set; }
    public PortalAppointmentRequestStatus Status { get; set; } = PortalAppointmentRequestStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RespondedAt { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public int? ClientFollowUpId { get; set; }

    public Client Client { get; set; } = null!;
}
