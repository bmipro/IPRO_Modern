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
using PublicFormRetry = IPRO.Web.Infrastructure.PublicFormRetry;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.RateLimiting;
using EndpointRateLimits = IPRO.Web.Infrastructure.EndpointRateLimits;

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
    private readonly IPRO.Business.Services.IEmailConsentService _consent;

    public PublicWebsiteController(IPRODbContext db, IPackageEntitlementService entitlements, IEmailService email, ILogger<PublicWebsiteController> logger, IConfiguration configuration, IDataProtectionProvider dataProtectionProvider, IBlobStorageService blob, Microsoft.Extensions.Caching.Memory.IMemoryCache cache, IPRO.Business.Services.IEmailConsentService consent)
    {
        _db = db;
        _entitlements = entitlements;
        _email = email;
        _logger = logger;
        _configuration = configuration;
        _consent = consent;
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

    // 571: a SuperAdmin's look at a Template Builder package before it is brought into a customer's
    // site (the admin app's BuilderImports review screen links here, one link per page). The admin is
    // not signed in on this host, so the link carries the import's id and the random key kept on its
    // row for an hour; only a package still waiting has a look. Nothing is written and nothing is
    // counted as a visit.
    //
    // A public address, so scanners will find it: a request without a well-formed key is answered
    // before the database is asked anything.
    [HttpGet]
    public async Task<IActionResult> PreviewBuilderImport(int id, string? key, string? page, int? design, string? without, string? keep, string? sections = null, string? moved = null)
    {
        if (id <= 0 || !IPRO.Business.SiteImport.BuilderImportService.LookKeyLooksRight(key)) return NotFound();
        // The key is settled on three small columns. The row itself (a whole site's content) is read
        // only for a request that carries the right key, so a guessed id costs next to nothing.
        var look = await _db.WebsiteBuilderImports.AsNoTracking().Where(i => i.Id == id)
            .Select(i => new IPRO.Entities.WebsiteBuilderImport { Id = i.Id, Status = i.Status, PreviewKey = i.PreviewKey, PreviewKeyExpiresAt = i.PreviewKeyExpiresAt })
            .FirstOrDefaultAsync();
        if (look == null || !IPRO.Business.SiteImport.BuilderImportService.LookIsOpen(look, key, DateTime.UtcNow)) return NotFound();
        var import = await _db.WebsiteBuilderImports.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
        if (import == null) return NotFound();

        var model = await IPRO.Web.Infrastructure.BuilderImportPreviewBuilder.BuildAsync(_db, import, page, design, without, keep, sections, moved);
        if (model == null) return NotFound();

        ViewBag.IsTemplatePreview = true;
        // The three designs show a banner on a preview; this one says what this preview is, and
        // (576) whether the page shown comes from the package or stays as it is.
        ViewBag.PreviewBanner = "A look at a Template Builder package before it is brought in. " + model.PreviewNote + " Nothing on the site has changed.";
        // 585: an adviser's own "See it on my site" from the builder is told it in their words; what an
        // adviser sent (looked at on their review, or on SuperAdmin's) is said plainly for either reader.
        if (import.Source == IPRO.Entities.WebsiteBuilderImportSources.Customer)
            ViewBag.PreviewBanner = import.Status == IPRO.Entities.WebsiteBuilderImportStatus.Preview
                ? "This is how your website would show this page with your changes from the new builder. Nothing on your website has changed. Close this tab to go back to the builder."
                : "A look at what was sent from the new builder, before it is published. " + model.PreviewNote + " Nothing on the website has changed.";
        // 585 (the owner's bench walk-through): the look's links to the site's own pages stay in the look
        // (LookLinks, applied by PublicWebsite/Index.cshtml), and a page it does not have is said so.
        ViewBag.LookLinks = IPRO.Web.Infrastructure.LookLinks.For(id, key!, design, without, keep, sections, moved, model.CurrentPage?.Slug ?? string.Empty);
        if (model.PreviewMissingPage != null)
            ViewBag.PreviewBanner = $"There is no page at /{model.PreviewMissingPage} in this look, so it shows the home page. " + (string)ViewBag.PreviewBanner;
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
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
            d.OwnershipVerifiedAt != null &&   // 592: proven to be this adviser's
            d.AzureBindingStatus == AgentDomainStatus.Bound &&
            (d.SslStatus == AgentDomainStatus.Bound || d.SslStatus == "SslBound"));

        return healthy ? $"https://{custom}" : requestOrigin;
    }

    [HttpGet("/robots.txt")]
    public async Task<IActionResult> Robots()
    {
        // 509: the platform's own public pages. A request served under a brand name arrives here
        // re-addressed to the platform host; PublicHost is the name the visitor used, and robots.txt
        // must name THAT host's sitemap.
        var publicHost = IPRO.Web.Infrastructure.PlatformAliasHosts.PublicHost(HttpContext);
        if (IPRO.Web.Infrastructure.PlatformSeoFiles.IsPlatformOrAlias(_configuration, publicHost))
        {
            return Content(IPRO.Web.Infrastructure.PlatformSeoFiles.Robots(publicHost), "text/plain", Encoding.UTF8);
        }

        var website = await FindWebsiteForHostAsync(NormalizeHost(Request.Host.Host));
        if (website == null) return NotFound();
        // 646: the card at the address of someone who keeps their own website is not for search engines.
        if (await OwnWebsiteForAsync(website.AgentUserId) != null)
            return Content("User-agent: *\nDisallow: /\n", "text/plain", Encoding.UTF8);
        var origin = await ResolveCanonicalOriginAsync(website);
        return Content($"User-agent: *\nAllow: /\nSitemap: {origin}/sitemap.xml\n", "text/plain", Encoding.UTF8);
    }

    [HttpGet("/sitemap.xml")]
    public async Task<IActionResult> Sitemap()
    {
        // 509: see Robots.
        var publicHost = IPRO.Web.Infrastructure.PlatformAliasHosts.PublicHost(HttpContext);
        if (IPRO.Web.Infrastructure.PlatformSeoFiles.IsPlatformOrAlias(_configuration, publicHost))
        {
            return Content(IPRO.Web.Infrastructure.PlatformSeoFiles.Sitemap(_configuration, publicHost), "application/xml", Encoding.UTF8);
        }

        var website = await FindWebsiteForHostAsync(NormalizeHost(Request.Host.Host));
        if (website == null) return NotFound();
        if (await OwnWebsiteForAsync(website.AgentUserId) != null) return NotFound();   // 646

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

    [EnableRateLimiting(EndpointRateLimits.PublicForm)]
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
            return LocalRedirect(AddResult(returnPath, "submitted", SubmittedResult(model, honeypotSubmissionType)));
        }

        if (SentTooFast(model.CaptchaToken, model.FormStartedAt))
        {
            await RecordSpamAttemptAsync(website, WebsiteSpamAttemptReasons.Timing, returnPath);
            return KeepForRetry(returnPath, LeadRetry(model, SendAgainMessage));
        }

        if (!IsCaptchaValid(model.CaptchaToken, model.CaptchaAnswer))
        {
            await RecordSpamAttemptAsync(website, WebsiteSpamAttemptReasons.Captcha, returnPath);
            return KeepForRetry(returnPath, LeadRetry(model, SecurityAnswerMessage).Problem("CaptchaAnswer", "Security check", SecurityAnswerProblem));
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
            return KeepForRetry(returnPath, PersonProblems(LeadRetry(model, FixAndSendMessage), model.ConsentGiven, "so we can reply"));
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
            return LocalRedirect(AddResult(returnPath, "submitted", SubmittedResult(model, submissionType)));
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
                    // 678 (audit D-4, double opt-in): subscribed by their click in the confirmation email, not by the form.
                    IsNewsletterSubscribed = false,
                    Notes = $"Created from {submissionType.ToLowerInvariant()} submission on {Request.Host.Host}.",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                await KeepEarlierOptOutAsync(client);
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
            if (submissionType == WebsiteLeadTypes.Newsletter)
            {
                if (DecidePublicNewsletterConsent(client.EmailOptOutAt.HasValue) == PublicNewsletterConsent.LeaveOptOutInPlace)
                {
                    // The opt-out STANDS. IsNewsletterSubscribed is deliberately left alone too:
                    // setting it would produce a contact who looks subscribed on every screen and
                    // whom IsSuppressed will never let a dispatcher mail. The attempt is recorded
                    // so the agent can see the interest and seek consent through a channel that
                    // proves the person owns the address.
                    // 678 (audit D-4): the confirmation email is their way back: pressing its button is their own word.
                    client.Notes = $"{client.Notes}\nNewsletter signup attempted {DateTime.UtcNow:yyyy-MM-dd} on {Request.Host.Host}, but this contact has opted out of email -- opt-out left in place until they press the button in the confirmation email sent to them.".TrimStart();
                    _logger.LogInformation(
                        "Client {ClientId} is opted out of all email; a public newsletter form post on {Host} did NOT lift it.",
                        client.Id, Request.Host.Host);
                }
                else if (await IPRO.Business.Services.SignupConfirmations.AlreadyConfirmedAsync(_db, client))
                {
                    // 678 (audit D-4): their address is proven by an earlier click with this adviser.
                    client.IsNewsletterSubscribed = true;
                }
                // 678 (audit D-4): otherwise the confirmation email below subscribes them, by their own click.
            }
            client.UpdatedAt = DateTime.UtcNow;
        }

        // 678 (audit D-4): a newsletter sign-up waits for the person's click unless already subscribed and proven.
        var newsletterNeedsConfirmation = submissionType == WebsiteLeadTypes.Newsletter && client != null
            && !(client.IsNewsletterSubscribed && !client.EmailOptOutAt.HasValue);

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
                ? newsletterNeedsConfirmation
                    ? $"Signed up for the newsletter at {lead.SourceDomain}{sourcePage}; a confirmation email was sent, and they are subscribed once they press its button."
                    : $"Subscribed through the website at {lead.SourceDomain}{sourcePage}."
                : $"Website inquiry from {lead.SourceDomain}{sourcePage}." + (string.IsNullOrWhiteSpace(model.Message) ? string.Empty : $" Message: {model.Message}");
            _db.ClientComments.Add(new ClientComment { ClientId = client.Id, Comment = Truncate(note, 4000), CreatedAt = DateTime.UtcNow });
        }

        await _db.SaveChangesAsync();
        await NotifyAgentAsync(website, lead);
        if (newsletterNeedsConfirmation && client != null)
        {
            await SendSignupConfirmationAsync(website.AgentUserId, client, EmailSignupKinds.Newsletter, lead.SourceDomain);
        }

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

        return LocalRedirect(AddResult(returnPath, "submitted", SubmittedResult(model, submissionType)));
    }

    // 529: what "submitted=" says after a lead form: its type, except a newsletter sign-up through the
    // pop-up's own form (WebsitePopup.FormBlockId), which says "popup" -- the pop-up then opens with its
    // thank-you, and the newsletter blocks of the page do not take it for theirs.
    internal static string SubmittedResult(WebsiteLeadFormViewModel model, string submissionType) =>
        submissionType == WebsiteLeadTypes.Newsletter && model.BlockId == WebsitePopup.FormBlockId
            ? WebsitePopup.SubmittedResult
            : submissionType.ToLowerInvariant();

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

        // 678 (audit D-4, double opt-in): the articles wait for the person's click unless their address is already
        // proven with this adviser. A request nobody confirmed within the link's life no longer counts.
        await IPRO.Business.Services.SignupConfirmations.DropExpiredHeldArticlesAsync(_db, client.Id, now);
        var confirmed = await IPRO.Business.Services.SignupConfirmations.AlreadyConfirmedAsync(_db, client);
        var heldBefore = confirmed ? 0 : await _db.DidYouKnowEmailQueueItems
            .CountAsync(q => q.ClientId == client.Id && q.Status == DidYouKnowQueueStatuses.AwaitingConfirmation && q.SentAtUtc == null);

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
        if ((ordered.Count == 0 || capacity == 0) && heldBefore > 0)
        {
            // 678: what they asked for is already waiting for their click; the email goes again at most once a day.
            await SendSignupConfirmationAsync(website.AgentUserId, client, EmailSignupKinds.DidYouKnow, NormalizeHost(Request.Host.Host));
            return heldBefore;
        }
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
                // 678 (audit D-4): held until their click (SignupConfirmations.ReleaseHeldArticlesAsync reschedules).
                Status = confirmed ? DidYouKnowQueueStatuses.Queued : DidYouKnowQueueStatuses.AwaitingConfirmation,
                CreatedAt = now
            });
            scheduledFor = scheduledFor.AddMinutes(Random.Shared.Next(4, 9));
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Queued {Count} Did You Know article email(s) for client {ClientId} from block {BlockId} (agent {AgentId}){Held}.",
            ordered.Count, client.Id, block.Id, website.AgentUserId, confirmed ? string.Empty : ", held for their confirmation");
        if (!confirmed)
        {
            await SendSignupConfirmationAsync(website.AgentUserId, client, EmailSignupKinds.DidYouKnow, NormalizeHost(Request.Host.Host));
        }
        return ordered.Count + heldBefore;
    }

    // 678 (audit D-1): an opt-out outlives the contact (624). A website form used to start a deleted person over with
    // a clean contact; the kept opt-out now goes on the new one too. Their way back is the button in a confirmation
    // email, their preferences page or the portal: their own later word.
    private async Task KeepEarlierOptOutAsync(Client client)
    {
        if (IPRO.Business.Services.ContactOptOuts.Apply(client, await IPRO.Business.Services.ContactOptOuts.ForAgentAsync(_db, client.AgentUserId)))
        {
            client.Notes = $"{client.Notes}\nThis address opted out of email earlier, so it stays off the lists until the person chooses to hear from you again.".TrimStart();
        }
    }

    // 678 (audit D-4): the confirmation email for a website sign-up. At most one a day per person and kind, and a
    // limited number an hour per adviser (SignupConfirmations); a failed send is logged, the sign-up stays waiting.
    private async Task SendSignupConfirmationAsync(int agentUserId, Client client, string kind, string sourceDomain)
    {
        try
        {
            var token = await IPRO.Business.Services.SignupConfirmations.RequestAsync(_db, client, kind, sourceDomain, DateTime.UtcNow);
            if (token == null)
            {
                _logger.LogInformation("Sign-up confirmation ({Kind}) for client {ClientId} not sent again: one is already waiting, or the hourly allowance is used.", kind, client.Id);
                return;
            }
            var agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentUserId);
            if (agent == null) return;
            var url = IPRO.Business.Services.SignupConfirmations.ConfirmUrl(IPRO.Utility.WebAppUrlHelper.GetWebAppBaseUrl(_configuration), token);
            var name = $"{client.FirstName} {client.LastName}".Trim();
            var result = await _email.SendDetailedAsync(client.Email, name.Length == 0 ? client.Email : name,
                IPRO.Business.Services.SignupConfirmations.Subject(agent, kind),
                IPRO.Business.Services.SignupConfirmations.Html(agent, kind, sourceDomain, url),
                customArgs: AdviserSender.Tags(agent, EmailStreams.Notify, new Dictionary<string, string>
                {
                    ["ipro_entity"] = "signup_confirmation",
                    ["client_id"] = client.Id.ToString(),
                    ["agent_user_id"] = agent.Id.ToString()
                }),
                replyToEmail: AdviserSender.ReplyToEmail(agent),
                replyToName: AdviserSender.ReplyToName(agent));
            if (!result.Success)
            {
                _logger.LogWarning("Sign-up confirmation ({Kind}) for client {ClientId} was not sent: {Message}", kind, client.Id, result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sign-up confirmation ({Kind}) for client {ClientId} failed.", kind, client.Id);
        }
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
        // 492 (audit WEB-L-2): the token names the agent as well as the document, and the download
        // checks both -- a guessed document id inside a valid token can no longer reach another
        // agent's private file. Tokens live 30 minutes, so the old two-part shape simply expires.
        return _leadMagnetProtector.Protect($"{agentDocumentId}|{website.AgentUserId}|{expiresAt}");
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
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var documentId)
            || !int.TryParse(parts[1], out var agentUserId)
            || !long.TryParse(parts[2], out var expiresAt))
        {
            return NotFound();
        }
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresAt)
        {
            return NotFound();
        }

        var document = await _db.AgentDocuments.FirstOrDefaultAsync(d => d.Id == documentId && d.AgentUserId == agentUserId);
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

    [EnableRateLimiting(EndpointRateLimits.PublicForm)]
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

        if (SentTooFast(model.CaptchaToken, model.FormStartedAt))
        {
            await RecordSpamAttemptAsync(website, WebsiteSpamAttemptReasons.Timing, returnPath);
            return KeepForRetry(returnPath, CustomFormRetry(model, SendAgainMessage));
        }

        if (!IsCaptchaValid(model.CaptchaToken, model.CaptchaAnswer))
        {
            await RecordSpamAttemptAsync(website, WebsiteSpamAttemptReasons.Captcha, returnPath);
            return KeepForRetry(returnPath, CustomFormRetry(model, SecurityAnswerMessage).Problem("CaptchaAnswer", "Security check", SecurityAnswerProblem));
        }

        model.FirstName = model.FirstName?.Trim() ?? string.Empty;
        model.LastName = model.LastName?.Trim() ?? string.Empty;
        model.Email = (model.Email?.Trim() ?? string.Empty).ToLowerInvariant();
        model.Phone = model.Phone?.Trim() ?? string.Empty;

        if (!ModelState.IsValid || !model.ConsentGiven)
        {
            return KeepForRetry(returnPath, PersonProblems(CustomFormRetry(model, FixAndSendMessage), model.ConsentGiven, "so we can reply"));
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

        var missingRequired = fields.Where(f => f.FieldType != WebsiteFormFieldTypes.Section && f.IsRequired &&
            (!postedAnswers.TryGetValue(f.Id, out var values) || values.Count == 0)).ToList();
        if (missingRequired.Count > 0)
        {
            // 578: the message names each question still to answer (it used to say only "required fields").
            var retry = CustomFormRetry(model, FixAndSendMessage);
            foreach (var field in missingRequired)
            {
                retry.Problem(PublicFormRetry.AnswerField(field.Id), field.Label,
                    field.FieldType == WebsiteFormFieldTypes.CheckboxGroup ? "Choose at least one." : "Answer this question.");
            }
            return KeepForRetry(returnPath, retry);
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
                await KeepEarlierOptOutAsync(client);
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

    [EnableRateLimiting(EndpointRateLimits.PublicForm)]
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

        if (SentTooFast(model.CaptchaToken, model.FormStartedAt))
        {
            await RecordSpamAttemptAsync(website, WebsiteSpamAttemptReasons.Timing, returnPath);
            return KeepForRetry(returnPath, TestimonialRetry(model, SendAgainMessage));
        }

        if (!IsCaptchaValid(model.CaptchaToken, model.CaptchaAnswer))
        {
            await RecordSpamAttemptAsync(website, WebsiteSpamAttemptReasons.Captcha, returnPath);
            return KeepForRetry(returnPath, TestimonialRetry(model, SecurityAnswerMessage).Problem("CaptchaAnswer", "Security check", SecurityAnswerProblem));
        }

        model.FirstName = model.FirstName?.Trim() ?? string.Empty;
        model.LastName = model.LastName?.Trim() ?? string.Empty;
        model.Email = (model.Email?.Trim() ?? string.Empty).ToLowerInvariant();
        model.Body = model.Body?.Trim() ?? string.Empty;

        if (!ModelState.IsValid || !model.ConsentGiven)
        {
            return KeepForRetry(returnPath, PersonProblems(TestimonialRetry(model, FixAndSendMessage), model.ConsentGiven, "to your testimonial being reviewed and published"));
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

        // 646: a professional who keeps their own website. iPro's address shows a short card that points to it
        // (and to the client portal's sign-in, which lives here), whatever page is asked for.
        var ownWebsite = await OwnWebsiteForAsync(website.AgentUserId);
        if (ownWebsite != null) return await OwnWebsiteCardAsync(website, ownWebsite);

        // 553: an old-style page address (/about.html, /index.php). No page of this site can live
        // at one -- a page address has no dot -- so the only question is whether it is an address
        // of the adviser's previous website. Answered from three columns and nothing else: these
        // paths are also what scanners probe all day (/wp-login.php, /xmlrpc.php), and they must
        // not cost a full page render each. Unknown: the same bare 404 such a path always got.
        if (!string.IsNullOrWhiteSpace(slug) && IPRO.Web.Infrastructure.PlatformAliasHosts.IsLegacyPage(new PathString("/" + slug.Trim('/'))))
        {
            var addresses = await _db.WebsitePages
                .AsNoTracking()
                .Where(p => p.AgentWebsiteId == website.Id && p.IsPublished)
                .Select(p => new IPRO.Entities.WebsitePage { Id = p.Id, Slug = p.Slug, IsHomePage = p.IsHomePage, OldAddresses = p.OldAddresses })
                .ToListAsync();
            var replacement = IPRO.Web.Infrastructure.OldAddresses.Find(addresses, slug);
            return replacement == null ? NotFound() : MovedForGood(replacement);
        }

        return await BuildWebsiteViewAsync(website, slug);
    }

    // 553: the answer for an address of the adviser's previous website. Permanent, but not for ever
    // (507): a 301 with no lifetime can sit in a browser indefinitely.
    private IActionResult MovedForGood(IPRO.Entities.WebsitePage page)
    {
        Response.Headers.CacheControl = IPRO.Web.Infrastructure.PlatformAliasHosts.RedirectLifetime;
        return LocalRedirectPermanent(page.IsHomePage ? "/" : "/" + page.Slug.Trim('/'));
    }

    private async Task<IPRO.Entities.AgentWebsite?> FindWebsiteForHostAsync(string host, bool requirePublished = true)
    {
        var website = await FindWebsiteForHostRawAsync(host, requirePublished);
        if (website == null) return null;

        // M13 (launch runway Phase 2, 2026-08-27): the cancel dialog has always promised "your
        // site will go offline at the end of the billing period" (DOCS/22), and until this line
        // it never did -- IsAccessGatedAsync's call sites were the portal, the Billing page and
        // the layout; the public website was not one of them, so a lapsed agent's site stayed up
        // forever and there was no billing reason to come back.
        //
        // Gated resolves to NULL: to every caller of this method -- page render, robots, sitemap,
        // lead submission, custom forms, testimonials -- the site is then indistinguishable from
        // one that does not exist. The render path already turns that into a 404, which the
        // comment there prefers over 200 so a dead site is deindexed rather than cached; a gated
        // site should not keep collecting leads either, and this closes all seven doors at once.
        //
        // IsAccessGatedAsync honours PaidThroughAt (wave-2 E / H3), so a cancelled-but-paid-
        // through site correctly stays ONLINE until the promised end of the paid period plus
        // grace. Client-portal routes (/portal) authenticate separately and are deliberately not
        // touched here: a client's access to their own documents is not this fix's to revoke.
        if (await IsPublicSiteGatedAsync(website.AgentUserId)) return null;
        return website;
    }

    // The gate verdict is cached briefly because host resolution runs on EVERY public page view
    // -- the hottest path in the product -- and IsAccessGatedAsync costs up to three queries. Two
    // minutes of staleness is invisible next to PaidThroughAt's day-scale granularity; the price
    // is that a just-reactivated site can take up to two minutes to come back, which the Billing
    // page's own copy already softens ("changes take effect shortly").
    // ---- 646: a professional who keeps their own website --------------------------------------------

    // Asked on every page view of iPro's address, so cached like the site gate below; a change in the portal
    // reaches the public address within two minutes.
    private async Task<OwnWebsitePreference.Choice?> OwnWebsiteForAsync(int agentUserId)
    {
        var cacheKey = $"own-website:{agentUserId}";
        if (_cache.TryGetValue(cacheKey, out OwnWebsiteCacheEntry? cached) && cached != null) return cached.Choice;
        var choice = await OwnWebsitePreference.GetAsync(_db, agentUserId);
        _cache.Set(cacheKey, new OwnWebsiteCacheEntry(choice), TimeSpan.FromMinutes(2));
        return choice;
    }

    private sealed record OwnWebsiteCacheEntry(OwnWebsitePreference.Choice? Choice);

    private async Task<IActionResult> OwnWebsiteCardAsync(AgentWebsite website, OwnWebsitePreference.Choice choice)
    {
        var agent = await _db.AgentUsers.AsNoTracking().Where(a => a.Id == website.AgentUserId)
            .Select(a => new { a.CompanyName, a.FirstName, a.LastName }).FirstOrDefaultAsync();
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        return View("OwnWebsite", new IPRO.Web.Models.OwnWebsiteCardViewModel
        {
            BusinessName = !string.IsNullOrWhiteSpace(agent?.CompanyName) ? agent!.CompanyName.Trim() : $"{agent?.FirstName} {agent?.LastName}".Trim(),
            WebsiteUrl = choice.WebsiteUrl,
            WebsiteHost = AgentOwnWebsite.DisplayHost(choice.WebsiteUrl),
            ClientSignInUrl = $"{Request.Scheme}://{Request.Host}/ClientPortalAccount/Login"
        });
    }

    // The contact form for their own website: /PublicWebsite/LeadForm/{key}, opened as a link or shown inside
    // one of their pages. Only their own website's address may show it inside a page (frame-ancestors); with no
    // address saved it works as a link and nothing else may frame it.
    [HttpGet("/PublicWebsite/LeadForm/{id}")]
    public async Task<IActionResult> LeadForm(string id)
    {
        var target = await LeadFormTargetAsync(id);
        if (target == null) return NotFound();
        AllowFramingBy(target.WebsiteUrl);
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        return View("LeadFormEmbed", NewLeadForm(id, target.BusinessName, null));
    }

    // No anti-forgery token: inside another site's page the browser sends iPro no cookie, so the token could never
    // be checked; and there is nothing to forge -- no session, no account, only a message to the professional.
    // The form carries what the website's own forms carry instead: the hidden field, the timing, the sum (single
    // use), the visitor's consent, the per-visitor limit and the five-minute repeat check.
    [EnableRateLimiting(EndpointRateLimits.PublicForm)]
    [HttpPost("/PublicWebsite/LeadForm/{id}"), IgnoreAntiforgeryToken]
    public async Task<IActionResult> SubmitLeadForm(string id, IPRO.Web.Models.LeadFormEmbedViewModel model)
    {
        var target = await LeadFormTargetAsync(id);
        if (target == null) return NotFound();
        AllowFramingBy(target.WebsiteUrl);
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        var website = target.Website;
        const string sourcePage = "/lead-form";
        var thanks = NewLeadForm(id, target.BusinessName, null);
        thanks.Sent = true;

        if (!string.IsNullOrWhiteSpace(model.HoneypotField))
        {
            await RecordSpamAttemptAsync(website, WebsiteSpamAttemptReasons.Honeypot, sourcePage);
            return View("LeadFormEmbed", thanks);
        }
        if (SentTooFast(model.CaptchaToken, model.FormStartedAt))
        {
            await RecordSpamAttemptAsync(website, WebsiteSpamAttemptReasons.Timing, sourcePage);
            return View("LeadFormEmbed", NewLeadForm(id, target.BusinessName, model, SendAgainMessage));
        }
        if (!IsCaptchaValid(model.CaptchaToken, model.CaptchaAnswer))
        {
            await RecordSpamAttemptAsync(website, WebsiteSpamAttemptReasons.Captcha, sourcePage);
            return View("LeadFormEmbed", NewLeadForm(id, target.BusinessName, model, SecurityAnswerMessage));
        }

        var firstName = Truncate(model.FirstName, 80);
        var lastName = Truncate(model.LastName, 80);
        var email = Truncate(model.Email, 200).ToLowerInvariant();
        var phone = Truncate(model.Phone, 40);
        var message = Truncate(model.Message, 4000);
        var problems = new List<string>();
        if (firstName.Length == 0) problems.Add("First name: please fill this in.");
        if (email.Length == 0 || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email)) problems.Add("Email: please enter an email address such as name@example.com.");
        if (!model.ConsentGiven) problems.Add("Please tick the box to agree, so we can reply.");
        if (problems.Count > 0)
        {
            var again = NewLeadForm(id, target.BusinessName, model, FixAndSendMessage);
            again.Problems.AddRange(problems);
            return View("LeadFormEmbed", again);
        }

        var duplicateCutoff = DateTime.UtcNow.AddMinutes(-5);
        if (await _db.WebsiteLeads.AnyAsync(l => l.AgentUserId == website.AgentUserId && l.Email == email &&
                l.SubmissionType == WebsiteLeadTypes.Contact && l.CreatedAt >= duplicateCutoff))
        {
            _logger.LogInformation("Lead form submission from {Email} for adviser {AgentId} suppressed as a duplicate within 5 minutes; nothing was created.", email, website.AgentUserId);
            return View("LeadFormEmbed", thanks);
        }

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.AgentUserId == website.AgentUserId && c.Email == email);
        var processingNote = string.Empty;
        var sourceDomain = AgentOwnWebsite.DisplayHost(target.WebsiteUrl);
        if (sourceDomain.Length == 0) sourceDomain = NormalizeHost(Request.Host.Host);
        if (client == null)
        {
            var access = await _entitlements.GetAccessAsync(website.AgentUserId, PackageFeatureCodes.Contacts);
            var currentCount = await _db.Clients.CountAsync(c => c.AgentUserId == website.AgentUserId);
            if (access.IsIncluded && (!access.LimitValue.HasValue || access.LimitValue.Value < 0 || currentCount < access.LimitValue.Value))
            {
                client = new Client
                {
                    AgentUserId = website.AgentUserId, FirstName = firstName, LastName = lastName, Email = email, Phone = phone,
                    Notes = $"Created from the contact form on {sourceDomain}.", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                };
                await KeepEarlierOptOutAsync(client);
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
            if (string.IsNullOrWhiteSpace(client.Phone) && phone.Length > 0) client.Phone = phone;
            if (string.IsNullOrWhiteSpace(client.FirstName)) client.FirstName = firstName;
            if (string.IsNullOrWhiteSpace(client.LastName)) client.LastName = lastName;
            client.UpdatedAt = DateTime.UtcNow;
        }

        var lead = new WebsiteLead
        {
            AgentUserId = website.AgentUserId, AgentWebsiteId = website.Id, ClientId = client?.Id,
            SubmissionType = WebsiteLeadTypes.Contact,
            FirstName = firstName, LastName = lastName, Email = email, Phone = phone, Message = message,
            SourceDomain = sourceDomain, SourcePage = sourcePage,
            Referrer = Truncate(Request.Headers.Referer.ToString(), 1000),
            IpAddress = Truncate(GetRequestIpAddress(), 64),
            ConsentGiven = true, ProcessingNote = processingNote,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        _db.WebsiteLeads.Add(lead);
        if (client != null)
        {
            var note = $"Contact form on {sourceDomain}." + (message.Length == 0 ? string.Empty : $" Message: {message}");
            _db.ClientComments.Add(new ClientComment { ClientId = client.Id, Comment = Truncate(note, 4000), CreatedAt = DateTime.UtcNow });
        }
        await _db.SaveChangesAsync();
        await NotifyAgentAsync(website, lead);
        return View("LeadFormEmbed", thanks);
    }

    private sealed record LeadFormTarget(AgentWebsite Website, string BusinessName, string? WebsiteUrl);

    // The key's professional, while they still keep their own website and their account is live (an unpaid or
    // suspended account's forms answer nothing, like its site).
    private async Task<LeadFormTarget?> LeadFormTargetAsync(string? key)
    {
        var owner = await OwnWebsitePreference.FindByFormKeyAsync(_db, key);
        if (owner == null) return null;
        if (await IsPublicSiteGatedAsync(owner.Value.AgentUserId)) return null;
        var website = await _db.AgentWebsites.Include(w => w.AgentUser)
            .Where(w => w.AgentUserId == owner.Value.AgentUserId)
            .OrderBy(w => w.Id)
            .FirstOrDefaultAsync();
        if (website == null) return null;
        var agent = website.AgentUser;
        var name = !string.IsNullOrWhiteSpace(agent?.CompanyName) ? agent!.CompanyName.Trim() : $"{agent?.FirstName} {agent?.LastName}".Trim();
        return new LeadFormTarget(website, name, owner.Value.WebsiteUrl);
    }

    private IPRO.Web.Models.LeadFormEmbedViewModel NewLeadForm(string key, string businessName, IPRO.Web.Models.LeadFormEmbedViewModel? typed, string? problem = null)
    {
        var left = Random.Shared.Next(2, 10);
        var right = Random.Shared.Next(2, 10);
        var form = new IPRO.Web.Models.LeadFormEmbedViewModel
        {
            FormKey = key,
            BusinessName = businessName,
            FirstName = typed?.FirstName, LastName = typed?.LastName, Email = typed?.Email, Phone = typed?.Phone, Message = typed?.Message,
            ConsentGiven = typed?.ConsentGiven == true,
            FormStartedAt = typed != null && typed.FormStartedAt > 0 ? typed.FormStartedAt : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            CaptchaLeft = left,
            CaptchaRight = right,
            CaptchaToken = _captchaProtector.Protect($"{left + right}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}|{Guid.NewGuid():N}")
        };
        if (problem != null) form.Problems.Add(problem);
        return form;
    }

    // The default policy lets only iPro frame iPro (frame-ancestors 'self', X-Frame-Options SAMEORIGIN). This
    // one page also lets the professional's own website show it -- its host and www twin, nothing else.
    private void AllowFramingBy(string? websiteUrl)
    {
        var origins = AgentOwnWebsite.FrameOrigins(websiteUrl);
        if (origins.Length == 0) return;
        Response.Headers.Remove("X-Frame-Options");
        var csp = Response.Headers.ContentSecurityPolicy.ToString();
        if (csp.Contains("frame-ancestors 'self';", StringComparison.Ordinal))
            Response.Headers.ContentSecurityPolicy = csp.Replace("frame-ancestors 'self';", "frame-ancestors 'self' " + string.Join(' ', origins) + ";", StringComparison.Ordinal);
    }

    private async Task<bool> IsPublicSiteGatedAsync(int agentUserId)
    {
        var cacheKey = $"public-site-gated:{agentUserId}";
        if (_cache.TryGetValue(cacheKey, out bool gated)) return gated;
        // 599 (2026-10-24): a suspended adviser (SuperAdmin -> Deactivate) is offline at once, even inside a
        // paid-through period -- the suspension cancels billing and takes the site down together.
        var isActive = await _db.AgentUsers.AsNoTracking().Where(a => a.Id == agentUserId).Select(a => a.IsActive).FirstOrDefaultAsync();
        gated = !isActive || await _entitlements.IsAccessGatedAsync(agentUserId);
        _cache.Set(cacheKey, gated, TimeSpan.FromMinutes(2));
        return gated;
    }

    private async Task<IPRO.Entities.AgentWebsite?> FindWebsiteForHostRawAsync(string host, bool requirePublished = true)
    {
        // A5-M-EMPTYHOST (fixed 2026-08-20): an empty Host header must resolve to NOTHING. Many
        // AgentDomains rows legitimately hold an empty RootDomain, and the match below compares
        // by value -- so an empty host used to match the first such row and hand a form
        // submission, robots.txt or sitemap to an arbitrary agent's site. Page rendering already
        // refused these requests; this closes the same door for every other caller at once.
        if (string.IsNullOrWhiteSpace(host)) return null;

        var hostMatches = BuildHostMatches(host);
        // 592: a proven row first (DomainOwnershipProof); a host only unproven rows name is served by nobody, not
        // even through the website's own CustomDomain below. A domain row alone used to be enough: with an
        // earlier adviser's binding still in Azure and the domain's DNS still on us, adding the name put this
        // adviser's pages on someone else's domain at once (www.411trades.com, 2026-10-19).
        var domainMatch = await _db.AgentDomains
            .Include(d => d.AgentWebsite).ThenInclude(w => w.AgentUser)
            .Include(d => d.AgentWebsite).ThenInclude(w => w.Template)
            .OrderByDescending(d => d.OwnershipVerifiedAt != null)
            .ThenBy(d => d.Id)
            .FirstOrDefaultAsync(d =>
                hostMatches.Contains(d.DomainName.ToLower()) ||
                hostMatches.Contains(d.RootDomain.ToLower()) ||
                hostMatches.Contains(d.WwwDomain.ToLower()));
        if (domainMatch != null && domainMatch.OwnershipVerifiedAt == null) return null;

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

        // The free site, by its adviser's own address. 592: no longer by the website's CustomDomain field: it is
        // written by the settings form before the domain row (a name it holds with no row of its own -- a failed
        // save, a removed row -- was served with no proof at all). Every custom domain is served through its
        // proven row above.
        return await _db.AgentWebsites
            .Include(w => w.AgentUser)
            .Include(w => w.Template)
            .FirstOrDefaultAsync(w => (w.IsPublished || !requirePublished) &&
                hostMatches.Contains(w.AgentUser.DomainName.ToLower()));
    }

    private async Task<IActionResult> BuildWebsiteViewAsync(IPRO.Entities.AgentWebsite website, string? slug)
    {
        var announcementBar = await AnnouncementBarForAsync(website);   // 528
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

            // 553: an address from the adviser's previous website goes to the page that replaced it,
            // for good. Asked only when no page lives AT the requested address -- nothing there, or a
            // longer path of which only the first part happens to match a page -- so a real page
            // always wins. Not a page view either: the visit is counted where it lands.
            var requestedAddress = IPRO.Web.Infrastructure.OldAddresses.Normalize(slug);
            if (currentPage == null || !string.Equals(requestedAddress, normalizedSlug, StringComparison.Ordinal))
            {
                var movedTo = IPRO.Web.Infrastructure.OldAddresses.Find(pages, requestedAddress);
                if (movedTo != null) return MovedForGood(movedTo);
            }

            if (currentPage == null && IPRO.Web.Infrastructure.AccessibilityStatement.IsAddress(slug))
            {
                // 579: every site's accessibility statement, linked from its footer, written from what
                // the site already has. The owner's own page at this address was looked for first and
                // wins. It costs less than the 404 below (no canonical origin: the statement declares
                // none), so a scanner asking for it once a second is no new load; not counted as a page
                // view either.
                return View("Index", new PublicWebsiteViewModel
                {
                    Website = website,
                    Pages = pages,
                    CurrentPage = null,
                    AccessibilityStatement = true,
                    AnnouncementBar = announcementBar
                });
            }

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
                    CanonicalOrigin = await ResolveCanonicalOriginAsync(website),
                    AnnouncementBar = announcementBar
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
        var pollVoteByBlockId = await IPRO.Web.Infrastructure.PollVoteBuilder.BuildAsync(_db, website.AgentUserId, currentPage, IPRO.Web.Infrastructure.PollVoteCookies.Read(Request));
        var formsByBlockId = await IPRO.Web.Infrastructure.PublicFormBuilder.BuildAsync(_db, website.AgentUserId, currentPage);
        var didYouKnowByBlockId = await IPRO.Web.Infrastructure.DidYouKnowBuilder.BuildAsync(_db, website.AgentUserId, currentPage);
        var articleContentByBlockId = await IPRO.Web.Infrastructure.ArticleContentBuilder.BuildAsync(_db, website.AgentUserId, currentPage);
        // ?post=<id> is how a Blog block shows one post in full without needing its own route
        // (see WebsiteBlogSettings for why). A bad or foreign id simply resolves to nothing and
        // the block renders its list instead.
        _ = int.TryParse(Request.Query["post"], out var selectedPostId);
        var blogByBlockId = await IPRO.Web.Infrastructure.BlogBuilder.BuildAsync(_db, website.AgentUserId, currentPage, selectedPostId);
        // 667: asked only for a page that shows a Reviews block (one small read by key).
        var googleReviewLink = currentPage?.Blocks.Any(b => b.IsVisible && b.BlockType == WebsiteBlockTypes.Reviews) == true
            ? await IPRO.DataAccess.GoogleReviewPreference.WebsiteLinkAsync(_db, website.AgentUserId, website.AgentUser?.BusinessType)
            : null;

        // 529: the root is the home page, whichever page it shows.
        var popup = await PopupForAsync(website, string.IsNullOrWhiteSpace(normalizedSlug) || currentPage?.IsHomePage == true);

        return View("Index", new PublicWebsiteViewModel
        {
            Website = website,
            Pages = pages,
            CurrentPage = currentPage,
            CanonicalOrigin = await ResolveCanonicalOriginAsync(website),
            ApprovedTestimonials = approvedTestimonials,
            PollResultsByBlockId = pollResultsByBlockId,
            PollVoteByBlockId = pollVoteByBlockId,
            FormsByBlockId = formsByBlockId,
            DidYouKnowByBlockId = didYouKnowByBlockId,
            ArticleContentByBlockId = articleContentByBlockId,
            BlogByBlockId = blogByBlockId,
            PictureDescriptions = await IPRO.Web.Infrastructure.PictureDescriptions.ForPageAsync(_db, website.Id, currentPage),
            AnnouncementBar = announcementBar,
            GoogleReviewLink = googleReviewLink,
            Popup = popup
        });
    }

    // 529: the pop-up for this page view -- switched on with something to offer, on this page (the home page
    // only, unless the adviser chose every page), not closed or signed up through by this visitor (the seen
    // cookie holds its SeenKey; pages are sent no-store, so reading it here is safe), and in the adviser's
    // package (cached like the bar's, and asked only for a site whose pop-up is on). Just after a sign-up
    // through it, it comes back to say thank you, on whatever page that was.
    private async Task<PublicPopupView?> PopupForAsync(IPRO.Entities.AgentWebsite website, bool isHomePage)
    {
        var popup = WebsiteHeaderSettings.FromJson(website.HeaderSettingsJson).Popup;
        if (!popup.IsShown) return null;
        var justSignedUp = popup.IsSignup && string.Equals(Request.Query["submitted"], WebsitePopup.SubmittedResult, StringComparison.Ordinal);
        if (!justSignedUp)
        {
            if (popup.HomePageOnly && !isHomePage) return null;
            if (Request.Cookies.TryGetValue(WebsitePopup.SeenCookie, out var seen) && seen == popup.SeenKey) return null;
        }

        var cacheKey = $"public-site-popup:{website.AgentUserId}";
        if (!_cache.TryGetValue(cacheKey, out bool included))
        {
            included = await _entitlements.HasAccessAsync(website.AgentUserId, PackageFeatureCodes.PromotionalPopup);
            _cache.Set(cacheKey, included, TimeSpan.FromMinutes(2));
        }
        if (!included) return null;

        var picture = popup.ImageAssetId.HasValue
            ? await _db.WebsiteMediaAssets.AsNoTracking()
                .Where(a => a.Id == popup.ImageAssetId.Value && a.AgentWebsiteId == website.Id)
                .Select(a => new { a.BlobUrl, a.Description })
                .FirstOrDefaultAsync()
            : null;
        return new PublicPopupView
        {
            Settings = popup,
            ImageUrl = picture?.BlobUrl ?? string.Empty,
            ImageAlt = picture?.Description ?? string.Empty,
            OpenAs = justSignedUp ? "done" : string.Empty
        };
    }

    // 528: the announcement bar for this page view -- switched on with a message, in the adviser's package
    // (cached like the site gate above, and asked only for a site whose bar is on), and not closed by this
    // visitor (the close button's cookie holds the bar's DismissKey; pages are sent no-store, so reading it
    // here is safe).
    private async Task<WebsiteAnnouncementBar?> AnnouncementBarForAsync(IPRO.Entities.AgentWebsite website)
    {
        var bar = WebsiteHeaderSettings.FromJson(website.HeaderSettingsJson).Announcement;
        if (!bar.IsShown) return null;
        if (bar.Dismissible && Request.Cookies.TryGetValue(WebsiteAnnouncementBar.DismissCookie, out var closed) && closed == bar.DismissKey) return null;

        var cacheKey = $"public-site-announcement:{website.AgentUserId}";
        if (!_cache.TryGetValue(cacheKey, out bool included))
        {
            included = await _entitlements.HasAccessAsync(website.AgentUserId, PackageFeatureCodes.AnnouncementBar);
            _cache.Set(cacheKey, included, TimeSpan.FromMinutes(2));
        }
        return included ? bar : null;
    }

    private async Task TrackPageViewAsync(AgentWebsite website, WebsitePage? page)
    {
        try
        {
            if (string.Equals(Request.Headers["DNT"], "1", StringComparison.Ordinal)) return;
            // 554: a HEAD answered as a GET (HeadRequests) is a link checker or a monitor asking
            // whether the page is there. Nobody looked at it.
            if (IPRO.Web.Infrastructure.HeadRequests.IsBeingAnswered(HttpContext)) return;

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
            // 493: ten notification emails an hour per website. The form is public and the subscription
            // is capped at 100 sends an hour; the lead is saved whatever happens, this only decides the
            // email, and the row under Website Leads says why it was held.
            // 632 H-2: and a limit across every website, past each site's first three an hour. A held
            // email is no longer dropped: HeldLeadNotificationJob sends it as soon as there is room.
            var now = DateTime.UtcNow;
            if (await IPRO.Business.Services.WebsiteLeadNotifications.IsHeldAsync(_db, website.Id, now)
                || !await IPRO.Business.Services.WebsiteLeadNotifications.WithinSharedLimitAsync(_db, website.Id, now))
            {
                lead.NotificationSent = false;
                lead.NotificationError = IPRO.Business.Services.WebsiteLeadNotifications.HeldMessage;
                _logger.LogWarning("Website lead {LeadId} saved; the notification was held for later: website {WebsiteId} or all websites reached the hourly limit.",
                    lead.Id, website.Id);
                return;
            }
            var result = await _email.SendDetailedAsync(website.AgentUser.Email, $"{website.AgentUser.FirstName} {website.AgentUser.LastName}".Trim(),
                IPRO.Business.Services.WebsiteLeadNotifications.Subject(lead),
                IPRO.Business.Services.WebsiteLeadNotifications.Html(lead, GetAgentPortalBaseUrl()));
            lead.NotificationSent = result.Success;
            lead.NotificationSentAt = result.Success ? DateTime.UtcNow : null;
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

    // 640 (582's item 2): "sent too fast" (under 2 seconds after the form was drawn) or a day old. It read only
    // FormStartedAt, a time the browser sends back unsigned, so a bot could post any time it liked. The security
    // question's sealed token carries the time the form was drawn, and that is the time read now; a token that
    // cannot be read falls back to FormStartedAt and then fails the security question anyway.
    private bool SentTooFast(string? captchaToken, long formStartedAt)
    {
        var startedAt = SealedTime(captchaToken) ?? formStartedAt;
        var elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - startedAt;
        return startedAt <= 0 || elapsed < 2 || elapsed > 86400;
    }

    private long? SealedTime(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var parts = _captchaProtector.Unprotect(token).Split('|', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], out var issuedAt) ? issuedAt : null;
        }
        catch
        {
            return null;
        }
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
            // 3 parts since 2026-08-15: answer|issuedAt|nonce. 2-part tokens are still accepted for
            // the 30-minute grace so forms rendered by the previous build don't all fail at deploy,
            // but they age out on their own; every new token carries a nonce.
            if (parts.Length is not (2 or 3) ||
                !int.TryParse(parts[0], out var expected) ||
                !long.TryParse(parts[1], out var issuedAt))
            {
                return false;
            }

            var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - issuedAt;
            if (age < 0 || age > 1800)
            {
                return false;
            }

            // SINGLE-USE (audit item 422g). The token used to stay valid for its whole 30-minute
            // window, so a bot that solved one 2+7 could replay the same token+answer pair hundreds
            // of times, turning the captcha into a one-time cost. A successful submission now burns
            // the nonce for the remainder of the token's lifetime; replays fail like a wrong answer.
            // In-memory is sufficient: the app runs single-instance, and the worst case after a
            // restart is one extra use of a token issued in the prior 30 minutes.
            //
            // 640 (582's item 1): a WRONG answer used to leave the question open, so one question took every
            // guess (two numbers from 2 to 9 make only 15 possible sums) and only the 3-posts-in-5-minutes limit
            // stood behind it.
            // Any answer now uses the question up; the form comes back with a new one (KeepForRetry).
            if (parts.Length == 3)
            {
                var nonceKey = $"captcha-used:{parts[2]}";
                if (_cache.TryGetValue(nonceKey, out _))
                {
                    return false;
                }
                _cache.Set(nonceKey, true, TimeSpan.FromMinutes(31));
            }

            return int.TryParse(answer.Trim(), out var actual) && actual == expected;
        }
        catch
        {
            return false;
        }
    }

    public enum PublicNewsletterConsent { Subscribe, LeaveOptOutInPlace }

    // Pre-launch audit (2026-08-30). SubmitLead matches an existing contact by EMAIL ALONE, and
    // this branch used to call ResubscribeAsync for one who had opted out -- clearing the global
    // opt-out and restoring newsletters, e-cards, e-letters, polls, DidYouKnow and drip. No token,
    // no confirmation, no proof the submitter owns the address: any visitor who knew an adviser's
    // site and one of their contacts' addresses could reverse that person's recorded "stop", and
    // the system would write a fabricated express-consent record to justify it. The code below
    // already names this exact hazard for the DidYouKnow queue ("no verification that they own
    // it") and caps it there.
    //
    // A recorded stop now survives an anonymous post. The lead itself is still captured, so the
    // agent sees the interest and can ask for consent through a channel that proves ownership.
    public static PublicNewsletterConsent DecidePublicNewsletterConsent(bool hasRecordedOptOut)
        => hasRecordedOptOut ? PublicNewsletterConsent.LeaveOptOutInPlace : PublicNewsletterConsent.Subscribe;

    // Pre-launch audit (2026-08-30): this rejected a leading "//" but not "/\\", which
    // Url.IsLocalUrl DOES reject -- so LocalRedirect threw AFTER the client row was written and
    // the agent notification had gone out. The visitor got an error page instead of their
    // confirmation, and a retry inside five minutes was swallowed by the duplicate guard. Not an
    // open redirect (the framework guard held); it failed loudly instead of falling back to "/".
    // Every other return-url site in the app guards with Url.IsLocalUrl first; this hand-rolled
    // variant was the outlier, so it now rejects the same shapes the framework does.
    internal static string NormalizeReturnPath(string? value)
    {
        var path = string.IsNullOrWhiteSpace(value) ? "/" : value.Trim();
        if (!path.StartsWith('/')) return "/";
        if (path.Length > 1 && (path[1] == '/' || path[1] == '\\')) return "/";
        return path.Split('?', '#')[0];
    }

    private static string AddResult(string path, string key, string value) => $"{path}?{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
    private static string AddResult(string path, string key, string value, string extraKey, string extraValue) =>
        $"{AddResult(path, key, value)}&{Uri.EscapeDataString(extraKey)}={Uri.EscapeDataString(extraValue)}";
    private static string Truncate(string? value, int length) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim()[..Math.Min(value.Trim().Length, length)];

    // 578: what a turned-down form says, and what it keeps for the visitor (PublicFormRetry).
    private const string SendAgainMessage = "Nothing was sent yet. Please check your details and send the form again.";
    private const string SecurityAnswerMessage = "Nothing was sent yet: the answer to the security question was not right. Your details are still here; please answer the new question and send again.";
    private const string SecurityAnswerProblem = "Answer the new question.";
    private const string FixAndSendMessage = "Nothing was sent yet. Please fix the following and send again:";

    private IActionResult KeepForRetry(string returnPath, PublicFormRetry retry)
    {
        retry.Save(TempData);
        return LocalRedirect(returnPath);
    }

    private static PublicFormRetry LeadRetry(WebsiteLeadFormViewModel model, string message)
    {
        return new PublicFormRetry { Form = PublicFormRetry.LeadForm(model, ResolveSubmissionType(model.SubmissionType)), Message = message, Consent = model.ConsentGiven }
            .Keep("FirstName", model.FirstName, 80).Keep("LastName", model.LastName, 80).Keep("Email", model.Email, 200)
            .Keep("Phone", model.Phone, 40).Keep("Message", model.Message, 4000);
    }

    private static PublicFormRetry TestimonialRetry(TestimonialFormViewModel model, string message) =>
        new PublicFormRetry { Form = PublicFormRetry.TestimonialForm, Message = message, Consent = model.ConsentGiven }
            .Keep("FirstName", model.FirstName, 80).Keep("LastName", model.LastName, 80).Keep("Email", model.Email, 200)
            .Keep("Body", model.Body, 2000);

    private static PublicFormRetry CustomFormRetry(WebsiteCustomFormViewModel model, string message)
    {
        var retry = new PublicFormRetry { Form = PublicFormRetry.CustomForm(model.WebsiteFormId), Message = message, Consent = model.ConsentGiven }
            .Keep("FirstName", model.FirstName, 80).Keep("LastName", model.LastName, 80).Keep("Email", model.Email, 200)
            .Keep("Phone", model.Phone, 40);
        // The answers as posted, cut to the longest a field takes, and no more than a real form sends.
        var budget = 20000;
        foreach (var answer in (model.Answers ?? new List<CustomAnswerInput>()).Take(100))
        {
            foreach (var value in (answer.Values ?? new List<string>()).Take(50))
            {
                var text = value?.Trim() ?? string.Empty;
                if (text.Length == 0) continue;
                if (text.Length > 4000) text = text[..4000];
                if (text.Length > budget) return retry;
                budget -= text.Length;
                if (!retry.Answers.TryGetValue(answer.FieldId, out var kept)) retry.Answers[answer.FieldId] = kept = new List<string>();
                kept.Add(text);
            }
        }
        return retry;
    }

    // Each field the model binder turned down, named with what to do about it.
    private PublicFormRetry PersonProblems(PublicFormRetry retry, bool consentGiven, string consentPurpose)
    {
        foreach (var (field, label, missing, tooLong) in new[]
        {
            ("FirstName", "First name", "Enter your first name.", "Use 80 characters or fewer."),
            ("LastName", "Last name", "Enter your last name.", "Use 80 characters or fewer."),
            ("Email", "Email", "Enter your email address.", "Enter an email address like name@example.com."),
            ("Phone", "Phone", "Enter your phone number.", "Use 40 characters or fewer."),
            ("Message", "How can we help?", "Write your message.", "Use 4,000 characters or fewer."),
            ("Body", "Your testimonial", "Write your testimonial.", "Use 2,000 characters or fewer.")
        })
        {
            if (ModelState.GetFieldValidationState(field) != Microsoft.AspNetCore.Mvc.ModelBinding.ModelValidationState.Invalid) continue;
            retry.Problem(field, label, string.IsNullOrWhiteSpace(retry.Value(field)) ? missing : tooLong);
        }
        if (!consentGiven) retry.Problem("ConsentGiven", "Agreement", $"Tick the box to agree {consentPurpose}.");
        return retry;
    }

    private static string ResolveSubmissionType(string? submissionType)
    {
        if (string.Equals(submissionType, WebsiteLeadTypes.Newsletter, StringComparison.OrdinalIgnoreCase)) return WebsiteLeadTypes.Newsletter;
        if (string.Equals(submissionType, WebsiteLeadTypes.LeadMagnet, StringComparison.OrdinalIgnoreCase)) return WebsiteLeadTypes.LeadMagnet;
        if (string.Equals(submissionType, WebsiteLeadTypes.DidYouKnow, StringComparison.OrdinalIgnoreCase)) return WebsiteLeadTypes.DidYouKnow;
        return WebsiteLeadTypes.Contact;
    }
}
