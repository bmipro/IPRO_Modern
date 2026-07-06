using IPRO.Business.Interfaces;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;

namespace IPRO.Business.Services;

public class PackageEntitlementService : IPackageEntitlementService
{
    private readonly IUnitOfWork _uow;

    public PackageEntitlementService(IUnitOfWork uow)
    {
        _uow = uow;
    }

    public async Task<bool> HasAccessAsync(int agentId, string featureCode)
    {
        var access = await GetAccessAsync(agentId, featureCode);
        return access.IsIncluded;
    }

    public async Task<PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode)
    {
        var billingRuleId = await ResolveBillingRuleIdAsync(agentId);
        var currentPackage = billingRuleId.HasValue
            ? await _uow.BillingRules.GetByIdAsync(billingRuleId.Value)
            : null;

        var currentFeature = billingRuleId.HasValue
            ? await _uow.PackageFeatures.FirstOrDefaultAsync(f =>
                f.BillingRuleId == billingRuleId.Value && f.FeatureCode == featureCode)
            : null;

        if (currentFeature?.IsIncluded == true)
        {
            return new PackageFeatureAccess
            {
                FeatureCode = currentFeature.FeatureCode,
                FeatureName = currentFeature.FeatureName,
                IsIncluded = true,
                LimitValue = currentFeature.LimitValue,
                LimitLabel = currentFeature.LimitLabel,
                CurrentPackageName = currentPackage?.PackageName ?? string.Empty
            };
        }

        var required = await FindLowestIncludedPackageAsync(featureCode);
        return new PackageFeatureAccess
        {
            FeatureCode = featureCode,
            FeatureName = currentFeature?.FeatureName ?? required?.FeatureName ?? featureCode,
            IsIncluded = false,
            CurrentPackageName = currentPackage?.PackageName ?? string.Empty,
            RequiredPackageName = required?.PackageName ?? string.Empty
        };
    }

    private async Task<int?> ResolveBillingRuleIdAsync(int agentId)
    {
        var activeBilling = await _uow.Billings.FirstOrDefaultAsync(b =>
            b.AgentUserId == agentId && b.Status == BillingStatus.Active);
        if (activeBilling != null)
        {
            return activeBilling.BillingRuleId;
        }

        var agent = await _uow.AgentUsers.GetByIdAsync(agentId);
        if (agent == null || agent.PackageId <= 0)
        {
            return null;
        }

        var directPackage = await _uow.BillingRules.GetByIdAsync(agent.PackageId);
        if (directPackage != null)
        {
            return directPackage.Id;
        }

        return agent.PackageId switch
        {
            2 => (await _uow.BillingRules.FirstOrDefaultAsync(p => p.PackageName == "IPro Silver"))?.Id,
            3 => (await _uow.BillingRules.FirstOrDefaultAsync(p => p.PackageName == "IPro Gold"))?.Id,
            4 => (await _uow.BillingRules.FirstOrDefaultAsync(p => p.PackageName == "IPro Platinum"))?.Id,
            _ => null
        };
    }

    private async Task<PackageRequirement?> FindLowestIncludedPackageAsync(string featureCode)
    {
        var packages = (await _uow.BillingRules.GetAllAsync())
            .OrderBy(GetPackageRank)
            .ToList();

        foreach (var package in packages)
        {
            var feature = await _uow.PackageFeatures.FirstOrDefaultAsync(f =>
                f.BillingRuleId == package.Id && f.FeatureCode == featureCode && f.IsIncluded);
            if (feature != null)
            {
                return new PackageRequirement(package.PackageName, feature.FeatureName);
            }
        }

        return null;
    }

    private static int GetPackageRank(BillingRule package) => package.PackageName switch
    {
        "IPro Silver" => 1,
        "IPro Gold" => 2,
        "IPro Platinum" => 3,
        "Broker Package" => 4,
        _ => 99
    };

    private sealed record PackageRequirement(string PackageName, string FeatureName);
}
