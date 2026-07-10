using IPRO.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[AllowAnonymous]
public class PublicWebsiteController : Controller
{
    private readonly IPRODbContext _db;

    public PublicWebsiteController(IPRODbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index()
    {
        var host = NormalizeHost(Request.Host.Host);
        if (string.IsNullOrWhiteSpace(host))
        {
            return NotFound();
        }

        var website = await _db.AgentWebsites
            .Include(w => w.AgentUser)
            .Include(w => w.Template)
            .FirstOrDefaultAsync(w =>
                w.IsPublished &&
                (w.CustomDomain.ToLower() == host || w.AgentUser.DomainName.ToLower() == host));

        if (website == null)
        {
            return View("NotFound", host);
        }

        return View(website);
    }

    private static string NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;
        return host.Trim().Trim('.').ToLowerInvariant();
    }
}
