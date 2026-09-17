using System.Security.Claims;
using IPRO.Admin.Models;
using IPRO.Business.Interfaces;
using IPRO.DataAccess.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Admin.Controllers;

[Authorize(Policy = "SuperAdmin")]
public class TaxRatesController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly IAdminAuditLogService _auditLog;

    public TaxRatesController(IUnitOfWork uow, IAdminAuditLogService auditLog)
    {
        _uow = uow;
        _auditLog = auditLog;
    }

    private int CurrentAdminId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    private string CurrentAdminUsername => User.Identity?.Name ?? "unknown";

    public async Task<IActionResult> Index()
    {
        return View(await BuildModelAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(TaxRateEditViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var ids = model.Rates.Select(r => r.Id).ToList();
        var taxRatesById = (await _uow.ProvinceTaxRates.FindAsync(x => ids.Contains(x.Id))).ToDictionary(x => x.Id);

        // 492 (audit ADMIN-12): the audit line carries before and after per province, so a wrong HST
        // rate can be reconstructed from the log instead of only "N rows updated".
        var changes = new List<string>();
        foreach (var row in model.Rates)
        {
            if (!taxRatesById.TryGetValue(row.Id, out var taxRate)) continue;

            var newCode = row.ProvinceCode.Trim().ToUpperInvariant();
            var newLabel = row.TaxLabel.Trim();
            var newRate = Math.Round(row.RatePercent / 100m, 5, MidpointRounding.AwayFromZero);
            if (taxRate.ProvinceCode != newCode || taxRate.TaxLabel != newLabel || taxRate.Rate != newRate || taxRate.IsActive != row.IsActive)
            {
                changes.Add($"{taxRate.ProvinceCode} {taxRate.TaxLabel} {taxRate.Rate * 100m:0.###}% {(taxRate.IsActive ? "active" : "inactive")} -> {newCode} {newLabel} {newRate * 100m:0.###}% {(row.IsActive ? "active" : "inactive")}");
            }

            taxRate.ProvinceCode = newCode;
            taxRate.ProvinceName = row.ProvinceName.Trim();
            taxRate.TaxLabel = newLabel;
            taxRate.Rate = newRate;
            taxRate.IsActive = row.IsActive;
            taxRate.UpdatedAt = DateTime.UtcNow;
            _uow.ProvinceTaxRates.Update(taxRate);
        }

        await _uow.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "TaxRatesUpdate", changes.Count == 0
            ? $"Saved {model.Rates.Count} province tax rate(s); no values changed"
            : $"Changed {changes.Count} of {model.Rates.Count} province tax rate(s): {string.Join("; ", changes)}");
        TempData["Success"] = "Tax rates updated.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<TaxRateEditViewModel> BuildModelAsync()
    {
        var rates = await _uow.ProvinceTaxRates.GetAllAsync();
        return new TaxRateEditViewModel
        {
            Rates = rates
                .OrderBy(r => GetSortOrder(r.ProvinceCode))
                .ThenBy(r => r.ProvinceName)
                .Select(r => new TaxRateRowViewModel
                {
                    Id = r.Id,
                    ProvinceCode = r.ProvinceCode,
                    ProvinceName = r.ProvinceName,
                    TaxLabel = r.TaxLabel,
                    RatePercent = r.Rate * 100m,
                    IsActive = r.IsActive
                })
                .ToList()
        };
    }

    private static int GetSortOrder(string code) => code switch
    {
        "AB" => 10,
        "BC" => 20,
        "MB" => 30,
        "NB" => 40,
        "NL" => 50,
        "NS" => 60,
        "ON" => 70,
        "PE" => 80,
        "QC" => 90,
        "SK" => 100,
        "YT" => 110,
        "NT" => 120,
        "NU" => 130,
        _ => 999
    };
}
