using IPRO.DataAccess;
using IPRO.Business.Interfaces;
using IPRO.Email;
using Microsoft.Extensions.Caching.Memory;
using IPRO.Entities;
using IPRO.Utility;
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
    private readonly IDataProtector _leadMagnetProtector;
    private readonly IBlobStorageService _blob;
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache _cache;

    public PublicWebsiteController(IPRODbContext db, IPackageEntitlementService entitlements, IEmailService email, ILogger<PublicWebsiteController> logger, IConfiguration configuration, IDataProtectionProvider dataProtectionProvider, IBlobStorageService blob, Microsoft.Extensions.Caching.Memory.IMemoryCache cache)
    {
        _db = db;
        _entitlements = entitlements;
        _email = email;
        _logger = logger;
        _configuration = configuration;
        _captchaProtector = dataProtectionProvider.CreateProtector("IPRO.Web.PublicWebsite.Captcha.v1");
        _leadMagnetProtector = dataProtectionProvider.CreateProtector("IPRO.Web.PublicWebsite.LeadMagnetDownload.v1");
        _blob = blob;
        _cache = cache;
    }

    public async Task<IActionResult> Index()
    {
        return await RenderPageAsync(null);
    }

    public async Task<IActionResult> Page(string slug)
    {
        return await RenderPageAsync(slug);
    }

    [HttpGet]
    public async Task<IActionResult> PreviewTemplateForAdmin(string token)
    {
        var sharedSecret = _configuration["AdminPreview:SharedSecret"];
        if (!IPRO.Utility.AdminPreviewToken.TryValidate(token, sharedSecret, out var agentUserId, out var templateId, out var useDefaults))
        {
            return NotFound();
        }

        var model = await IPRO.Web.Infrastructure.TemplatePreviewBuilder.BuildAsync(_db, agentUserId, templateId, useDefaults);
        if (model == null) return NotFound();

        ViewBag.IsTemplatePreview = true;
        return View("~/Views/PublicWebsite/Index.cshtml", model);
    }

    // Canonical host selection. A site bound to a custom domain deliberately stays reachable on
    // its 247advisers subdomain -- certificates renew manually, so the subdomain is the fallback
    // if one lapses. But serving identical content on two hosts, each self-canonicalizing, makes
    // search engines treat them as competing duplicates and split the agent's ranking. So every
    // SEO surface (rel=canonical, og:url, structured data, sitemap URLs, robots' Sitemap line)
    // names ONE origin: the custom domain when it is fully healthy (Azure binding AND SSL both
    // Bound), otherwise the host the request arrived on. Deliberately NOT a 301 -- a hard
    // redirect would take the site down with a lapsed cert; canonical only tells crawlers which
    // copy counts while humans keep working on either host.
    private async Task<string> ResolveCanonicalOriginAsync(IPRO.Entities.AgentWebsite website)
    {
        var requestOrigin = $"{Request.Scheme}://{Request.Host}";
        if (string.IsNullOrWhiteSpace(website.CustomDomain)) return requestOrigin;

        var custom = website.CustomDomain.Trim().ToLowerInvariant();
        if (string.Equals(NormalizeHost(Request.Host.Host), custom, StringComparison.OrdinalIgnoreCase))
        {
            return requestOrigin;
        }

        // "SslBound" alongside the current constant: rows written before the status scheme settled
        // on AgentDomainStatus.Bound carry the legacy value (seen in real data), and a healthy
        // legacy domain must not silently fail this gate.
        var healthy = await _db.AgentDomains.AsNoTracking().AnyAsync(d =>
            d.AgentWebsiteId == website.Id &&
            (d.DomainName.ToLower() == custom || d.WwwDomain.ToLower() == custom || d.RootDomain.ToLower() == custom) &&
            d.AzureBindingStatus == AgentDomainStatus.Bound &&
            (d.SslStatus == AgentDomainStatus.Bound || d.SslStatus == "SslBound"));

        return healthy ? $"https://{custom}" : requestOrigin;
    }

    [HttpGet("/robots.txt")]
    public async Task<IActionResult> Robots()
    {
        var website = await FindWebsiteForHostAsync(NormalizeHost(Request.Host.Host));
        if (website == null) return NotFound();
        var origin = await ResolveCanonicalOriginAsync(website);
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
        var origin = await ResolveCanonicalOriginAsync(website);
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

        var honeypotSubmissionType = ResolveSubmissionType(model.SubmissionType);
        if (!string.IsNullOrWhiteSpace(model.HoneypotField))
        {
            await RecordSpamAttemptAsync(website, WebsiteSpamAttemptReasons.Honeypot, returnPath);
            return LocalRedirect(AddResult(returnPath, "submitted", honeypotSubmissionType.ToLowerInvariant()));
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

        var submissionType = ResolveSubmissionType(model.SubmissionType);
        model.FirstName = model.FirstName?.Trim() ?? string.Empty;
        model.LastName = model.LastName?.Trim() ?? string.Empty;
        model.Email = (model.Email?.Trim() ?? string.Empty).ToLowerInvariant();
        model.Phone = model.Phone?.Trim() ?? string.Empty;
        model.Message = model.Message?.Trim() ?? string.Empty;

        if (!ModelState.IsValid || !model.ConsentGiven)
        {
            var invalidFields = ModelState
                .Where(entry => entry.Value?.ValidationState == Microsoft.AspNetCore.Mvc.ModelBinding.ModelValidationState.Invalid)
                .Select(entry => $"{entry.Key}: {string.Join("; ", entry.Value!.Errors.Select(e => e.ErrorMessage))}");
            _logger.LogWarning(
                "Public lead submission rejected by validation on {Domain}{Page}. ConsentGiven={ConsentGiven}. Invalid fields: {InvalidFields}",
                NormalizeHost(Request.Host.Host), returnPath, model.ConsentGiven, string.Join(" | ", invalidFields));
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
            // Suppressed as a repeat within 5 minutes: NOTHING is created here -- no lead, no client,
            // no queued email. Logged because the visitor still sees the ordinary success message, so
            // without this line a suppressed submission is indistinguishable from a successful one.
            // That ambiguity cost a real debugging session on 2026-08-05.
            _logger.LogInformation(
                "Public {Type} submission from {Email} on {Domain} suppressed as a duplicate within 5 minutes; nothing was created.",
                submissionType, model.Email, NormalizeHost(Request.Host.Host));
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

        if (submissionType == WebsiteLeadTypes.LeadMagnet)
        {
            var downloadToken = await TryIssueLeadMagnetTokenAsync(website, model.LeadMagnetBlockId);
            if (downloadToken != null)
            {
                return LocalRedirect(AddResult(returnPath, "submitted", submissionType.ToLowerInvariant(), "dl", downloadToken));
            }
        }

        if (submissionType == WebsiteLeadTypes.DidYouKnow)
        {
            // The count decides what the visitor is told. Promising "we're emailing you the articles"
            // when zero were queued is a lie the page used to tell in five different situations.
            var queuedCount = await QueueDidYouKnowArticleEmailsAsync(website, model.DidYouKnowBlockId, client);
            return LocalRedirect(AddResult(returnPath, "submitted", submissionType.ToLowerInvariant(),
                "q", queuedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return LocalRedirect(AddResult(returnPath, "submitted", submissionType.ToLowerInvariant()));
    }

    // Queues one email per selected article rather than sending immediately -- a visitor
    // receiving 4-6 emails in the same second is itself a spam signal to mail providers, so
    // sends are staggered a few jittered minutes apart and drained by DidYouKnowEmailDispatchJob
    // (runs every minute). Each email carries one article's full content, so there's no
    // "read more" link back to the site to worry about either.
    // Returns how many emails were queued. Every early exit is logged: this method used to have five
    // silent returns, and the visitor saw "we're emailing you the articles" in all of them. On
    // 2026-08-05 that cost a long debugging session in which neither the agent nor the logs could say
    // whether a submission had queued anything at all. The caller uses the count to decide what the
    // page actually claims.
    private async Task<int> QueueDidYouKnowArticleEmailsAsync(AgentWebsite website, int? blockId, Client? client)
    {
        if (!blockId.HasValue || client == null || string.IsNullOrWhiteSpace(client.Email))
        {
            _logger.LogWarning(
                "Did You Know queueing skipped for agent {AgentId}: blockId={BlockId}, client={ClientState}. " +
                "A missing client usually means the package contact limit was reached.",
                website.AgentUserId, blockId,
                client == null ? "none" : string.IsNullOrWhiteSpace(client.Email) ? "no email" : "ok");
            return 0;
        }

        var block = await _db.WebsiteContentBlocks
            .Include(b => b.WebsitePage)
            .FirstOrDefaultAsync(b => b.Id == blockId.Value
                && b.WebsitePage.AgentWebsiteId == website.Id
                && b.BlockType == WebsiteBlockTypes.DidYouKnow);
        if (block == null)
        {
            _logger.LogWarning(
                "Did You Know queueing skipped: block {BlockId} not found on website {WebsiteId}, or is not a DidYouKnow block.",
                blockId.Value, website.Id);
            return 0;
        }

        var articleIds = WebsiteDidYouKnowSettings.FromJson(block.SettingsJson).ArticleIds;
        if (articleIds.Count == 0)
        {
            _logger.LogWarning(
                "Did You Know block {BlockId} has NO articles selected, so nothing was queued for {Email}. " +
                "The agent needs to pick articles on the block.",
                block.Id, client.Email);
            return 0;
        }

        var articles = await _db.Articles
            .Where(a => articleIds.Contains(a.Id) && a.AgentUserId == website.AgentUserId && a.IsPublished)
            .ToListAsync();

        // Preserve the agent's chosen order, not whatever order the DB returned.
        var ordered = articleIds
            .Select(id => articles.FirstOrDefault(a => a.Id == id))
            .Where(a => a != null)
            .Select(a => a!)
            .ToList();
        if (ordered.Count == 0)
        {
            _logger.LogWarning(
                "Did You Know block {BlockId} references {Count} article(s) but none are published/owned by agent {AgentId}, so nothing was queued.",
                block.Id, articleIds.Count, website.AgentUserId);
            return 0;
        }

        var now = DateTime.UtcNow;

        // Abuse cap (audit item 422g): this queue emails whatever address the visitor typed, with no
        // verification that they own it. The captcha (now single-use) and the SubmitLead rate limit
        // gate the front door, but the queue itself must also refuse to become a mail cannon aimed
        // at someone else's inbox: skip articles this client already has queued or received, and cap
        // the address at 10 queued items per 24 hours. Legitimate visitors never notice either rule.
        var alreadyQueuedArticleIds = await _db.DidYouKnowEmailQueueItems
            .Where(q => q.ClientId == client.Id)
            .Select(q => q.ArticleId)
            .ToListAsync();
        var recentCount = await _db.DidYouKnowEmailQueueItems
            .CountAsync(q => q.ClientId == client.Id && q.CreatedAt > now.AddHours(-24));

        ordered = ordered.Where(a => !alreadyQueuedArticleIds.Contains(a.Id)).ToList();
        var capacity = Math.Max(0, 10 - recentCount);
        if (ordered.Count == 0 || capacity == 0)
        {
            _logger.LogWarning(
                "Did You Know queueing capped for {Email}: {Requested} article(s) requested, {Recent} queued in the last 24h, duplicates removed. Nothing added.",
                client.Email, articleIds.Count, recentCount);
            return 0;
        }
        if (ordered.Count > capacity)
        {
            ordered = ordered.Take(capacity).ToList();
        }

        var scheduledFor = now.AddMinutes(1);
        foreach (var article in ordered)
        {
            _db.DidYouKnowEmailQueueItems.Add(new DidYouKnowEmailQueueItem
            {
                ArticleId = article.Id,
                ClientId = client.Id,
                ScheduledForUtc = scheduledFor,
                CreatedAt = now
            });
            scheduledFor = scheduledFor.AddMinutes(Random.Shared.Next(4, 9));
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Queued {Count} Did You Know article email(s) for {Email} from block {BlockId} (agent {AgentId}).",
            ordered.Count, client.Email, block.Id, website.AgentUserId);
        return ordered.Count;
    }

    private async Task<string?> TryIssueLeadMagnetTokenAsync(AgentWebsite website, int? blockId)
    {
        if (!blockId.HasValue) return null;

        var block = await _db.WebsiteContentBlocks
            .Include(b => b.WebsitePage)
            .FirstOrDefaultAsync(b => b.Id == blockId.Value
                && b.WebsitePage.AgentWebsiteId == website.Id
                && b.BlockType == WebsiteBlockTypes.LeadMagnet);
        if (block == null) return null;

        var agentDocumentId = WebsiteLeadMagnetSettings.FromJson(block.SettingsJson).AgentDocumentId;
        if (agentDocumentId <= 0) return null;

        var documentExists = await _db.AgentDocuments.AnyAsync(d => d.Id == agentDocumentId && d.AgentUserId == website.AgentUserId);
        if (!documentExists) return null;

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        return _leadMagnetProtector.Protect($"{agentDocumentId}|{expiresAt}");
    }

    [HttpGet]
    public async Task<IActionResult> DownloadLeadMagnet(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return NotFound();

        string payload;
        try
        {
            payload = _leadMagnetProtector.Unprotect(token);
        }
        catch
        {
            return NotFound();
        }

        var parts = payload.Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var documentId) || !long.TryParse(parts[1], out var expiresAt))
        {
            return NotFound();
        }
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresAt)
        {
            return NotFound();
        }

        var document = await _db.AgentDocuments.FirstOrDefaultAsync(d => d.Id == documentId);
        if (document == null) return NotFound();

        var stream = await _blob.DownloadAsync(document.BlobUrl);
        if (stream == null) return NotFound();

        var contentType = string.IsNullOrWhiteSpace(document.ContentType) ? "application/octet-stream" : document.ContentType;
        return File(stream, contentType, document.FileName);
    }

    [HttpGet]
    public async Task<IActionResult> Form(int id)
    {
        // A form sent directly (e.g. via a Campaign email) is a standalone resource - it
        // shouldn't require the agent's whole marketing website to be published first.
        var website = await FindWebsiteForHostAsync(NormalizeHost(Request.Host.Host), requirePublished: false);
        if (website == null) return NotFound();

        var formData = await IPRO.Web.Infrastructure.PublicFormBuilder.BuildForFormAsync(_db, id, website.AgentUserId);
        if (formData == null) return NotFound();

        ViewBag.Website = website;
        return View("StandaloneForm", formData);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitCustomForm(WebsiteCustomFormViewModel model)
    {
        var returnPath = NormalizeReturnPath(model.ReturnPath);
        // Matches Form(id)'s relaxed publish requirement above - a visitor reaching this action
        // already has a valid antiforgery token, which only comes from a page that rendered the
        // form (either the standalone route or an embedded page, which still requires publish).
        var website = await FindWebsiteForHostAsync(NormalizeHost(Request.Host.Host), requirePublished: false);
        if (website == null)
        {
            return NotFound();
        }

        if (!string.IsNullOrWhiteSpace(model.HoneypotField))
        {
            await RecordSpamAttemptAsync(website, WebsiteSpamAttemptReasons.Honeypot, returnPath);
            return LocalRedirect(AddResult(returnPath, "submitted", "form", "formId", model.WebsiteFormId.ToString()));
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

        model.FirstName = model.FirstName?.Trim() ?? string.Empty;
        model.LastName = model.LastName?.Trim() ?? string.Empty;
        model.Email = (model.Email?.Trim() ?? string.Empty).ToLowerInvariant();
        model.Phone = model.Phone?.Trim() ?? string.Empty;

        if (!ModelState.IsValid || !model.ConsentGiven)
        {
            TempData["PublicFormError"] = "Please provide your name, a valid email address, and consent so the adviser can respond.";
            return LocalRedirect(returnPath);
        }

        // Re-load the form and its fields server-side - never trust posted field labels/types, only the
        // posted values matched against the field IDs that actually belong to this form for this agent.
        var form = await _db.WebsiteForms.FirstOrDefaultAsync(f => f.Id == model.WebsiteFormId && f.AgentUserId == website.AgentUserId && f.IsActive);
        if (form == null)
        {
            return NotFound();
        }

        var fields = await _db.WebsiteFormFields.Where(f => f.WebsiteFormId == form.Id).OrderBy(f => f.SortOrder).ToListAsync();
        var fieldIds = fields.Select(f => f.Id).ToList();
        var options = await _db.WebsiteFormFieldOptions.Where(o => fieldIds.Contains(o.WebsiteFormFieldId)).ToListAsync();

        var postedAnswers = (model.Answers ?? new List<CustomAnswerInput>())
            .GroupBy(a => a.FieldId)
            .ToDictionary(g => g.Key, g => g.SelectMany(a => a.Values ?? new List<string>()).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList());

        var missingRequired = fields.Any(f => f.FieldType != WebsiteFormFieldTypes.Section && f.IsRequired &&
            (!postedAnswers.TryGetValue(f.Id, out var values) || values.Count == 0));
        if (missingRequired)
        {
            TempData["PublicFormError"] = "Please fill in all required fields.";
            return LocalRedirect(returnPath);
        }

        var pageId = model.PageId.HasValue && await _db.WebsitePages.AnyAsync(p => p.Id == model.PageId && p.AgentWebsiteId == website.Id)
            ? model.PageId
            : null;
        var duplicateCutoff = DateTime.UtcNow.AddMinutes(-5);
        if (await _db.WebsiteLeads.AnyAsync(l =>
                l.AgentUserId == website.AgentUserId &&
                l.Email == model.Email &&
                l.SubmissionType == WebsiteLeadTypes.CustomForm &&
                l.CreatedAt >= duplicateCutoff))
        {
            return LocalRedirect(AddResult(returnPath, "submitted", "form", "formId", form.Id.ToString()));
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
                    Notes = $"Created from a custom form submission on {Request.Host.Host}.",
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
            if (string.IsNullOrWhiteSpace(client.LastName) && !string.IsNullOrWhiteSpace(model.LastName)) client.LastName = model.LastName;
            client.UpdatedAt = DateTime.UtcNow;
        }

        var summaryLines = new List<string>();
        var answerRows = new List<WebsiteFormSubmissionAnswer>();
        var sortOrder = 0;
        foreach (var field in fields.Where(f => f.FieldType != WebsiteFormFieldTypes.Section))
        {
            if (!postedAnswers.TryGetValue(field.Id, out var values) || values.Count == 0) continue;

            var validValues = values;
            if (WebsiteFormFieldTypes.SupportsOptions(field.FieldType))
            {
                var validOptionTexts = options.Where(o => o.WebsiteFormFieldId == field.Id).Select(o => o.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
                validValues = values.Where(v => validOptionTexts.Contains(v)).ToList();
                if (validValues.Count == 0) continue;
            }

            var joined = Truncate(string.Join(", ", validValues), 4000);
            summaryLines.Add($"{field.Label}: {joined}");
            answerRows.Add(new WebsiteFormSubmissionAnswer
            {
                WebsiteFormId = form.Id,
                WebsiteFormFieldId = field.Id,
                FieldLabel = field.Label,
                FieldType = field.FieldType,
                Value = joined,
                SortOrder = sortOrder++
            });
        }

        var sourcePage = returnPath.Length > 500 ? returnPath[..500] : returnPath;
        var lead = new WebsiteLead
        {
            AgentUserId = website.AgentUserId,
            AgentWebsiteId = website.Id,
            WebsitePageId = pageId,
            ClientId = client?.Id,
            SubmissionType = WebsiteLeadTypes.CustomForm,
            FirstName = model.FirstName,
            LastName = model.LastName,
            Email = model.Email,
            Phone = model.Phone,
            Message = Truncate(string.Join(Environment.NewLine, summaryLines), 4000),
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
        await _db.SaveChangesAsync();

        foreach (var row in answerRows)
        {
            row.WebsiteLeadId = lead.Id;
        }
        _db.WebsiteFormSubmissionAnswers.AddRange(answerRows);

        if (client != null)
        {
            var note = $"Custom form submission ({form.Title}) from {lead.SourceDomain}{sourcePage}.";
            _db.ClientComments.Add(new ClientComment { ClientId = client.Id, Comment = Truncate(note, 4000), CreatedAt = DateTime.UtcNow });
        }

        await _db.SaveChangesAsync();
        await NotifyAgentAsync(website, lead);

        return LocalRedirect(AddResult(returnPath, "submitted", "form", "formId", form.Id.ToString()));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitTestimonial(TestimonialFormViewModel model)
    {
        var returnPath = NormalizeReturnPath(model.ReturnPath);
        var website = await FindWebsiteForHostAsync(NormalizeHost(Request.Host.Host));
        if (website == null)
        {
            return NotFound();
        }

        if (!string.IsNullOrWhiteSpace(model.HoneypotField))
        {
            await RecordSpamAttemptAsync(website, WebsiteSpamAttemptReasons.Honeypot, returnPath);
            return LocalRedirect(AddResult(returnPath, "submitted", "testimonial"));
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

        model.FirstName = model.FirstName?.Trim() ?? string.Empty;
        model.LastName = model.LastName?.Trim() ?? string.Empty;
        model.Email = (model.Email?.Trim() ?? string.Empty).ToLowerInvariant();
        model.Body = model.Body?.Trim() ?? string.Empty;

        if (!ModelState.IsValid || !model.ConsentGiven)
        {
            TempData["PublicFormError"] = "Please provide your name, a valid email address, your testimonial, and consent so it can be reviewed.";
            return LocalRedirect(returnPath);
        }

        var duplicateCutoff = DateTime.UtcNow.AddMinutes(-5);
        if (await _db.TestimonialSubmissions.AnyAsync(t =>
                t.AgentUserId == website.AgentUserId &&
                t.Email == model.Email &&
                t.SubmittedAt >= duplicateCutoff))
        {
            return LocalRedirect(AddResult(returnPath, "submitted", "testimonial"));
        }

        _db.TestimonialSubmissions.Add(new TestimonialSubmission
        {
            AgentUserId = website.AgentUserId,
            FirstName = model.FirstName,
            LastName = model.LastName,
            Email = model.Email,
            Body = model.Body,
            Status = TestimonialStatus.Pending,
            SubmittedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        return LocalRedirect(AddResult(returnPath, "submitted", "testimonial"));
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
            // No published website answers for this host. 404 rather than 200 so a site that is not
            // live yet is never indexed as though it were. (If we ever want an unpublished site to
            // keep its search ranking while it is down, 503 is the header to reach for -- but
            // "connected domain, nothing published" is genuinely not-found.)
            Response.StatusCode = StatusCodes.Status404NotFound;
            return View("NotFound", host);
        }

        return await BuildWebsiteViewAsync(website, slug);
    }

    private async Task<IPRO.Entities.AgentWebsite?> FindWebsiteForHostAsync(string host, bool requirePublished = true)
    {
        var hostMatches = BuildHostMatches(host);
        var domainMatch = await _db.AgentDomains
            .Include(d => d.AgentWebsite).ThenInclude(w => w.AgentUser)
            .Include(d => d.AgentWebsite).ThenInclude(w => w.Template)
            .FirstOrDefaultAsync(d =>
                hostMatches.Contains(d.DomainName.ToLower()) ||
                hostMatches.Contains(d.RootDomain.ToLower()) ||
                hostMatches.Contains(d.WwwDomain.ToLower()));

        if (domainMatch?.AgentWebsite != null && (domainMatch.AgentWebsite.IsPublished || !requirePublished))
        {
            return domainMatch.AgentWebsite;
        }

        if (domainMatch != null)
        {
            var websiteQuery = _db.AgentWebsites
                .Include(w => w.AgentUser)
                .Include(w => w.Template)
                .Where(w => w.IsPublished || !requirePublished);

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
            .FirstOrDefaultAsync(w => (w.IsPublished || !requirePublished) &&
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
        var normalizedSlug = NormalizeSlug(slug);

        if (string.IsNullOrWhiteSpace(normalizedSlug))
        {
            // The root. A site with no published pages still renders its default home layout.
            currentPage = pages.FirstOrDefault(p => p.IsHomePage) ?? pages.FirstOrDefault();
        }
        else
        {
            currentPage = pages.FirstOrDefault(p => string.Equals(p.Slug, normalizedSlug, StringComparison.OrdinalIgnoreCase));

            if (currentPage == null)
            {
                // A LIVE site that simply has no page with this slug: an ordinary 404, and it must
                // look like one -- the agent's own header, navigation and footer, with a way back.
                //
                // This used to render the IPRO-branded "Website not published yet" interstitial with
                // HTTP 200. Three things wrong with that: it told visitors a working firm website was
                // unpublished, it invited them to sign in to the agent portal on a domain the public
                // associates with the agent alone, and 200 meant search engines happily indexed every
                // mistyped URL as a real page. The slug-collision fix widened its reach further --
                // every portal controller name (/clients, /newsletter, ...) now lands here.
                //
                // Deliberately not tracked as a page view: a 404 is not a visit to anything.
                Response.StatusCode = StatusCodes.Status404NotFound;
                return View("Index", new PublicWebsiteViewModel
                {
                    Website = website,
                    Pages = pages,
                    CurrentPage = null,
                    PageNotFound = true,
                    CanonicalOrigin = await ResolveCanonicalOriginAsync(website)
                });
            }
        }

        await TrackPageViewAsync(website, currentPage);

        var approvedTestimonials = currentPage?.Blocks.Any(b => b.BlockType == WebsiteBlockTypes.TestimonialForm) == true
            ? await _db.TestimonialSubmissions
                .Where(t => t.AgentUserId == website.AgentUserId && t.Status == TestimonialStatus.Approved)
                .OrderByDescending(t => t.ReviewedAt)
                .ToListAsync()
            : new List<TestimonialSubmission>();

        var pollResultsByBlockId = await IPRO.Web.Infrastructure.PollResultsBuilder.BuildAsync(_db, website.AgentUserId, currentPage);
        var formsByBlockId = await IPRO.Web.Infrastructure.PublicFormBuilder.BuildAsync(_db, website.AgentUserId, currentPage);
        var didYouKnowByBlockId = await IPRO.Web.Infrastructure.DidYouKnowBuilder.BuildAsync(_db, website.AgentUserId, currentPage);
        var articleContentByBlockId = await IPRO.Web.Infrastructure.ArticleContentBuilder.BuildAsync(_db, website.AgentUserId, currentPage);

        return View("Index", new PublicWebsiteViewModel
        {
            Website = website,
            Pages = pages,
            CurrentPage = currentPage,
            CanonicalOrigin = await ResolveCanonicalOriginAsync(website),
            ApprovedTestimonials = approvedTestimonials,
            PollResultsByBlockId = pollResultsByBlockId,
            FormsByBlockId = formsByBlockId,
            DidYouKnowByBlockId = didYouKnowByBlockId,
            ArticleContentByBlockId = articleContentByBlockId
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

    private string GetAgentPortalBaseUrl() => IPRO.Web.Infrastructure.PortalUrlHelper.GetAgentPortalBaseUrl(_configuration);

    // UseForwardedHeaders (Program.cs) has already validated X-Forwarded-For against the trusted
    // proxy list and applied the result to Connection.RemoteIpAddress. Re-reading the raw header
    // here took its leftmost, entirely client-supplied entry -- so a forged header let a visitor
    // choose the IP recorded on their lead, hide behind an untraceable value in the spam log, and
    // inflate unique-visitor analytics arbitrarily (the visitorHash is built from this). Same
    // header-trust class as the rate-limiter bug already fixed in Program.cs.
    private string GetRequestIpAddress() =>
        HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;

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
            // 3 parts since 2026-08-15: answer|issuedAt|nonce. 2-part tokens are still accepted for
            // the 30-minute grace so forms rendered by the previous build don't all fail at deploy,
            // but they age out on their own; every new token carries a nonce.
            if (parts.Length is not (2 or 3) ||
                !int.TryParse(parts[0], out var expected) ||
                !long.TryParse(parts[1], out var issuedAt) ||
                !int.TryParse(answer.Trim(), out var actual))
            {
                return false;
            }

            var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - issuedAt;
            if (age < 0 || age > 1800 || actual != expected)
            {
                return false;
            }

            // SINGLE-USE (audit item 422g). The token used to stay valid for its whole 30-minute
            // window, so a bot that solved one 2+7 could replay the same token+answer pair hundreds
            // of times, turning the captcha into a one-time cost. A successful submission now burns
            // the nonce for the remainder of the token's lifetime; replays fail like a wrong answer.
            // In-memory is sufficient: the app runs single-instance, and the worst case after a
            // restart is one extra use of a token issued in the prior 30 minutes.
            if (parts.Length == 3)
            {
                var nonceKey = $"captcha-used:{parts[2]}";
                if (_cache.TryGetValue(nonceKey, out _))
                {
                    return false;
                }
                _cache.Set(nonceKey, true, TimeSpan.FromMinutes(31));
            }

            return true;
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
    private static string AddResult(string path, string key, string value, string extraKey, string extraValue) =>
        $"{AddResult(path, key, value)}&{Uri.EscapeDataString(extraKey)}={Uri.EscapeDataString(extraValue)}";
    private static string Truncate(string? value, int length) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim()[..Math.Min(value.Trim().Length, length)];

    private static string ResolveSubmissionType(string? submissionType)
    {
        if (string.Equals(submissionType, WebsiteLeadTypes.Newsletter, StringComparison.OrdinalIgnoreCase)) return WebsiteLeadTypes.Newsletter;
        if (string.Equals(submissionType, WebsiteLeadTypes.LeadMagnet, StringComparison.OrdinalIgnoreCase)) return WebsiteLeadTypes.LeadMagnet;
        if (string.Equals(submissionType, WebsiteLeadTypes.DidYouKnow, StringComparison.OrdinalIgnoreCase)) return WebsiteLeadTypes.DidYouKnow;
        return WebsiteLeadTypes.Contact;
    }
}
