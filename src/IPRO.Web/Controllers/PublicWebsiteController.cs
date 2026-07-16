using IPRO.DataAccess;
using IPRO.Business.Interfaces;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IPRO.Web.Models;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace IPRO.Web.Controllers;

[AllowAnonymous]
public class PublicWebsiteController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IEmailService _email;
    private readonly ILogger<PublicWebsiteController> _logger;
    private readonly IConfiguration _configuration;
    private readonly IDataProtector _captchaProtector;

    public PublicWebsiteController(IPRODbContext db, IPackageEntitlementService entitlements, IEmailService email, ILogger<PublicWebsiteController> logger, IConfiguration configuration, IDataProtectionProvider dataProtectionProvider)
    {
        _db = db;
        _entitlements = entitlements;
        _email = email;
        _logger = logger;
        _configuration = configuration;
        _captchaProtector = dataProtectionProvider.CreateProtector("IPRO.Web.PublicWebsite.Captcha.v1");
    }

    public async Task<IActionResult> Index()
    {
        return await RenderPageAsync(null);
    }

    public async Task<IActionResult> Page(string slug)
    {
        return await RenderPageAsync(slug);
    }

    [HttpGet("/robots.txt")]
    public async Task<IActionResult> Robots()
    {
        var website = await FindWebsiteForHostAsync(NormalizeHost(Request.Host.Host));
        if (website == null) return NotFound();
        var origin = $"{Request.Scheme}://{Request.Host}";
        return Content($"User-agent: *\nAllow: /\nSitemap: {origin}/sitemap.xml\n", "text/plain", Encoding.UTF8);
    }

    [HttpGet("/sitemap.xml")]
    public async Task<IActionResult> Sitemap()
    {
        var website = await FindWebsiteForHostAsync(NormalizeHost(Request.Host.Host));
        if (website == null) return NotFound();

        var pages = await _db.WebsitePages
            .AsNoTracking()
            .Where(p => p.AgentWebsiteId == website.Id && p.IsPublished)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Title)
            .ToListAsync();
        var origin = $"{Request.Scheme}://{Request.Host}";
        var urls = pages.Select(page => new
        {
            Location = origin + (page.IsHomePage ? "/" : $"/{page.Slug.Trim('/')}"),
            page.UpdatedAt
        });
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
        foreach (var url in urls)
        {
            xml.Append("  <url><loc>").Append(SecurityElement.Escape(url.Location)).Append("</loc><lastmod>")
                .Append(url.UpdatedAt.ToString("yyyy-MM-dd")).Append("</lastmod></url>\n");
        }
        xml.Append("</urlset>");
        return Content(xml.ToString(), "application/xml", Encoding.UTF8);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitLead(WebsiteLeadFormViewModel model)
    {
        var returnPath = NormalizeReturnPath(model.ReturnPath);
        var website = await FindWebsiteForHostAsync(NormalizeHost(Request.Host.Host));
        if (website == null)
        {
            return NotFound();
        }

        if (!string.IsNullOrWhiteSpace(model.Website))
        {
            await RecordSpamAttemptAsync(website, WebsiteSpamAttemptReasons.Honeypot, returnPath);
            return LocalRedirect(AddResult(returnPath, "submitted", "true"));
        }

        var elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - model.FormStartedAt;
        if (model.FormStartedAt <= 0 || elapsed < 2 || elapsed > 86400)
        {
            await RecordSpamAttemptAsync(website, WebsiteSpamAttemptReasons.Timing, returnPath);
            TempData["PublicFormError"] = "Please refresh the page and try the form again.";
            return LocalRedirect(returnPath);
        }

        if (!IsCaptchaValid(model.CaptchaToken, model.CaptchaAnswer))
        {
            await RecordSpamAttemptAsync(website, WebsiteSpamAttemptReasons.Captcha, returnPath);
            TempData["PublicFormError"] = "Please complete the security check and try again.";
            return LocalRedirect(returnPath);
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
            .FirstOrDefaultAsync(d =>
                hostMatches.Contains(d.DomainName.ToLower()) ||
                hostMatches.Contains(d.RootDomain.ToLower()) ||
                hostMatches.Contains(d.WwwDomain.ToLower()));

        if (domainMatch?.AgentWebsite?.IsPublished == true)
        {
            return domainMatch.AgentWebsite;
        }

        if (domainMatch != null)
        {
            var websiteQuery = _db.AgentWebsites
                .Include(w => w.AgentUser)
                .Include(w => w.Template)
                .Where(w => w.IsPublished);

            if (domainMatch.AgentWebsiteId > 0)
            {
                var websiteById = await websiteQuery.FirstOrDefaultAsync(w => w.Id == domainMatch.AgentWebsiteId);
                if (websiteById != null) return websiteById;
            }

            if (domainMatch.AgentUserId > 0)
            {
                var websiteByAgent = await websiteQuery.FirstOrDefaultAsync(w => w.AgentUserId == domainMatch.AgentUserId);
                if (websiteByAgent != null) return websiteByAgent;
            }
        }

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

        await TrackPageViewAsync(website, currentPage);

        return View("Index", new PublicWebsiteViewModel
        {
            Website = website,
            Pages = pages,
            CurrentPage = currentPage
        });
    }

    private async Task TrackPageViewAsync(AgentWebsite website, WebsitePage? page)
    {
        try
        {
            if (string.Equals(Request.Headers["DNT"], "1", StringComparison.Ordinal)) return;

            var userAgent = Request.Headers["User-Agent"].ToString();
            if (string.IsNullOrWhiteSpace(userAgent) || IsLikelyBot(userAgent)) return;

            var ipAddress = GetRequestIpAddress();
            var monthScope = DateTime.UtcNow.ToString("yyyy-MM");
            var visitorInput = $"{website.Id}|{monthScope}|{ipAddress}|{userAgent}";
            var visitorHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(visitorInput)));
            var referrerHost = string.Empty;
            if (Uri.TryCreate(Request.Headers.Referer.ToString(), UriKind.Absolute, out var referrer) &&
                !string.Equals(NormalizeHost(referrer.Host), NormalizeHost(Request.Host.Host), StringComparison.OrdinalIgnoreCase))
            {
                referrerHost = NormalizeHost(referrer.Host);
            }

            _db.WebsitePageViews.Add(new WebsitePageView
            {
                AgentWebsiteId = website.Id,
                WebsitePageId = page?.Id,
                SourceDomain = Truncate(NormalizeHost(Request.Host.Host), 255),
                Path = page == null || page.IsHomePage ? "/" : $"/{page.Slug.Trim('/')}",
                ReferrerHost = Truncate(referrerHost, 255),
                VisitorHash = visitorHash,
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Public website analytics could not record a view for website {WebsiteId}.", website.Id);
        }
    }

    private static bool IsLikelyBot(string userAgent)
    {
        string[] markers = ["bot", "crawler", "spider", "slurp", "preview", "facebookexternalhit", "whatsapp", "headless"];
        return markers.Any(marker => userAgent.Contains(marker, StringComparison.OrdinalIgnoreCase));
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
            var result = await _email.SendDetailedAsync(website.AgentUser.Email, $"{website.AgentUser.FirstName} {website.AgentUser.LastName}".Trim(), $"New website lead: {lead.FirstName} {lead.LastName}".Trim(), html);
            lead.NotificationSent = result.Success;
            lead.NotificationError = result.Success ? string.Empty : Truncate(result.Message, 500);
        }
        catch (Exception ex)
        {
            lead.NotificationSent = false;
            lead.NotificationError = Truncate(ex.Message, 500);
            _logger.LogWarning(ex, "Website lead {LeadId} was saved but the agent notification failed.", lead.Id);
        }
        finally
        {
            await _db.SaveChangesAsync();
        }
    }

    private async Task RecordSpamAttemptAsync(AgentWebsite website, string reason, string sourcePage)
    {
        _db.WebsiteSpamAttempts.Add(new WebsiteSpamAttempt
        {
            AgentUserId = website.AgentUserId,
            AgentWebsiteId = website.Id,
            Reason = reason,
            SourceDomain = NormalizeHost(Request.Host.Host),
            SourcePage = Truncate(sourcePage, 500),
            IpAddress = Truncate(GetRequestIpAddress(), 64),
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
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

    private bool IsCaptchaValid(string? token, string? answer)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(answer))
        {
            return false;
        }

        try
        {
            var payload = _captchaProtector.Unprotect(token);
            var parts = payload.Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], out var expected) ||
                !long.TryParse(parts[1], out var issuedAt) ||
                !int.TryParse(answer.Trim(), out var actual))
            {
                return false;
            }

            var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - issuedAt;
            return age >= 0 && age <= 1800 && actual == expected;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeReturnPath(string? value)
    {
        var path = string.IsNullOrWhiteSpace(value) ? "/" : value.Trim();
        return path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal) ? path.Split('?', '#')[0] : "/";
    }

    private static string AddResult(string path, string key, string value) => $"{path}?{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
    private static string Truncate(string? value, int length) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim()[..Math.Min(value.Trim().Length, length)];
}
