namespace IPRO.Billing;

public class BillingService
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string PlanId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime NextBillingDate { get; set; }
}