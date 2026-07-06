namespace IPRO.Billing;

public class BillingChangeResult
{
    public bool Success { get; set; }
    public bool RequiresPayment { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ApprovalUrl { get; set; } = string.Empty;
    public int? InvoiceId { get; set; }
    public decimal AmountDue { get; set; }

    public static BillingChangeResult Failed(string message) => new()
    {
        Success = false,
        Message = message
    };
}
