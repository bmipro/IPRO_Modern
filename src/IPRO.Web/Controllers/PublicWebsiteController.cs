using IPRO.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IPRO.Web.Models;

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
        return await RenderPageAsync(null);
    }

    public async Task<IActionResult> Page(string slug)
    {
        return await RenderPageAsync(slug);
    }

    private async Task<IActionResult> RenderPageAsync(string? slug)
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
            return await BuildWebsiteViewAsync(domainMatch.AgentWebsite, slug);
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

        return await BuildWebsiteViewAsync(website, slug);
    }

    private async Task<IActionResult> BuildWebsiteViewAsync(IPRO.Entities.AgentWebsite website, string? slug)
    {
        var pages = await _db.WebsitePages
            .AsNoTracking()
            .Where(p => p.AgentWebsiteId == website.Id && p.IsPublished)
            .Include(p => p.Blocks)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Title)
            .ToListAsync();

        IPRO.Entities.WebsitePage? currentPage = null;
        if (pages.Count > 0)
        {
            var normalizedSlug = NormalizeSlug(slug);
            currentPage = string.IsNullOrWhiteSpace(normalizedSlug)
                ? pages.FirstOrDefault(p => p.IsHomePage) ?? pages[0]
                : pages.FirstOrDefault(p => string.Equals(p.Slug, normalizedSlug, StringComparison.OrdinalIgnoreCase));
            if (currentPage == null) return View("NotFound", Request.Host.Host);
        }

        return View("Index", new PublicWebsiteViewModel
        {
            Website = website,
            Pages = pages,
            CurrentPage = currentPage
        });
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

    private static string NormalizeSlug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.Trim().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? string.Empty;
    }
}
