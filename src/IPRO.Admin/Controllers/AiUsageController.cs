using System.Security.Claims;
using IPRO.Business.Interfaces;
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
    private readonly IAdminAuditLogService? _auditLog;

    public AiUsageController(IPRODbContext db, IAdminAuditLogService? auditLog = null)
    {
        _db = db;
        _auditLog = auditLog;
    }

    private int CurrentAdminId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    private string CurrentAdminUsername => User.Identity?.Name ?? "unknown";

    // 679: one account's "Draft with AI" use, for the page's table.
    public sealed record AccountUse(int AgentUserId, string Name, int Today, int Last30Days, long InputTokens, long OutputTokens);

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

        // 679 (the owner, 2026-11-25): the drafts each account has a day, and who used them. Each account's day is its
        // own (time zone), so "today" here is the latest day each account has, when that is within the last day.
        ViewBag.DraftsPerDay = await IPRO.Business.Services.AiDraftAllowance.LimitAsync(_db);
        var since = DateTime.UtcNow.Date.AddDays(-30);
        var rows = await _db.AiAdviserDailyUsages.AsNoTracking().Where(u => u.Date >= since).ToListAsync();
        var ids = rows.Select(r => r.AgentUserId).Distinct().ToList();
        var names = await _db.AgentUsers.AsNoTracking().Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.FirstName, a.LastName, a.CompanyName, a.UserName })
            .ToDictionaryAsync(a => a.Id, a => string.IsNullOrWhiteSpace(a.CompanyName) ? $"{a.FirstName} {a.LastName}".Trim() + $" ({a.UserName})" : $"{a.CompanyName} ({a.UserName})");
        var yesterday = DateTime.UtcNow.Date.AddDays(-1);
        ViewBag.AccountUse = rows.GroupBy(r => r.AgentUserId)
            .Select(g => new AccountUse(g.Key, names.GetValueOrDefault(g.Key, $"Account {g.Key}"),
                g.Where(r => r.Date >= yesterday).OrderByDescending(r => r.Date).Select(r => r.CallCount).FirstOrDefault(),
                g.Sum(r => r.CallCount), g.Sum(r => r.InputTokens), g.Sum(r => r.OutputTokens)))
            .OrderByDescending(u => u.Last30Days)
            .Take(50)
            .ToList();

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

    // 679 (the owner, 2026-11-25): "Draft with AI" presses each account has a day, one number for everyone.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDraftLimit(int perDay)
    {
        if (IPRO.Business.Services.AiDraftAllowance.LimitProblem(perDay) is { } problem)
        {
            TempData["Error"] = problem;
            return RedirectToAction(nameof(Index));
        }

        var settings = await _db.AiBillingSettings.FirstOrDefaultAsync(s => s.Id == 1);
        if (settings == null)
        {
            settings = new AiBillingSettings { Id = 1 };
            _db.AiBillingSettings.Add(settings);
        }
        var before = settings.DraftsPerAccountPerDay;
        settings.DraftsPerAccountPerDay = perDay;
        settings.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        if (_auditLog != null)
        {
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AiDraftLimit", $"AI drafts per account a day changed from {before} to {perDay}.");
        }

        TempData["Success"] = $"Each account now has {perDay} AI drafts a day.";
        return RedirectToAction(nameof(Index));
    }
}
