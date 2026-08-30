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
    private readonly IConfiguration _configuration;

    public HomeController(IPRODbContext db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
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

        // The hero's canned HeroInsight went with the hand-built portal mock: the panels are
        // real screenshots now (mkt-shots 2026-08-30), so nothing on this page invents data.

        // The hero's browser frame advertises the address a new agent is actually issued. Read it
        // from the same config key GenerateUniqueDomainAsync builds against, so marketing can never
        // drift from what signup hands out -- it previously advertised iproadvisers.com while the
        // product issued 247advisers.com.
        ViewBag.TemporaryRootDomain = _configuration["App:TemporarySiteRootDomain"] ?? "247advisers.com";

        return View(ordered);
    }

    public IActionResult Error() => View();

    // Public legal pages. Attribute-routed to bare /terms and /privacy rather than the
    // conventional /Home/Terms, because these get linked from email footers, the signup form
    // and (eventually) external documents, where a short stable URL matters.
    //
    // Both render the same partials the signup acceptance box uses, so the text a subscriber
    // agrees to and the text published here cannot drift apart. See DOCS/legal/.
    [HttpGet("terms")]
    public IActionResult Terms() => View();

    [HttpGet("privacy")]
    public IActionResult Privacy() => View();

    private static int GetPackageRank(BillingRule package) => package.PackageName switch
    {
        "IPro Silver" => 1,
        "IPro Gold" => 2,
        "IPro Platinum" => 3,
        "Broker Package" => 4,
        _ => 50
    };
}
