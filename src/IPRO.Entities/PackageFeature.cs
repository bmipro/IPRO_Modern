namespace IPRO.Entities;

public class PackageFeature
{
    public int Id { get; set; }
    public int BillingRuleId { get; set; }
    public string FeatureCode { get; set; } = string.Empty;
    public string FeatureName { get; set; } = string.Empty;
    public bool IsIncluded { get; set; }
    public int? LimitValue { get; set; }
    public string LimitLabel { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public BillingRule BillingRule { get; set; } = null!;
}
