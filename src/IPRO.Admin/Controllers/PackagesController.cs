using System.Security.Claims;
using IPRO.Admin.Models;
using IPRO.Billing;
using IPRO.Business.Interfaces;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Admin.Controllers;

[Authorize(Policy = "SuperAdmin")]
public class PackagesController : Controller
{
    private const int Unlimited = -1;
    private readonly IUnitOfWork _uow;
    private readonly IBillingService _billing;
    private readonly IAdminAuditLogService _auditLog;

    public PackagesController(IUnitOfWork uow, IBillingService billing, IAdminAuditLogService auditLog)
    {
        _uow = uow;
        _billing = billing;
        _auditLog = auditLog;
    }

    private int CurrentAdminId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    private string CurrentAdminUsername => User.Identity?.Name ?? "unknown";

    public async Task<IActionResult> Index()
    {
        var packages = (await _uow.BillingRules.GetAllAsync()).ToList();
        var features = (await _uow.PackageFeatures.GetAllAsync()).ToList();
        var templates = (await _uow.WebsiteTemplates.GetAllAsync())
            .ToDictionary(t => t.Id, t => t.Name);

        var model = packages
            .OrderBy(p => p.MonthlyPrice <= 0 ? decimal.MaxValue : p.MonthlyPrice)
            .ThenBy(p => p.PackageName)
            .Select(p => new PackageListViewModel
            {
                Id = p.Id,
                PackageName = p.PackageName,
                Description = p.Description,
                MonthlyPrice = p.MonthlyPrice,
                AnnualPrice = p.AnnualPrice,
                SetupFee = p.SetupFee,
                ContactsLimit = FormatFeatureLimit(features.FirstOrDefault(f =>
                    f.BillingRuleId == p.Id &&
                    string.Equals(f.FeatureCode, PackageFeatureCodes.Contacts, StringComparison.OrdinalIgnoreCase)), p.MaxClients),
                DomainsLimit = FormatFeatureLimit(features.FirstOrDefault(f =>
                    f.BillingRuleId == p.Id &&
                    string.Equals(f.FeatureCode, PackageFeatureCodes.MultiDomainSupport, StringComparison.OrdinalIgnoreCase))),
                DefaultWebsiteTemplateName = p.DefaultWebsiteTemplateId.HasValue &&
                    templates.TryGetValue(p.DefaultWebsiteTemplateId.Value, out var templateName)
                        ? templateName
                        : "Global default",
                IsActive = p.IsActive
            })
            .ToList();

        return View(model);
    }

    public async Task<IActionResult> Create() =>
        View(await BuildPackageModelAsync(new BillingRule()));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(PackageEditViewModel model)
    {
        if (!ModelState.IsValid)
        {
            await EnsureFeatureRowsAsync(model);
            await LoadWebsiteTemplatesAsync();
            return View(model);
        }

        var rule = new BillingRule();
        ApplyRuleFields(rule, model);
        rule.CreatedAt = DateTime.UtcNow;

        await _uow.BillingRules.AddAsync(rule);
        await _uow.SaveChangesAsync();
        await SaveFeatureRowsAsync(rule.Id, model.Features);
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "PackageCreate", $"Package '{rule.PackageName}' created (monthly ${rule.MonthlyPrice}, annual ${rule.AnnualPrice}, setup ${rule.SetupFee})");

        TempData["Success"] = "Package created.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var rule = await _uow.BillingRules.GetByIdAsync(id);
        if (rule == null) return NotFound();
        return View(await BuildPackageModelAsync(rule));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(PackageEditViewModel model)
    {
        if (!ModelState.IsValid)
        {
            await EnsureFeatureRowsAsync(model);
            await LoadWebsiteTemplatesAsync();
            return View(model);
        }

        var rule = await _uow.BillingRules.GetByIdAsync(model.Id);
        if (rule == null) return NotFound();

        ApplyRuleFields(rule, model);
        _uow.BillingRules.Update(rule);
        await _uow.SaveChangesAsync();
        await SaveFeatureRowsAsync(rule.Id, model.Features);
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "PackageEdit", $"Package '{rule.PackageName}' pricing/features updated");

