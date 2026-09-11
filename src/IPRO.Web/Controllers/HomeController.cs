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

        var ordered = await LoadPublicPackagesAsync();

        // The hero's canned HeroInsight went with the hand-built portal mock: the panels are
        // real screenshots now (mkt-shots 2026-08-30), so nothing on this page invents data.

        // The hero's browser frame advertises the address a new agent is actually issued. Read it
        // from the same config key GenerateUniqueDomainAsync builds against, so marketing can never
        // drift from what signup hands out -- it previously advertised iproadvisers.com while the
        // product issued 247advisers.com.
        ViewBag.TemporaryRootDomain = _configuration["App:TemporarySiteRootDomain"] ?? "247advisers.com";

        return View(ordered);
    }

    // 478 (2026-09-11): the accountants landing page, /accountants on the platform host, built from the
    // designer's package (accountants-page-v2-font-revision). The same public packages as the home
    // in the same order fill the pricing slot; the live accountant starter-site preview fills the
    // preview slot; register and preview links carry the business type. It renders for signed-in
    // agents too: a page someone was sent to must not bounce them to the dashboard.
    // iproaccountants.com lands here on the domain-switch day (477). The attribute route gives the
    // page its short address: the default {controller}/{action} route reads /accountants as a
    // controller called Accountants and answers 404 (the first local render did exactly that).
    [AllowAnonymous]
    [HttpGet("/accountants")]
    [HttpGet("/Home/Accountants")]
    public async Task<IActionResult> Accountants()
    {
        ViewBag.TemporaryRootDomain = _configuration["App:TemporarySiteRootDomain"] ?? "247advisers.com";
        return View(await LoadPublicPackagesAsync());
    }

    // The packages the public may buy, in the order the home shows them.
    private async Task<List<BillingRule>> LoadPublicPackagesAsync()
    {
        var packages = await _db.BillingRules
            .AsNoTracking()
            .Include(p => p.Features)
            .Where(p => p.IsActive && !p.IsTrialPackage && !p.IsHiddenTestPackage)
            .ToListAsync();

        return packages
            .OrderBy(GetPackageRank)
            .ThenBy(p => p.MonthlyPrice <= 0 ? decimal.MaxValue : p.MonthlyPrice)
            .ThenBy(p => p.PackageName)
            .ToList();
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
