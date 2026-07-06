using IPRO.Admin.Models;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Admin.Controllers;

[Authorize]
public class PackagesController : Controller
{
    private readonly IUnitOfWork _uow;

    public PackagesController(IUnitOfWork uow) => _uow = uow;

    public async Task<IActionResult> Index() => View(await _uow.BillingRules.GetAllAsync());

    public async Task<IActionResult> Create() =>
        View(await BuildPackageModelAsync(new BillingRule()));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(PackageEditViewModel model)
    {
        if (!ModelState.IsValid)
        {
            await EnsureFeatureRowsAsync(model);
            return View(model);
        }

        var rule = new BillingRule();
        ApplyRuleFields(rule, model);
        rule.CreatedAt = DateTime.UtcNow;

        await _uow.BillingRules.AddAsync(rule);
        await _uow.SaveChangesAsync();
        await SaveFeatureRowsAsync(rule.Id, model.Features);

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
            return View(model);
        }

        var rule = await _uow.BillingRules.GetByIdAsync(model.Id);
        if (rule == null) return NotFound();

        ApplyRuleFields(rule, model);
        _uow.BillingRules.Update(rule);
        await _uow.SaveChangesAsync();
        await SaveFeatureRowsAsync(rule.Id, model.Features);

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
        return RedirectToAction(nameof(Index));
    }

    private async Task<PackageEditViewModel> BuildPackageModelAsync(BillingRule rule)
    {
        var features = rule.Id == 0
            ? await BuildDefaultFeatureRowsAsync()
            : await BuildExistingFeatureRowsAsync(rule.Id);

        return new PackageEditViewModel
        {
            Id = rule.Id,
            PackageName = rule.PackageName,
            Description = rule.Description,
            MonthlyPrice = rule.MonthlyPrice,
            QuarterlyPrice = rule.QuarterlyPrice,
            AnnualPrice = rule.AnnualPrice,
            PayPalMonthlyPlanId = rule.PayPalMonthlyPlanId,
            PayPalAnnualPlanId = rule.PayPalAnnualPlanId,
            MaxClients = rule.MaxClients,
            MaxNewsletters = rule.MaxNewsletters,
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
        rule.QuarterlyPrice = model.QuarterlyPrice;
        rule.AnnualPrice = model.AnnualPrice;
        rule.PayPalMonthlyPlanId = model.PayPalMonthlyPlanId ?? string.Empty;
        rule.PayPalAnnualPlanId = model.PayPalAnnualPlanId ?? string.Empty;
        rule.MaxClients = model.MaxClients;
        rule.MaxNewsletters = model.MaxNewsletters;
        rule.IsActive = model.IsActive;
    }
}