        TempData["Success"] = "Package updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(int id)
    {
        var rule = await _uow.BillingRules.GetByIdAsync(id);
        if (rule == null) return NotFound();
        rule.IsActive = !rule.IsActive;
        _uow.BillingRules.Update(rule);
        await _uow.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "PackageToggle", $"Package '{rule.PackageName}' {(rule.IsActive ? "activated" : "deactivated")}");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncPayPalPlans(int id)
    {
        var result = await _billing.SyncPayPalPlansAsync(id);
        if (result.Success)
        {
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "PackageSyncPayPalPlans", $"Synced PayPal plans for package id {id}: Monthly {FormatPlanStatus(result.MonthlyPlanId)}, Annual {FormatPlanStatus(result.AnnualPlanId)}");
        }
        TempData[result.Success ? "Success" : "Error"] = result.Success
            ? $"{result.Message} Monthly: {FormatPlanStatus(result.MonthlyPlanId)} Annual: {FormatPlanStatus(result.AnnualPlanId)}"
            : result.Message;

        return RedirectToAction(nameof(Edit), new { id });
    }

    private async Task<PackageEditViewModel> BuildPackageModelAsync(BillingRule rule)
    {
        await LoadWebsiteTemplatesAsync();

        var features = rule.Id == 0
            ? await BuildDefaultFeatureRowsAsync()
            : await BuildExistingFeatureRowsAsync(rule.Id);

        return new PackageEditViewModel
        {
            Id = rule.Id,
            PackageName = rule.PackageName,
            Description = rule.Description,
            MonthlyPrice = rule.MonthlyPrice,
            SetupFee = rule.SetupFee,
            QuarterlyPrice = rule.QuarterlyPrice == 0 ? null : rule.QuarterlyPrice,
            AnnualPrice = rule.AnnualPrice == 0 ? null : rule.AnnualPrice,
            PayPalMonthlyPlanId = rule.PayPalMonthlyPlanId,
            PayPalAnnualPlanId = rule.PayPalAnnualPlanId,
            MaxClients = rule.MaxClients,
            MaxNewsletters = rule.MaxNewsletters == 0 ? null : rule.MaxNewsletters,
            DefaultWebsiteTemplateId = rule.DefaultWebsiteTemplateId,
            IsActive = rule.IsActive,
            Features = features
        };
    }

    private async Task EnsureFeatureRowsAsync(PackageEditViewModel model)
    {
        if (model.Features.Count > 0) return;

        model.Features = model.Id == 0
            ? await BuildDefaultFeatureRowsAsync()
            : await BuildExistingFeatureRowsAsync(model.Id);
    }

    private async Task<List<PackageFeatureEditViewModel>> BuildExistingFeatureRowsAsync(int billingRuleId)
    {
        var existing = (await _uow.PackageFeatures.FindAsync(f => f.BillingRuleId == billingRuleId)).ToList();
        var catalog = await BuildDefaultFeatureRowsAsync();
        var existingByCode = existing.ToDictionary(f => f.FeatureCode, StringComparer.OrdinalIgnoreCase);

        foreach (var catalogFeature in catalog)
        {
            if (existingByCode.ContainsKey(catalogFeature.FeatureCode)) continue;

            existing.Add(new PackageFeature
            {
                BillingRuleId = billingRuleId,
                FeatureCode = catalogFeature.FeatureCode,
                FeatureName = catalogFeature.FeatureName,
                SortOrder = catalogFeature.SortOrder,
                IsIncluded = false
            });
        }

        return existing
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.FeatureName)
            .Select(ToFeatureModel)
            .ToList();
    }

    private async Task<List<PackageFeatureEditViewModel>> BuildDefaultFeatureRowsAsync()
    {
        var allFeatures = (await _uow.PackageFeatures.GetAllAsync()).ToList();

        return allFeatures
            .GroupBy(f => f.FeatureCode)
            .Select(g =>
            {
                var feature = g.OrderBy(f => f.SortOrder).First();
                return new PackageFeatureEditViewModel
                {
                    FeatureCode = feature.FeatureCode,
                    FeatureName = feature.FeatureName,
                    SortOrder = feature.SortOrder,
                    IsIncluded = false
                };
            })
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.FeatureName)
            .ToList();
    }

    private async Task SaveFeatureRowsAsync(int billingRuleId, IEnumerable<PackageFeatureEditViewModel> featureModels)
    {
        var existing = (await _uow.PackageFeatures.FindAsync(f => f.BillingRuleId == billingRuleId))
            .ToDictionary(f => f.FeatureCode, StringComparer.OrdinalIgnoreCase);

        foreach (var featureModel in featureModels)
        {
            if (string.IsNullOrWhiteSpace(featureModel.FeatureCode)) continue;

            if (!existing.TryGetValue(featureModel.FeatureCode, out var feature))
            {
                feature = new PackageFeature
                {
                    BillingRuleId = billingRuleId,
                    FeatureCode = featureModel.FeatureCode,
                    CreatedAt = DateTime.UtcNow
                };
                await _uow.PackageFeatures.AddAsync(feature);
            }

            feature.FeatureName = featureModel.FeatureName;
            feature.IsIncluded = featureModel.IsIncluded;
            feature.LimitValue = featureModel.LimitValue;
            feature.LimitLabel = featureModel.LimitLabel ?? string.Empty;
            feature.SortOrder = featureModel.SortOrder;
        }

        await _uow.SaveChangesAsync();
    }

    private static PackageFeatureEditViewModel ToFeatureModel(PackageFeature feature) => new()
    {
        Id = feature.Id,
        FeatureCode = feature.FeatureCode,
        FeatureName = feature.FeatureName,
        IsIncluded = feature.IsIncluded,
        LimitValue = feature.LimitValue,
        LimitLabel = feature.LimitLabel,
        SortOrder = feature.SortOrder
    };

    private static void ApplyRuleFields(BillingRule rule, PackageEditViewModel model)
    {
        rule.PackageName = model.PackageName;
        rule.Description = model.Description ?? string.Empty;
        rule.MonthlyPrice = model.MonthlyPrice;
        rule.SetupFee = model.SetupFee;
        rule.QuarterlyPrice = model.QuarterlyPrice ?? 0;
        rule.AnnualPrice = model.AnnualPrice ?? 0;
        rule.PayPalMonthlyPlanId = model.PayPalMonthlyPlanId ?? string.Empty;
        rule.PayPalAnnualPlanId = model.PayPalAnnualPlanId ?? string.Empty;
        rule.MaxClients = ResolveMaxClients(model);
        rule.MaxNewsletters = model.MaxNewsletters ?? 0;
        rule.DefaultWebsiteTemplateId = model.DefaultWebsiteTemplateId > 0 ? model.DefaultWebsiteTemplateId : null;
        rule.IsActive = model.IsActive;
    }

    private async Task LoadWebsiteTemplatesAsync()
    {
        ViewBag.WebsiteTemplates = (await _uow.WebsiteTemplates.FindAsync(t => t.IsActive))
            .OrderByDescending(t => t.IsDefault)
            .ThenBy(t => t.Name)
            .ToList();
    }

    private static int ResolveMaxClients(PackageEditViewModel model)
    {
        var contacts = model.Features.FirstOrDefault(f =>
            string.Equals(f.FeatureCode, PackageFeatureCodes.Contacts, StringComparison.OrdinalIgnoreCase));

        if (contacts == null)
        {
            return model.MaxClients;
        }

        if (!contacts.IsIncluded)
        {
            return 0;
        }

        if (contacts.LimitValue.HasValue)
        {
            return contacts.LimitValue.Value;
        }

        if ((contacts.LimitLabel ?? string.Empty).Contains("unlimited", StringComparison.OrdinalIgnoreCase))
        {
            return Unlimited;
        }

        return model.MaxClients > 0 ? model.MaxClients : Unlimited;
    }

    private static string FormatFeatureLimit(PackageFeature? feature, int? fallbackValue = null)
    {
        if (feature?.IsIncluded != true)
        {
            return fallbackValue.HasValue ? FormatLimitNumber(fallbackValue.Value) : "-";
        }

        if (!string.IsNullOrWhiteSpace(feature.LimitLabel))
        {
            return feature.LimitLabel;
        }

        return feature.LimitValue.HasValue ? FormatLimitNumber(feature.LimitValue.Value) : "Included";
    }

    private static string FormatLimitNumber(int value) => value == Unlimited ? "Unlimited" : value.ToString("N0");

    private static string FormatPlanStatus(string planId) => string.IsNullOrWhiteSpace(planId) ? "not created." : planId;
}
