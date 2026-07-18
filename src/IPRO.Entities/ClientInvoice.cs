namespace IPRO.Entities;

public enum ClientInvoiceDocumentType { Estimate, Invoice }
public enum ClientInvoiceStatus { Draft, Sent, Approved, Declined, Paid, Void }
public enum ClientInvoicePaymentMethod { Online, Cheque, Cash, EFT, Other }

public class ClientInvoice
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public int ClientId { get; set; }
    public ClientInvoiceDocumentType DocumentType { get; set; } = ClientInvoiceDocumentType.Invoice;
    public ClientInvoiceStatus Status { get; set; } = ClientInvoiceStatus.Draft;
    public string DocumentNumber { get; set; } = string.Empty;
    public DateTime IssueDate { get; set; } = DateTime.UtcNow;
    public DateTime? DueDate { get; set; }
    public string Currency { get; set; } = "CAD";
    public decimal SubTotal { get; set; }
    public string TaxRegion { get; set; } = string.Empty;
    public decimal TaxRate { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
    public string? Notes { get; set; } = string.Empty;
    public DateTime? PaidAt { get; set; }
    public ClientInvoicePaymentMethod? PaidMethod { get; set; }
    public string ViewToken { get; set; } = string.Empty;
    public DateTime? SentAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public AgentUser AgentUser { get; set; } = null!;
    public Client Client { get; set; } = null!;
    public ICollection<ClientInvoiceLineItem> LineItems { get; set; } = new List<ClientInvoiceLineItem>();
}
