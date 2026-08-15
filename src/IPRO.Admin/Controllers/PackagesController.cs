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
                SetupFeeWaived = p.SetupFeeWaived,
                SetupFeeWaivedUntil = p.SetupFeeWaivedUntil,
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
                IsActive = p.IsActive,
                IsTrialPackage = p.IsTrialPackage
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

        // PackageName is a natural key: the entitlement seeder (every startup, both apps), the
        // legacy package-number mapping and the QA seeder all resolve packages BY NAME. A second
        // "IPro Gold" would make agents resolve to whichever row MySQL returns first -- wrong
        // features, wrong price, wrong PayPal plan. The UX_BillingRules_PackageName unique index is
        // the backstop; this check gives the admin a message instead of an exception page.
        var newName = model.PackageName?.Trim() ?? string.Empty;
        if (await _uow.BillingRules.CountAsync(r => r.PackageName == newName) > 0)
        {
            ModelState.AddModelError(nameof(model.PackageName),
                $"A package named '{newName}' already exists. Package names must be unique -- billing and " +
                "entitlements resolve packages by name.");
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
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "PackageCreate", $"Package '{rule.PackageName}' created (monthly ${rule.MonthlyPrice}, annual ${rule.AnnualPrice}, setup ${rule.SetupFee}{DescribeWaiver(rule)})");

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

        // Same natural-key guard as Create: renaming onto another package's name collides too.
        var renamedTo = model.PackageName?.Trim() ?? string.Empty;
        if (await _uow.BillingRules.CountAsync(r => r.PackageName == renamedTo && r.Id != model.Id) > 0)
        {
            ModelState.AddModelError(nameof(model.PackageName),
                $"Another package is already named '{renamedTo}'. Package names must be unique -- billing and " +
                "entitlements resolve packages by name.");
            await EnsureFeatureRowsAsync(model);
            await LoadWebsiteTemplatesAsync();
            return View(model);
        }

        ApplyRuleFields(rule, model);
        _uow.BillingRules.Update(rule);
        await _uow.SaveChangesAsync();
        await SaveFeatureRowsAsync(rule.Id, model.Features);
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "PackageEdit", $"Package '{rule.PackageName}' pricing/features updated (monthly ${rule.MonthlyPrice}, annual ${rule.AnnualPrice}, setup ${rule.SetupFee}{DescribeWaiver(rule)})");

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
            SetupFeeWaived = rule.SetupFeeWaived,
            SetupFeeWaivedUntil = rule.SetupFeeWaivedUntil?.Date,
            QuarterlyPrice = rule.QuarterlyPrice == 0 ? null : rule.QuarterlyPrice,
            AnnualPrice = rule.AnnualPrice == 0 ? null : rule.AnnualPrice,
            PayPalMonthlyPlanId = rule.PayPalMonthlyPlanId,
            PayPalAnnualPlanId = rule.PayPalAnnualPlanId,
            PayPalMonthlyPlanPrice = rule.PayPalMonthlyPlanPrice,
            PayPalAnnualPlanPrice = rule.PayPalAnnualPlanPrice,
            MaxClients = rule.MaxClients,
            MaxNewsletters = rule.MaxNewsletters == 0 ? null : rule.MaxNewsletters,
            DefaultWebsiteTemplateId = rule.DefaultWebsiteTemplateId,
            IsActive = rule.IsActive,
            IsTrialPackage = rule.IsTrialPackage,
            TrialDurationDays = rule.TrialDurationDays,
            TrialReminderDayOffsets = rule.TrialReminderDayOffsets,
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

    // Waiving a setup fee changes what customers are charged, so it belongs in the audit trail
    // alongside the price itself, not just in the row.
    private static string DescribeWaiver(BillingRule rule) => rule.SetupFeeWaived
        ? rule.SetupFeeWaivedUntil.HasValue
            ? $", setup fee WAIVED until {rule.SetupFeeWaivedUntil.Value:yyyy-MM-dd}"
            : ", setup fee WAIVED (no end date)"
        : string.Empty;

    private static void ApplyRuleFields(BillingRule rule, PackageEditViewModel model)
    {
        rule.PackageName = model.PackageName;
        rule.Description = model.Description ?? string.Empty;
        rule.MonthlyPrice = model.MonthlyPrice;
        rule.SetupFee = model.SetupFee;
        rule.SetupFeeWaived = model.SetupFeeWaived;
        // The admin picks a date; store the last instant of it so "waived until Sept 30" includes
        // the 30th. Clearing the checkbox also clears the date, so an old date can't linger and
        // silently re-arm the waiver if someone ticks the box again later.
        rule.SetupFeeWaivedUntil = model.SetupFeeWaived && model.SetupFeeWaivedUntil.HasValue
            ? model.SetupFeeWaivedUntil.Value.Date.AddDays(1).AddTicks(-1)
            : null;
        rule.QuarterlyPrice = model.QuarterlyPrice ?? 0;
        rule.AnnualPrice = model.AnnualPrice ?? 0;
        rule.PayPalMonthlyPlanId = model.PayPalMonthlyPlanId ?? string.Empty;
        rule.PayPalAnnualPlanId = model.PayPalAnnualPlanId ?? string.Empty;
        rule.MaxClients = ResolveMaxClients(model);
        rule.MaxNewsletters = model.MaxNewsletters ?? 0;
        rule.DefaultWebsiteTemplateId = model.DefaultWebsiteTemplateId > 0 ? model.DefaultWebsiteTemplateId : null;
        rule.IsActive = model.IsActive;
        rule.IsTrialPackage = model.IsTrialPackage;
        rule.TrialDurationDays = model.IsTrialPackage ? model.TrialDurationDays : null;
        rule.TrialReminderDayOffsets = model.IsTrialPackage ? model.TrialReminderDayOffsets : null;
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
