using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Admin.Controllers;

[Authorize(Policy = "SuperAdmin")]
public class AiUsageController : Controller
{
    private readonly IPRODbContext _db;

    public AiUsageController(IPRODbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        var settings = await _db.AiBillingSettings.FirstOrDefaultAsync(s => s.Id == 1)
            ?? new AiBillingSettings { Id = 1, TotalFundedUsd = 0, LowBalanceThresholdPercent = 20 };

        var totalSpend = await _db.AiUsageDailyLogs.SumAsync(l => (decimal?)l.EstimatedCostUsd) ?? 0m;
        var remaining = settings.TotalFundedUsd - totalSpend;
        var remainingPercent = settings.TotalFundedUsd > 0 ? (remaining / settings.TotalFundedUsd) * 100m : 0m;

        ViewBag.Settings = settings;
        ViewBag.TotalSpend = totalSpend;
        ViewBag.Remaining = remaining;
        ViewBag.RemainingPercent = remainingPercent;
        ViewBag.IsLow = settings.TotalFundedUsd > 0 && remainingPercent <= settings.LowBalanceThresholdPercent;

        var recentLogs = await _db.AiUsageDailyLogs
            .OrderByDescending(l => l.Date)
            .Take(30)
            .ToListAsync();

        return View(recentLogs);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddFunds(decimal amount)
    {
        if (amount <= 0)
        {
            TempData["Error"] = "Enter a positive amount to record.";
            return RedirectToAction(nameof(Index));
        }

        var settings = await _db.AiBillingSettings.FirstOrDefaultAsync(s => s.Id == 1);
        if (settings == null)
        {
            settings = new AiBillingSettings { Id = 1, LowBalanceThresholdPercent = 20 };
            _db.AiBillingSettings.Add(settings);
        }

        settings.TotalFundedUsd += amount;
        settings.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = $"Recorded ${amount:0.00} in AI credits. Total funded is now ${settings.TotalFundedUsd:0.00}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetThreshold(int percent)
    {
        if (percent is < 1 or > 90)
        {
            TempData["Error"] = "Threshold must be between 1 and 90 percent.";
            return RedirectToAction(nameof(Index));
        }

        var settings = await _db.AiBillingSettings.FirstOrDefaultAsync(s => s.Id == 1);
        if (settings == null)
        {
            settings = new AiBillingSettings { Id = 1 };
            _db.AiBillingSettings.Add(settings);
        }

        settings.LowBalanceThresholdPercent = percent;
        settings.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = $"Low-balance reminder will now trigger at {percent}% remaining.";
        return RedirectToAction(nameof(Index));
    }
}
