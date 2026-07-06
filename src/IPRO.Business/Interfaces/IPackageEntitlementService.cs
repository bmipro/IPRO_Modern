using IPRO.Entities;

namespace IPRO.Business.Interfaces;

public interface IPackageEntitlementService
{
    Task<PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode);
    Task<bool> HasAccessAsync(int agentId, string featureCode);
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

    public string UpgradeMessage =>
        IsIncluded
            ? string.Empty
            : string.IsNullOrWhiteSpace(RequiredPackageName)
                ? $"This function is not included in your current package."
                : $"This function is included in {RequiredPackageName} and above. Please upgrade your package to use this feature.";
}
