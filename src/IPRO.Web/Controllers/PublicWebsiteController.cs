using IPRO.DataAccess;
using IPRO.Business.Interfaces;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IPRO.Web.Models;
using System.Net;

namespace IPRO.Web.Controllers;

[AllowAnonymous]
public class PublicWebsiteController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IEmailService _email;
    private readonly ILogger<PublicWebsiteController> _logger;
    private readonly IConfiguration _configuration;

    public PublicWebsiteController(IPRODbContext db, IPackageEntitlementService entitlements, IEmailService email, ILogger<PublicWebsiteController> logger, IConfiguration configuration)
    {
        _db = db;
        _entitlements = entitlements;
        _email = email;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<IActionResult> Index()
    {
        return await RenderPageAsync(null);
    }

    public async Task<IActionResult> Page(string slug)
    {
        return await RenderPageAsync(slug);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitLead(WebsiteLeadFormViewModel model)
    {
        var returnPath = NormalizeReturnPath(model.ReturnPath);
        if (!string.IsNullOrWhiteSpace(model.Website))
        {
            return LocalRedirect(AddResult(returnPath, "submitted", "true"));
        }

        var elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - model.FormStartedAt;
        if (model.FormStartedAt <= 0 || elapsed < 2 || elapsed > 86400)
        {
            TempData["PublicFormError"] = "Please refresh the page and try the form again.";
            return LocalRedirect(returnPath);
        }

        var website = await FindWebsiteForHostAsync(NormalizeHost(Request.Host.Host));
        if (website == null)
        {
            return NotFound();
        }

        var submissionType = string.Equals(model.SubmissionType, WebsiteLeadTypes.Newsletter, StringComparison.OrdinalIgnoreCase)
            ? WebsiteLeadTypes.Newsletter
            : WebsiteLeadTypes.Contact;
        model.FirstName = model.FirstName?.Trim() ?? string.Empty;
        model.LastName = model.LastName?.Trim() ?? string.Empty;
        model.Email = (model.Email?.Trim() ?? string.Empty).ToLowerInvariant();
        model.Phone = model.Phone?.Trim() ?? string.Empty;
        model.Message = model.Message?.Trim() ?? string.Empty;

        if (!ModelState.IsValid || !model.ConsentGiven)
        {
            TempData["PublicFormError"] = "Please provide your name, a valid email address, and consent so the adviser can respond.";
            return LocalRedirect(returnPath);
        }

        var pageId = model.PageId.HasValue && await _db.WebsitePages.AnyAsync(p => p.Id == model.PageId && p.AgentWebsiteId == website.Id)
            ? model.PageId
            : null;
        var duplicateCutoff = DateTime.UtcNow.AddMinutes(-5);
        if (await _db.WebsiteLeads.AnyAsync(l =>
                l.AgentUserId == website.AgentUserId &&
                l.Email == model.Email &&
                l.SubmissionType == submissionType &&
                l.CreatedAt >= duplicateCutoff))
        {
            return LocalRedirect(AddResult(returnPath, "submitted", submissionType.ToLowerInvariant()));
        }

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.AgentUserId == website.AgentUserId && c.Email == model.Email);
        var processingNote = string.Empty;
        if (client == null)
        {
            var access = await _entitlements.GetAccessAsync(website.AgentUserId, PackageFeatureCodes.Contacts);
            var currentCount = await _db.Clients.CountAsync(c => c.AgentUserId == website.AgentUserId);
            var canCreate = access.IsIncluded && (!access.LimitValue.HasValue || access.LimitValue.Value < 0 || currentCount < access.LimitValue.Value);
            if (canCreate)
            {
                client = new Client
                {
                    AgentUserId = website.AgentUserId,
                    FirstName = model.FirstName,
                    LastName = model.LastName,
                    Email = model.Email,
                    Phone = model.Phone,
                    IsNewsletterSubscribed = submissionType == WebsiteLeadTypes.Newsletter,
                    Notes = $"Created from {submissionType.ToLowerInvariant()} submission on {Request.Host.Host}.",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _db.Clients.Add(client);
                await _db.SaveChangesAsync();
            }
            else
            {
                processingNote = "Saved as a website lead but not added to contacts because the package contact limit was reached.";
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(client.Phone) && !string.IsNullOrWhiteSpace(model.Phone)) client.Phone = model.Phone;
            if (string.IsNullOrWhiteSpace(client.FirstName)) client.FirstName = model.FirstName;
            if (string.IsNullOrWhiteSpace(client.LastName)) client.LastName = model.LastName;
            if (submissionType == WebsiteLeadTypes.Newsletter) client.IsNewsletterSubscribed = true;
            client.UpdatedAt = DateTime.UtcNow;
        }

        var sourcePage = returnPath.Length > 500 ? returnPath[..500] : returnPath;
        var lead = new WebsiteLead
        {
            AgentUserId = website.AgentUserId,
            AgentWebsiteId = website.Id,
            WebsitePageId = pageId,
            ClientId = client?.Id,
            SubmissionType = submissionType,
            FirstName = model.FirstName,
            LastName = model.LastName,
            Email = model.Email,
            Phone = model.Phone,
            Message = model.Message,
            SourceDomain = NormalizeHost(Request.Host.Host),
            SourcePage = sourcePage,
            Referrer = Truncate(Request.Headers.Referer.ToString(), 1000),
            IpAddress = Truncate(GetRequestIpAddress(), 64),
            ConsentGiven = model.ConsentGiven,
            ProcessingNote = processingNote,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.WebsiteLeads.Add(lead);

        if (client != null)
        {
            var note = submissionType == WebsiteLeadTypes.Newsletter
                ? $"Subscribed through the website at {lead.SourceDomain}{sourcePage}."
                : $"Website inquiry from {lead.SourceDomain}{sourcePage}." + (string.IsNullOrWhiteSpace(model.Message) ? string.Empty : $" Message: {model.Message}");
            _db.ClientComments.Add(new ClientComment { ClientId = client.Id, Comment = Truncate(note, 4000), CreatedAt = DateTime.UtcNow });
        }

        await _db.SaveChangesAsync();
        await NotifyAgentAsync(website, lead);
        return LocalRedirect(AddResult(returnPath, "submitted", submissionType.ToLowerInvariant()));
    }

    private async Task<IActionResult> RenderPageAsync(string? slug)
    {
        var host = NormalizeHost(Request.Host.Host);
        if (string.IsNullOrWhiteSpace(host))
        {
            return NotFound();
        }

        var website = await FindWebsiteForHostAsync(host);

        if (website == null)
        {
            return View("NotFound", host);
        }

        return await BuildWebsiteViewAsync(website, slug);
    }

    private async Task<IPRO.Entities.AgentWebsite?> FindWebsiteForHostAsync(string host)
    {
        var hostMatches = BuildHostMatches(host);
        var domainMatch = await _db.AgentDomains
            .Include(d => d.AgentWebsite).ThenInclude(w => w.AgentUser)
            .Include(d => d.AgentWebsite).ThenInclude(w => w.Template)
            .FirstOrDefaultAsync(d => hostMatches.Contains(d.DomainName.ToLower()) && d.AgentWebsite.IsPublished);
        if (domainMatch?.AgentWebsite != null) return domainMatch.AgentWebsite;

        return await _db.AgentWebsites
            .Include(w => w.AgentUser)
            .Include(w => w.Template)
            .FirstOrDefaultAsync(w => w.IsPublished &&
                (hostMatches.Contains(w.CustomDomain.ToLower()) || hostMatches.Contains(w.AgentUser.DomainName.ToLower())));
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

    private async Task NotifyAgentAsync(AgentWebsite website, WebsiteLead lead)
    {
        try
        {
            var name = WebUtility.HtmlEncode($"{lead.FirstName} {lead.LastName}".Trim());
            var type = lead.SubmissionType == WebsiteLeadTypes.Newsletter ? "newsletter signup" : "website inquiry";
            var html = $"""
                <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
                  <div style="padding:22px;background:#1457d9;color:white"><h1 style="margin:0;font-size:24px">New IPRO website lead</h1></div>
                  <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                    <p>You received a new <strong>{type}</strong> from <strong>{name}</strong>.</p>
                    <p><strong>Email:</strong> {WebUtility.HtmlEncode(lead.Email)}<br><strong>Phone:</strong> {WebUtility.HtmlEncode(lead.Phone)}<br><strong>Source:</strong> {WebUtility.HtmlEncode(lead.SourceDomain + lead.SourcePage)}</p>
                    <p>{WebUtility.HtmlEncode(lead.Message)}</p>
                    <p><a href="{GetAgentPortalBaseUrl()}/WebsiteLeads" style="display:inline-block;padding:11px 18px;background:#1457d9;color:white;text-decoration:none;border-radius:6px">Open Website Leads</a></p>
                  </div>
                </div>
                """;
            await _email.SendAsync(website.AgentUser.Email, $"{website.AgentUser.FirstName} {website.AgentUser.LastName}".Trim(), $"New website lead: {lead.FirstName} {lead.LastName}".Trim(), html);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Website lead {LeadId} was saved but the agent notification failed.", lead.Id);
        }
    }

    private string GetAgentPortalBaseUrl()
    {
        var configured = _configuration["App:PortalBaseUrl"]?.Trim().TrimEnd('/');
        return Uri.TryCreate(configured, UriKind.Absolute, out _) && !configured!.Contains("YOUR_", StringComparison.OrdinalIgnoreCase)
            ? configured
            : "https://ipro-prod-web.azurewebsites.net";
    }

    private string GetRequestIpAddress()
    {
        var forwarded = Request.Headers["X-Forwarded-For"].ToString().Split(',').FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(forwarded) ? HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty : forwarded;
    }

    private static string NormalizeReturnPath(string? value)
    {
        var path = string.IsNullOrWhiteSpace(value) ? "/" : value.Trim();
        return path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal) ? path.Split('?', '#')[0] : "/";
    }

    private static string AddResult(string path, string key, string value) => $"{path}?{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
    private static string Truncate(string? value, int length) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim()[..Math.Min(value.Trim().Length, length)];
}
