namespace IPRO.Entities;

public enum ClientInvoiceEmailKind { Send, Reminder }
public enum ClientInvoiceEmailStatus { Failed, Sent, Delivered, Bounced }

// 452 (2026-09-02): one row per email an invoice generated -- the send, each resend, each overdue
// reminder. Before this the invoice was stamped Sent before the mail was even attempted and the
// provider's answer was discarded, so an agent could never tell whether the client received it.
// ProviderMessageId is what the delivery pipeline (AzureEmailEventsController) correlates on.
public class ClientInvoiceEmail
{
    public int Id { get; set; }
    public int ClientInvoiceId { get; set; }
    public int AgentUserId { get; set; }
    public int ClientId { get; set; }
    public ClientInvoiceEmailKind Kind { get; set; }
    public ClientInvoiceEmailStatus Status { get; set; }
    public string ToEmail { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string ProviderMessageId { get; set; } = string.Empty;
    public string LastEvent { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;
    public DateTime? SentAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? OpenedAt { get; set; }
    public DateTime? ClickedAt { get; set; }
    public DateTime? BouncedAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ClientInvoice ClientInvoice { get; set; } = null!;
}
