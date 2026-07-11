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

        var hostMatches = BuildHostMatches(host);
        var domainMatch = await _db.AgentDomains
            .Include(d => d.AgentWebsite)
                .ThenInclude(w => w.AgentUser)
            .Include(d => d.AgentWebsite)
                .ThenInclude(w => w.Template)
            .FirstOrDefaultAsync(d =>
                hostMatches.Contains(d.DomainName.ToLower()) &&
                d.AgentWebsite.IsPublished);

        if (domainMatch?.AgentWebsite != null)
        {
            return View(domainMatch.AgentWebsite);
        }

        var website = await _db.AgentWebsites
            .Include(w => w.AgentUser)
            .Include(w => w.Template)
            .FirstOrDefaultAsync(w =>
                w.IsPublished &&
                (hostMatches.Contains(w.CustomDomain.ToLower()) ||
                 hostMatches.Contains(w.AgentUser.DomainName.ToLower())));

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

    private static string[] BuildHostMatches(string host)
    {
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { host };

        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            matches.Add(host[4..]);
        }
        else
        {
            matches.Add("www." + host);
        }

        return matches.ToArray();
    }
}
