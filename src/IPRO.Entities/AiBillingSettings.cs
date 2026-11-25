namespace IPRO.Entities;

public class AiBillingSettings
{
    public int Id { get; set; }
    public decimal TotalFundedUsd { get; set; }
    public int LowBalanceThresholdPercent { get; set; } = 20;
    // 679 (the owner, 2026-11-25): "Draft with AI" presses per account a day, one number for everyone (AiDraftAllowance).
    public int DraftsPerAccountPerDay { get; set; } = 40;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
