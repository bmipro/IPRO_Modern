using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Business.Services;

public class PackageEntitlementService : IPackageEntitlementService
{
    private readonly IUnitOfWork _uow;
    private readonly IPRODbContext _db;

    public PackageEntitlementService(IUnitOfWork uow, IPRODbContext db)
    {
        _uow = uow;
        _db = db;
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

    public async Task<bool> IsAccessGatedAsync(int agentId)
    {
        var activeBilling = await _uow.Billings.FirstOrDefaultAsync(b =>
            b.AgentUserId == agentId && b.Status == BillingStatus.Active);
        if (activeBilling != null) return false;

        // Cancelled-but-paid-through (DOCS/22): billing has stopped, but the agent paid for this
        // period and keeps access until it ends. Rows cancelled before the design shipped have a
        // null PaidThroughAt and gate immediately, as they always did. Expired counts too (wave-2
        // E): the webhook and reconcile doors write PaidThroughAt on Expired rows since the
        // billing wave, and honoring it on Cancelled only locked a paid-up agent out instantly.
        var paidThrough = await _uow.Billings.FirstOrDefaultAsync(b =>
            b.AgentUserId == agentId &&
            (b.Status == BillingStatus.Cancelled || b.Status == BillingStatus.Expired) &&
            b.PaidThroughAt != null && b.PaidThroughAt > DateTime.UtcNow);
        if (paidThrough != null) return false;

        var agent = await _uow.AgentUsers.GetByIdAsync(agentId);
        // No active subscription and never on a trial (e.g. registered for a paid package
        // directly, or has no account at all) - no free ride, gated immediately.
        if (agent?.TrialEndsAt == null) return true;

        var settings = await _uow.TrialSettings.GetByIdAsync(1);
        var graceDays = settings?.GracePeriodDays ?? 1;
        return DateTime.UtcNow > agent.TrialEndsAt.Value.AddDays(graceDays);
    }

    public async Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode)
    {
        var ids = agentIds.Distinct().ToList();
        var result = new Dictionary<int, bool>();
        if (ids.Count == 0) return result;

        var billingRuleIdByAgent = await ResolveBillingRuleIdsBulkAsync(ids);

        var ruleIds = billingRuleIdByAgent.Values.Where(v => v.HasValue).Select(v => v!.Value).Distinct().ToList();
        var includedRuleIds = ruleIds.Count == 0
            ? new HashSet<int>()
            : (await _db.PackageFeatures
                .Where(f => ruleIds.Contains(f.BillingRuleId) && f.FeatureCode == featureCode && f.IsIncluded)
                .Select(f => f.BillingRuleId)
                .ToListAsync()).ToHashSet();

        foreach (var id in ids)
        {
            result[id] = billingRuleIdByAgent.TryGetValue(id, out var ruleId) && ruleId.HasValue && includedRuleIds.Contains(ruleId.Value);
        }

        return result;
    }

    // Bulk equivalent of ResolveBillingRuleIdAsync + IsAccessGatedAsync's gating check, combined -
    // must stay logically identical to both (active billing wins; otherwise only an agent still
    // inside their trial+grace window falls back to their directly-assigned package).
    private async Task<Dictionary<int, int?>> ResolveBillingRuleIdsBulkAsync(List<int> agentIds)
    {
        var result = new Dictionary<int, int?>();

        // Active rows win; a cancelled-but-paid-through row (DOCS/22) carries the agent's package
        // until it expires. Must stay logically identical to IsAccessGatedAsync above.
        var now = DateTime.UtcNow;
        var activeBillings = await _db.Billings
            .Where(b => agentIds.Contains(b.AgentUserId) &&
                        (b.Status == BillingStatus.Active ||
                         ((b.Status == BillingStatus.Cancelled || b.Status == BillingStatus.Expired) &&
                          b.PaidThroughAt != null && b.PaidThroughAt > now)))
            .ToListAsync();
        var billingRuleIdByBilledAgent = activeBillings
            .GroupBy(b => b.AgentUserId)
            .ToDictionary(g => g.Key,
                g => (int?)g.OrderByDescending(b => b.Status == BillingStatus.Active).First().BillingRuleId);

        var unbilledAgentIds = agentIds.Where(id => !billingRuleIdByBilledAgent.ContainsKey(id)).ToList();

        var unbilledAgentsById = unbilledAgentIds.Count == 0
            ? new Dictionary<int, AgentUser>()
            : await _db.AgentUsers.Where(a => unbilledAgentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id);

        var graceDays = unbilledAgentsById.Count == 0
            ? 1
            : (await _db.TrialSettings.FirstOrDefaultAsync(t => t.Id == 1))?.GracePeriodDays ?? 1;

        var withinTrialAgents = unbilledAgentsById.Values
            .Where(a => a.TrialEndsAt.HasValue && DateTime.UtcNow <= a.TrialEndsAt.Value.AddDays(graceDays))
            .ToList();

        var candidatePackageIds = withinTrialAgents.Select(a => a.PackageId).Where(pid => pid > 0).Distinct().ToList();
        var billingRulesById = candidatePackageIds.Count == 0
            ? new Dictionary<int, BillingRule>()
            : await _db.BillingRules.Where(p => candidatePackageIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        var legacyNameByPackageId = new Dictionary<int, string> { [2] = "IPro Silver", [3] = "IPro Gold", [4] = "IPro Platinum" };
        var legacyNames = legacyNameByPackageId.Values.ToList();
        var billingRulesByName = (await _db.BillingRules.Where(p => legacyNames.Contains(p.PackageName)).ToListAsync())
            .GroupBy(p => p.PackageName)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var id in agentIds)
        {
            if (billingRuleIdByBilledAgent.TryGetValue(id, out var billedRuleId))
            {
                result[id] = billedRuleId;
                continue;
            }

            if (!unbilledAgentsById.TryGetValue(id, out var agent)
                || !agent.TrialEndsAt.HasValue
                || DateTime.UtcNow > agent.TrialEndsAt.Value.AddDays(graceDays)
                || agent.PackageId <= 0)
            {
                result[id] = null;
                continue;
            }

            if (billingRulesById.TryGetValue(agent.PackageId, out var directRule))
            {
                result[id] = directRule.Id;
            }
            else if (legacyNameByPackageId.TryGetValue(agent.PackageId, out var legacyName) && billingRulesByName.TryGetValue(legacyName, out var legacyRule))
            {
                result[id] = legacyRule.Id;
            }
            else
            {
                result[id] = null;
            }
        }

        return result;
    }

    private async Task<int?> ResolveBillingRuleIdAsync(int agentId)
    {
        var activeBilling = await _uow.Billings.FirstOrDefaultAsync(b =>
            b.AgentUserId == agentId && b.Status == BillingStatus.Active);
        if (activeBilling != null)
        {
            return activeBilling.BillingRuleId;
        }

        if (await IsAccessGatedAsync(agentId))
        {
            return null;
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
