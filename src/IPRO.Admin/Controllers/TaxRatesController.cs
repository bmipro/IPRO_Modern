using IPRO.Admin.Models;
using IPRO.DataAccess.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Admin.Controllers;

[Authorize]
public class TaxRatesController : Controller
{
    private readonly IUnitOfWork _uow;

    public TaxRatesController(IUnitOfWork uow)
    {
        _uow = uow;
    }

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

        foreach (var row in model.Rates)
        {
            var taxRate = await _uow.ProvinceTaxRates.GetByIdAsync(row.Id);
            if (taxRate == null) continue;

            taxRate.ProvinceCode = row.ProvinceCode.Trim().ToUpperInvariant();
            taxRate.ProvinceName = row.ProvinceName.Trim();
            taxRate.TaxLabel = row.TaxLabel.Trim();
            taxRate.Rate = Math.Round(row.RatePercent / 100m, 5, MidpointRounding.AwayFromZero);
            taxRate.IsActive = row.IsActive;
            taxRate.UpdatedAt = DateTime.UtcNow;
            _uow.ProvinceTaxRates.Update(taxRate);
        }

        await _uow.SaveChangesAsync();
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
