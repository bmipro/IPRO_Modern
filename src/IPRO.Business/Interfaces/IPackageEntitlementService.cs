using IPRO.Entities;

namespace IPRO.Business.Interfaces;

public interface IPackageEntitlementService
{
    Task<PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode);
    Task<bool> HasAccessAsync(int agentId, string featureCode);

    /// Batched form of HasAccessAsync for jobs that loop over many agents - resolves every
    /// agent's billing rule and feature access in a handful of fixed queries instead of
    /// 2-4 queries per agent.
    Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode);

    /// True if the agent has no active paid subscription and is outside their trial + grace
    /// window (or was never on a trial at all) - i.e. every paid feature should be blocked and
    /// they should be routed to Billing to subscribe.
    Task<bool> IsAccessGatedAsync(int agentId);
}

public class PackageFeatureAccess
{
    public string FeatureCode { get; set; } = string.Empty;
    public string FeatureName { get; set; } = string.Empty;
    public bool IsIncluded { get; set; }
    public int? LimitValue { get; set; }
    public string LimitLabel { get; set; } = string.Empty;
    public string CurrentPackageName { get; set; } = string.Empty;
    public string RequiredPackageName { get; set; } = string.Empty;

    // 669: set only for file_upload_capacity on a "per user" package. LimitValue is then already the
    // whole account's limit (PerUserLimitMb x Users), so every check and display reads it unchanged.
    public bool PerUser { get; set; }
    public int Users { get; set; } = 1;
    public int? PerUserLimitMb { get; set; }

    public string UpgradeMessage =>
        IsIncluded
            ? string.Empty
            : string.IsNullOrWhiteSpace(RequiredPackageName)
                ? $"This function is not included in your current package."
                : $"This function is included in {RequiredPackageName} and above. Please upgrade your package to use this feature.";
}
