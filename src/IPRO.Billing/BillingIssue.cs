namespace IPRO.Billing;

public class BillingIssue
{
    public int BillingId { get; set; }
    public int? InvoiceId { get; set; }
    public string PackageName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal AmountDue { get; set; }
    public string Currency { get; set; } = "CAD";
    public string Message { get; set; } = string.Empty;
}
