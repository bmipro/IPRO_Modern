using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Admin.Controllers;

[Authorize(Policy = "SuperAdmin")]
public class PromotionCodesController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly IAdminAuditLogService _auditLog;

    public PromotionCodesController(IUnitOfWork uow, IAdminAuditLogService auditLog)
    {
        _uow = uow;
        _auditLog = auditLog;
    }

    private int CurrentAdminId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    private string CurrentAdminUsername => User.Identity?.Name ?? "unknown";

    public async Task<IActionResult> Index()
    {
        var codes = (await _uow.PromotionCodes.GetAllAsync()).ToList();
        var packages = (await _uow.BillingRules.GetAllAsync()).ToDictionary(p => p.Id, p => p.PackageName);
        ViewBag.PackageNames = packages;
        return View(codes.OrderByDescending(c => c.CreatedAt));
    }

    public async Task<IActionResult> Create()
    {
        ViewBag.Packages = (await _uow.BillingRules.GetAllAsync()).OrderBy(p => p.PackageName).ToList();
        return View("Edit", new PromotionCode { IsActive = true });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var code = await _uow.PromotionCodes.GetByIdAsync(id);
        if (code == null) return NotFound();

        ViewBag.Packages = (await _uow.BillingRules.GetAllAsync()).OrderBy(p => p.PackageName).ToList();
        return View(code);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(PromotionCode model)
    {
        model.Code = model.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        model.Description = model.Description?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(model.Code))
        {
            ModelState.AddModelError(nameof(model.Code), "Code is required.");
        }
        else
        {
            var duplicate = await _uow.PromotionCodes.ExistsAsync(p => p.Id != model.Id && p.Code == model.Code);
            if (duplicate)
            {
                ModelState.AddModelError(nameof(model.Code), "That code is already in use.");
            }
        }

        if (model.RecurringDiscountType != PromoDiscountType.None && model.RestrictedBillingRuleId == null)
        {
            ModelState.AddModelError(nameof(model.RestrictedBillingRuleId), "A recurring-price discount must be restricted to one package (PayPal plans are package-specific).");
        }

        if (model.RecurringDiscountType != PromoDiscountType.None && model.RecurringDiscountValue <= 0)
        {
            ModelState.AddModelError(nameof(model.RecurringDiscountValue), "Enter a discount value greater than zero.");
        }

        if (model.RecurringDiscountType != PromoDiscountType.None && model.RecurringDurationCycles == null && model.RestrictedBillingRuleId != null)
        {
            var restrictedPackage = await _uow.BillingRules.GetByIdAsync(model.RestrictedBillingRuleId.Value);
            if (restrictedPackage != null)
            {
                var discountedMonthly = ComputeDiscountedAmount(restrictedPackage.MonthlyPrice, model.RecurringDiscountType, model.RecurringDiscountValue);
                var discountedAnnual = ComputeDiscountedAmount(restrictedPackage.AnnualPrice, model.RecurringDiscountType, model.RecurringDiscountValue);
                var effectiveSetupFee = model.SetupFeeDiscountType != PromoDiscountType.None
                    ? ComputeDiscountedAmount(restrictedPackage.SetupFee, model.SetupFeeDiscountType, model.SetupFeeDiscountValue)
                    : restrictedPackage.SetupFee;

                // A permanent discount that zeroes out the recurring price is only supported when the setup
                // fee is also fully discounted (a genuine "free forever" comp, activated without PayPal at all).
                // If a setup fee would still be due, there's no way to represent "one-time paid, then free
                // forever" as a single PayPal recurring plan.
                if ((discountedMonthly <= 0 || discountedAnnual <= 0) && effectiveSetupFee > 0)
                {
                    ModelState.AddModelError(nameof(model.RecurringDurationCycles),
                        "A permanent discount can't bring the recurring price to $0 or less while a setup fee is still due — PayPal doesn't support that combination. Either fully discount the setup fee too (a true free-forever comp), set a limited duration instead (e.g. 1 for \"first cycle free\"), or reduce the discount.");
                }
            }
        }

        if (model.SetupFeeDiscountType != PromoDiscountType.None && model.SetupFeeDiscountValue <= 0)
        {
            ModelState.AddModelError(nameof(model.SetupFeeDiscountValue), "Enter a setup fee discount value greater than zero.");
        }

        if (!ModelState.IsValid)
        {
            ViewBag.Packages = (await _uow.BillingRules.GetAllAsync()).OrderBy(p => p.PackageName).ToList();
            return View(model);
        }

        var isNew = model.Id == 0;
        if (isNew)
        {
            model.CreatedAt = DateTime.UtcNow;
            await _uow.PromotionCodes.AddAsync(model);
        }
        else
        {
            var existing = await _uow.PromotionCodes.GetByIdAsync(model.Id);
            if (existing == null) return NotFound();

            var pricingChanged =
                existing.RecurringDiscountType != model.RecurringDiscountType ||
                existing.RecurringDiscountValue != model.RecurringDiscountValue ||
                existing.RecurringDurationCycles != model.RecurringDurationCycles ||
                existing.RestrictedBillingRuleId != model.RestrictedBillingRuleId;

            existing.Code = model.Code;
            existing.Description = model.Description;
            existing.IsActive = model.IsActive;
            existing.ExpiresAt = model.ExpiresAt;
            existing.MaxRedemptions = model.MaxRedemptions;
            existing.RestrictedBillingRuleId = model.RestrictedBillingRuleId;
            existing.RecurringDiscountType = model.RecurringDiscountType;
            existing.RecurringDiscountValue = model.RecurringDiscountValue;
            existing.RecurringDurationCycles = model.RecurringDurationCycles;
            existing.SetupFeeDiscountType = model.SetupFeeDiscountType;
            existing.SetupFeeDiscountValue = model.SetupFeeDiscountValue;

            if (pricingChanged)
            {
                // Cached PayPal plan ids no longer match the new pricing/duration; clear so a fresh plan is created next use.
                existing.PayPalPromoPlanIdMonthly = string.Empty;
                existing.PayPalPromoPlanIdAnnual = string.Empty;
            }

            _uow.PromotionCodes.Update(existing);
        }

        await _uow.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, isNew ? "PromotionCodeCreate" : "PromotionCodeEdit", $"Promotion code '{model.Code}' {(isNew ? "created" : "updated")}");
        TempData["Success"] = "Promotion code saved.";
        return RedirectToAction(nameof(Index));
    }

    private static decimal ComputeDiscountedAmount(decimal original, PromoDiscountType type, decimal value) => type switch
    {
        PromoDiscountType.PercentOff => Math.Max(0, Math.Round(original * (1 - value / 100m), 2)),
        PromoDiscountType.FlatAmountOff => Math.Max(0, original - value),
        _ => original
    };

    public async Task<IActionResult> Redemptions(int id)
    {
        var code = await _uow.PromotionCodes.GetByIdAsync(id);
        if (code == null) return NotFound();

        var redemptions = (await _uow.PromotionCodeRedemptions.FindAsync(r => r.PromotionCodeId == id))
            .OrderByDescending(r => r.RedeemedAt)
            .ToList();
        var agents = (await _uow.AgentUsers.GetAllAsync()).ToDictionary(a => a.Id);
        var packageNames = (await _uow.BillingRules.GetAllAsync()).ToDictionary(p => p.Id, p => p.PackageName);

        ViewBag.Code = code;
        ViewBag.Agents = agents;
        ViewBag.PackageNames = packageNames;
        return View(redemptions);
    }
}
