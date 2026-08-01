using IPRO.DataAccess;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    public IActionResult Index() => View();

    [HttpGet]
    public IActionResult Show([FromQuery] ProspectPreviewInput input) => View(input.Normalized());

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
