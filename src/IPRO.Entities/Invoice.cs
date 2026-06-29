namespace IPRO.Entities;

public class Invoice
{
    public int Id { get; set; }
    public int BillingId { get; set; }
    public int AgentUserId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public decimal SubTotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = "CAD";
    public string PayPalTransactionId { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public bool IsPaid { get; set; } = false;
    public Billing Billing { get; set; } = null!;
}
