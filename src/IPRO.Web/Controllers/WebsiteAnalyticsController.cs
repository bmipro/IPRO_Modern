using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class WebsiteAnalyticsController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public WebsiteAnalyticsController(IPRODbContext db, IPackageEntitlementService entitlements)
    {
        _db = db;
        _entitlements = entitlements;
    }

    public async Task<IActionResult> Index(int days = 30)
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.VisitorTracking);
        if (!access.IsIncluded)
        {
            TempData["Error"] = access.UpgradeMessage;
            return RedirectToAction("Index", "Billing");
        }

        days = days is 7 or 30 or 90 ? days : 30;
        var website = await _db.AgentWebsites.AsNoTracking().FirstOrDefaultAsync(w => w.AgentUserId == AgentId);
        if (website == null)
        {
            TempData["Error"] = "Create your website before opening analytics.";
            return RedirectToAction("Index", "Website");
        }

        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-days);
        var previousCutoff = cutoff.AddDays(-days);
        var query = _db.WebsitePageViews.AsNoTracking()
            .Where(v => v.AgentWebsiteId == website.Id && v.CreatedAt >= cutoff);

        var totalViews = await query.CountAsync();
        var uniqueVisitors = await query.Select(v => v.VisitorHash).Distinct().CountAsync();
        var previousViews = await _db.WebsitePageViews.AsNoTracking()
            .CountAsync(v => v.AgentWebsiteId == website.Id && v.CreatedAt >= previousCutoff && v.CreatedAt < cutoff);
        var leads = await _db.WebsiteLeads.AsNoTracking()
            .CountAsync(l => l.AgentWebsiteId == website.Id && l.CreatedAt >= cutoff);

        var daily = await query
            .GroupBy(v => v.CreatedAt.Date)
            .Select(group => new WebsiteAnalyticsDailyPoint(
                group.Key,
                group.Count(),
                group.Select(v => v.VisitorHash).Distinct().Count()))
            .OrderBy(point => point.Date)
            .ToListAsync();

        var topPages = await query
            .GroupBy(v => v.Path)
            .Select(group => new WebsiteAnalyticsBreakdown(
                group.Key,
                group.Count(),
                group.Select(v => v.VisitorHash).Distinct().Count()))
            .OrderByDescending(item => item.Views)
            .Take(10)
            .ToListAsync();

        var referrers = await query
            .GroupBy(v => v.ReferrerHost)
            .Select(group => new WebsiteAnalyticsBreakdown(
                group.Key == "" ? "Direct / unknown" : group.Key,
                group.Count(),
                group.Select(v => v.VisitorHash).Distinct().Count()))
            .OrderByDescending(item => item.Views)
            .Take(10)
            .ToListAsync();

        var domains = await query
            .GroupBy(v => v.SourceDomain)
            .Select(group => new WebsiteAnalyticsBreakdown(
                group.Key,
                group.Count(),
                group.Select(v => v.VisitorHash).Distinct().Count()))
            .OrderByDescending(item => item.Views)
            .ToListAsync();

        return View(new WebsiteAnalyticsViewModel
        {
            PeriodDays = days,
            TotalViews = totalViews,
            UniqueVisitors = uniqueVisitors,
            Leads = leads,
            ConversionRate = totalViews == 0 ? 0 : Math.Round(leads * 100m / totalViews, 1),
            ViewChangePercent = previousViews == 0
                ? totalViews == 0 ? 0 : 100
                : Math.Round((totalViews - previousViews) * 100m / previousViews, 1),
            DailyViews = daily,
            TopPages = topPages,
            Referrers = referrers,
            Domains = domains
        });
    }
}
