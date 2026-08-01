using IPRO.DataAccess;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

// Unauthenticated, pre-signup "see your site before you sign up" preview. Every action here is a
// GET (nothing persists, so no CSRF token is needed and the result is bookmarkable/shareable) and
// causes zero database writes -- see ProspectWebsitePreviewBuilder for why that's possible.
[AllowAnonymous]
public class PreviewController : Controller
{
    private readonly IPRODbContext _db;
    public PreviewController(IPRODbContext db) { _db = db; }

    [HttpGet]
    public IActionResult Index([FromQuery] string? package = null)
    {
        // Carried from a Home pricing card's "See a live X site in 30s" link -- threaded into the
        // form as a hidden field so submitting doesn't silently drop back to the default package.
        ViewBag.CarriedPackage = ProspectPreviewInput.ValidPackages.Contains(package) ? package : null;
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> Show([FromQuery] ProspectPreviewInput input)
    {
        var prospect = input.Normalized();
        // Drives the package-context card on Show.cshtml -- the plan this preview is showing, so
        // it can carry a real price/setup-fee alongside the fabricated live site instead of neither.
        ViewBag.SelectedPackage = await _db.BillingRules.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PackageName == prospect.Package);
        return View(prospect);
    }

    [HttpGet]
    public async Task<IActionResult> Site([FromQuery] ProspectPreviewInput input)
    {
        var model = await ProspectWebsitePreviewBuilder.BuildAsync(_db, input);
        if (model == null) return NotFound();

        ViewBag.IsProspectPreview = true;
        ViewBag.PreviewNavRouteBase = "/Preview/Site?" + input.Normalized().ToIdentityQueryString();
        return View("~/Views/PublicWebsite/Index.cshtml", model);
    }
}
