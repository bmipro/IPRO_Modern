namespace IPRO.Entities;

public class TrialInviteCode
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; } = string.Empty;
    public int BillingRuleId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? ExpiresAt { get; set; }
    public int? MaxRedemptions { get; set; }
    public int RedemptionCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public BillingRule? BillingRule { get; set; }
}
