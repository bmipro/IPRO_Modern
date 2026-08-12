namespace IPRO.Entities;

public class Invoice
{
    public int Id { get; set; }
    public int BillingId { get; set; }
    public int AgentUserId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public decimal SubTotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal TaxRate { get; set; }
    public string TaxRegion { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public string Currency { get; set; } = "CAD";
    public string PayPalTransactionId { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public bool IsPaid { get; set; } = false;

    // Bill-to snapshot, frozen at issue time. An invoice is an accounting record that must outlive its
    // agent (deleted about a month after cancelling, per business practice), so it cannot depend on the
    // AgentUsers row for rendering. BillToAddress is newline-separated display lines.
    public string BillToName { get; set; } = string.Empty;
    public string BillToCompany { get; set; } = string.Empty;
    public string BillToEmail { get; set; } = string.Empty;
    public string BillToAddress { get; set; } = string.Empty;
    public Billing Billing { get; set; } = null!;
    public ICollection<InvoiceLineItem> LineItems { get; set; } = new List<InvoiceLineItem>();
}
