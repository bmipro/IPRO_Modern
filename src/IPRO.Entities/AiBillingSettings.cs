namespace IPRO.Entities;

public class AiBillingSettings
{
    public int Id { get; set; }
    public decimal TotalFundedUsd { get; set; }
    public int LowBalanceThresholdPercent { get; set; } = 20;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
