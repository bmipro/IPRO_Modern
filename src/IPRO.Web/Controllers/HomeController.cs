using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

// Unlike IPRO.Admin's equivalent, this never surfaces exception details - every authenticated
// Web user is a paying agent or client, not IPRO staff, so there's no elevated role to gate
// diagnostics behind here.
[AllowAnonymous]
public class HomeController : Controller
{
    private readonly IPRODbContext _db;

    public HomeController(IPRODbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Dashboard");
        }

        var packages = await _db.BillingRules
            .AsNoTracking()
            .Include(p => p.Features)
            .Where(p => p.IsActive && !p.IsTrialPackage && !p.IsHiddenTestPackage)
            .ToListAsync();

        var ordered = packages
            .OrderBy(GetPackageRank)
            .ThenBy(p => p.MonthlyPrice <= 0 ? decimal.MaxValue : p.MonthlyPrice)
            .ThenBy(p => p.PackageName)
            .ToList();

        // Same canned example the /Preview flow shows a Insurance/Financial prospect -- reused
        // here (not duplicated) so the hero's "what you'll see" promise stays truthful by
        // construction instead of by manually keeping two copies of the same copy in sync.
        ViewBag.HeroInsight = MockDailyInsightCatalog.Get("Insurance / Financial");

        return View(ordered);
    }

    public IActionResult Error() => View();

    private static int GetPackageRank(BillingRule package) => package.PackageName switch
    {
        "IPro Silver" => 1,
        "IPro Gold" => 2,
        "IPro Platinum" => 3,
        "Broker Package" => 4,
        _ => 50
    };
}
