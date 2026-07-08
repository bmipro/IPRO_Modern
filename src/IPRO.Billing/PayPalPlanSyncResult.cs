namespace IPRO.Billing;

public class PayPalPlanSyncResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ProductId { get; set; } = string.Empty;
    public string MonthlyPlanId { get; set; } = string.Empty;
    public string AnnualPlanId { get; set; } = string.Empty;

    public static PayPalPlanSyncResult Failed(string message) => new()
    {
        Success = false,
        Message = message
    };
}
