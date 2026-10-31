using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Scheduler;
using IPRO.Utility;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;

namespace IPRO.Web.Controllers;

[Authorize]
public class WebsiteController : Controller
{
    private readonly IWebsiteService _websites;
    private readonly IBlobStorageService _blob;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IAgentService _agents;
    private readonly IConfiguration _configuration;
    private readonly IPRODbContext _db;
    private readonly IDomainCheckService _domainCheck;
    private readonly IAzureDomainAutomationService _azureDomains;
    private readonly ILogger<WebsiteController> _logger;
    private readonly IMemoryCache? _cache;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public WebsiteController(IWebsiteService websites, IBlobStorageService blob, IPackageEntitlementService entitlements, IAgentService agents, IConfiguration configuration, IPRODbContext db,
        IDomainCheckService domainCheck, IAzureDomainAutomationService azureDomains, ILogger<WebsiteController> logger, IMemoryCache? cache = null)
    {
        _websites = websites;
        _blob = blob;
        _entitlements = entitlements;
        _agents = agents;
        _configuration = configuration;
        _db = db;
        _domainCheck = domainCheck;
        _azureDomains = azureDomains;
        _logger = logger;
        _cache = cache;
    }

    public async Task<IActionResult> Index()
    {
        var gate = await RequireWebsiteAccessAsync();
        if (gate != null) return gate;

        ViewBag.Words = IPRO.Entities.BusinessWords.For((await _agents.GetByIdAsync(AgentId))?.BusinessType);   // 626
        var website = await _websites.GetByAgentIdAsync(AgentId);
        await LoadWebsiteContextAsync(website?.TemplateId);
        // 585: the new builder (Beta) card, for an adviser a SuperAdmin switched it on for (or whose
        // site has changes from it that can still be put back).
        ViewBag.BuilderBeta = website == null ? null : await BuilderBetaCardAsync(website);
        return View(website);
    }

    private async Task<IPRO.Web.Models.WebsiteBuilderCardViewModel?> BuilderBetaCardAsync(AgentWebsite website)
    {
        var state = await IPRO.Business.SiteImport.BuilderBeta.StateAsync(_db, website, DateTime.UtcNow);
        // Shown while the Beta is on, and after it is switched off while something of it is still the
        // adviser's: a package waiting, or changes on the site that can be put back.
        if (state.Beta?.Enabled != true && state.PutBack == null && state.Waiting == null) return null;
        var changed = state.PutBack == null
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : await IPRO.Business.SiteImport.BuilderBeta.ChangedSinceAsync(_db, state.PutBack);
        var zone = (await _db.AgentUsers.AsNoTracking().Where(a => a.Id == website.AgentUserId).Select(a => a.TimeZone).FirstOrDefaultAsync()) ?? string.Empty;
        return new IPRO.Web.Models.WebsiteBuilderCardViewModel(state, WebsiteBuilderController.BuilderBaseUrl(_configuration) != null,
            changed, IPRO.Business.SiteImport.BuilderBeta.KeepDays, zone);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> Save(AgentWebsite model, IFormFile? logo, bool applyTemplateDefaults = false, bool keepDomain = false)
    {
        var gate = await RequireWebsiteAccessAsync();
        if (gate != null) return gate;

        // 573: the logo's address never comes from the form; only the upload below sets it. A posted
        // LogoUrl used to be stored as the site's logo, and the address it replaced was then deleted
        // from storage by its path, whoever's file that was.
        model.LogoUrl = string.Empty;

        if (model.TemplateId <= 0)
        {
            var agent = await _agents.GetByIdAsync(AgentId);
            model.TemplateId = (await _websites.EnsureDefaultTemplateForPackageAsync(agent?.PackageId, agent?.BusinessType)).Id;
        }

        var existing = await _websites.GetByAgentIdAsync(AgentId);
        // 595: the custom domain and the address on the emails are set on the Domain page now; My Website's form says
        // so (keepDomain) and they stay exactly as they are, whatever another tab changed meanwhile.
        if (keepDomain)
        {
            model.CustomDomain = existing?.CustomDomain ?? string.Empty;
            model.CustomDomainDisplay = existing?.CustomDomainDisplay ?? string.Empty;
        }
        var selectedTemplate = await _db.WebsiteTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == model.TemplateId);
        if (selectedTemplate == null || (!selectedTemplate.IsActive && existing?.TemplateId != selectedTemplate.Id))
        {
            TempData["Error"] = "That website template is no longer available. Choose another active template.";
            return RedirectToAction(nameof(Index));
        }
        if (logo != null && logo.Length > 0)
        {
            if (logo.Length > 8 * 1024 * 1024)
            {
                TempData["Error"] = "Logo images must be 8 MB or smaller.";
                return RedirectToAction(nameof(Index));
            }

            // 585: judged by what it is, not by its name: a PNG called logo.jpg is a PNG (UploadedPicture).
            await using var logoStream = logo.OpenReadStream();
            var logoHeader = await IPRO.Utility.UploadedPicture.HeaderAsync(logoStream);
            var logoPicture = IPRO.Utility.UploadedPicture.Of(logoHeader);
            if (logoPicture == null)
            {
                TempData["Error"] = IPRO.Utility.UploadedPicture.Refusal(logoHeader);
                return RedirectToAction(nameof(Index));
            }

            model.LogoUrl = await _blob.UploadAsync(logoStream, IPRO.Utility.UploadedPicture.StoredName(logo.FileName, logoPicture), "agent-logos", logoPicture.ContentType, isPrivate: false);
        }

        model.CustomDomain = NormalizeDomain(model.CustomDomain);
        // 552: the custom domain as it is written in the adviser's emails ("www.4iPro.com"): the same letters, only
        // the capitals may differ -- a different address there would send clients somewhere else.
        model.CustomDomainDisplay = (model.CustomDomainDisplay ?? string.Empty).Trim();
        // 595's third review (M-1): with keepDomain the form sent neither value, so a stored one is not this form's to
        // refuse (the Domain page checks it where it is typed, and the emails ignore one that does not match).
        if (!keepDomain)
        {
            if (model.CustomDomainDisplay.Length > 0 && !string.Equals(model.CustomDomainDisplay, model.CustomDomain, StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = string.IsNullOrEmpty(model.CustomDomain)
                    ? "\"Shown in your emails as\" needs your custom domain first."
                    : $"\"Shown in your emails as\" must be {model.CustomDomain} with your own capitals: the same letters, nothing added or left out.";
                return RedirectToAction(nameof(Index));
            }
        }
        model.SiteTitle = model.SiteTitle?.Trim() ?? string.Empty;
        model.TagLine = model.TagLine?.Trim() ?? string.Empty;
        model.SiteLanguage = WebsiteLanguages.Normalize(model.SiteLanguage);   // 578
        model.ThemeColor = applyTemplateDefaults || existing is null
            ? WebsiteTemplateDesign.FromTemplate(selectedTemplate).AccentColor
            : NormalizeThemeColor(model.ThemeColor);

        if (applyTemplateDefaults || existing is null)
        {
            model.FontFamilyOverride = string.Empty;
            model.HeadingFontSizeOverride = 0;
            model.BodyFontSizeOverride = 0;
            model.BackgroundColorOverride = string.Empty;
            model.ButtonStyleOverride = string.Empty;
            model.SectionSpacingOverride = string.Empty;
            model.HeroStyleOverride = string.Empty;
        }
        else
        {
            model.FontFamilyOverride = model.FontFamilyOverride?.Trim() ?? string.Empty;
            model.HeadingFontSizeOverride = NormalizeFontSize(model.HeadingFontSizeOverride, 14, 40);
            model.BodyFontSizeOverride = NormalizeFontSize(model.BodyFontSizeOverride, 12, 24);
            model.BackgroundColorOverride = NormalizeOptionalColor(model.BackgroundColorOverride);
            model.ButtonStyleOverride = NormalizeOptionalOption(model.ButtonStyleOverride, "square", "soft", "pill");
            model.SectionSpacingOverride = NormalizeOptionalOption(model.SectionSpacingOverride, "compact", "comfortable", "spacious");
            model.HeroStyleOverride = NormalizeOptionalOption(model.HeroStyleOverride, "gradient", "clean", "classic");
        }

        // Same claim check as AddDomain -- this path writes AgentWebsites.CustomDomain and inserts an
        // AgentDomains row of its own, so leaving it on the old AgentDomains-only test would have kept
        // the takeover reachable through the ordinary Save form.
        if (!string.IsNullOrWhiteSpace(model.CustomDomain))
        {
            var domainClaim = await DescribeDomainClaimAsync(NormalizeDomain(model.CustomDomain), AgentId);
            if (domainClaim != null)
            {
                TempData["Error"] = domainClaim;
                return RedirectToAction(nameof(Index));
            }
        }

        if (!string.IsNullOrWhiteSpace(model.CustomDomain) &&
            existing?.CustomDomain != model.CustomDomain &&
            !await _db.AgentDomains.AnyAsync(d => d.AgentUserId == AgentId && d.DomainName == model.CustomDomain))
        {
            var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.MultiDomainSupport);
            var currentCount = await _db.AgentDomains.CountAsync(d => d.AgentUserId == AgentId);
            if (!access.IsIncluded)
            {
                TempData["Error"] = access.UpgradeMessage;
                return RedirectToAction(nameof(Index));
            }

            if (access.LimitValue.HasValue && access.LimitValue.Value >= 0 && currentCount >= access.LimitValue.Value)
            {
                TempData["Error"] = $"Your current package allows {access.LimitValue.Value} custom domain(s). Remove one or upgrade before adding another.";
                return RedirectToAction(nameof(Index));
            }
        }

        // 592: a name another adviser holds unproven moves here only on this adviser's own published record
        // (TakeOverUnprovenClaimsAsync), and before anything of this save is written.
        (string? Error, string Token, bool ProvenNow)? claim = null;
        if (!string.IsNullOrWhiteSpace(model.CustomDomain) &&
            !await _db.AgentDomains.AnyAsync(d => d.AgentUserId == AgentId && d.DomainName == model.CustomDomain))
        {
            claim = await TakeOverUnprovenClaimsAsync(model.CustomDomain, AgentId);
            if (claim.Value.Error != null && existing?.CustomDomain != model.CustomDomain)
            {
                TempData["Error"] = claim.Value.Error;
                return RedirectToAction(nameof(Index));
            }
        }

        if (existing == null)
        {
            // 573: a first save writes the fields this form has, and nothing else the request named
            // (the posted object could carry pages, domains, an account, "published").
            existing = await _websites.CreateAsync(new AgentWebsite
            {
                AgentUserId = AgentId,
                TemplateId = model.TemplateId,
                SiteTitle = model.SiteTitle,
                TagLine = model.TagLine,
                ThemeColor = model.ThemeColor,
                FontFamilyOverride = model.FontFamilyOverride,
                HeadingFontSizeOverride = model.HeadingFontSizeOverride,
                BodyFontSizeOverride = model.BodyFontSizeOverride,
                BackgroundColorOverride = model.BackgroundColorOverride,
                ButtonStyleOverride = model.ButtonStyleOverride,
                SectionSpacingOverride = model.SectionSpacingOverride,
                HeroStyleOverride = model.HeroStyleOverride,
                CustomDomain = model.CustomDomain,
                CustomDomainDisplay = model.CustomDomainDisplay,
                SiteLanguage = model.SiteLanguage,
                LogoUrl = model.LogoUrl
            });
        }
        else
        {
            existing.SiteTitle = model.SiteTitle;
            existing.TagLine = model.TagLine;
            existing.ThemeColor = model.ThemeColor;
            existing.FontFamilyOverride = model.FontFamilyOverride;
            existing.HeadingFontSizeOverride = model.HeadingFontSizeOverride;
            existing.BodyFontSizeOverride = model.BodyFontSizeOverride;
            existing.BackgroundColorOverride = model.BackgroundColorOverride;
            existing.ButtonStyleOverride = model.ButtonStyleOverride;
            existing.SectionSpacingOverride = model.SectionSpacingOverride;
            existing.HeroStyleOverride = model.HeroStyleOverride;
            existing.TemplateId = model.TemplateId;
            existing.CustomDomain = model.CustomDomain;
            existing.CustomDomainDisplay = model.CustomDomainDisplay;   // 552
            existing.SiteLanguage = model.SiteLanguage;   // 578
            // Capture the logo being replaced so it can be removed after the new one is safely stored.
            // Without this every re-upload stranded the previous file forever: agent-logos was holding
            // more blobs than there are agents, including four copies of the same logo. The agent-photo
            // path in AccountController already did this correctly; this is the same shape.
            var replacedLogoUrl = !string.IsNullOrEmpty(model.LogoUrl) && existing.LogoUrl != model.LogoUrl
                ? existing.LogoUrl
                : null;

            if (!string.IsNullOrEmpty(model.LogoUrl)) existing.LogoUrl = model.LogoUrl;
            await _websites.UpdateAsync(existing);

            // Only after the new URL is persisted -- deleting first would lose the old logo if the save
            // failed. And only when nothing else still references the old file (A5-H14's shape: a logo
            // can sit inside composed newsletter HTML); a kept file is cheap, a blanked image in
            // delivered mail is not undoable.
            if (!string.IsNullOrWhiteSpace(replacedLogoUrl) &&
                !await IPRO.DataAccess.BlobReferences.IsReferencedAsync(_db, replacedLogoUrl))
            {
                try { await _blob.DeleteAsync(replacedLogoUrl); }
                catch (Exception ex) { _logger.LogError(ex, "Replaced logo {Url} could not be deleted", replacedLogoUrl); }
            }
        }

        // 592: an unchanged custom domain that another account holds unproven (an older website's field with no row
        // of its own) does not stop the rest of the settings from saving; the name itself waits for a record.
        if (existing != null && claim?.Error == null)
        {
            // 595: a name this save adds to the domain list opens its guide, as Add does.
            var isNew = !string.IsNullOrWhiteSpace(model.CustomDomain) &&
                        !await _db.AgentDomains.AnyAsync(d => d.AgentUserId == AgentId && d.DomainName == model.CustomDomain);
            await SyncPrimaryDomainAsync(existing, model.CustomDomain, claim?.Token, claim?.ProvenNow == true);
            if (isNew)
            {
                TempData["OpenDomainGuide"] = await NewDomainIdAsync(model.CustomDomain);
                TempData["Success"] = "Website settings saved! Follow the steps below to connect " + model.CustomDomain + ".";
                return RedirectToAction(nameof(Domains));
            }
        }
        else if (claim?.Error != null)
        {
            TempData["Warning"] = claim.Value.Error;
        }

        TempData["Success"] = "Website settings saved!";
        return RedirectToAction(nameof(Index));
    }
    // 595 (owner, 2026-10-22): the custom domains have a page of their own, at full width, with its own address
    // (/portal/Website/Domains): the list, Add, each domain's guide, I need help, Primary, Remove, and the address on
    // the adviser's emails. My Website keeps a summary card that links here.
    [HttpGet]
    public async Task<IActionResult> Domains()
    {
        var gate = await RequireWebsiteAccessAsync();
        if (gate != null) return gate;

        var website = await _websites.GetByAgentIdAsync(AgentId);
        await LoadWebsiteContextAsync(website?.TemplateId);
        return View(website);
    }

    // 552's "Shown in your emails as", moved from My Website's settings form to the Domain page in 595, with the same rule:
    // the custom domain's own letters, only the capitals may differ.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DomainDisplay(string? customDomainDisplay)
    {
        var gate = await RequireWebsiteAccessAsync();
        if (gate != null) return gate;

        var website = await _websites.GetByAgentIdAsync(AgentId);
        if (website == null)
        {
            TempData["Error"] = "Save your website settings first.";
            return RedirectToAction(nameof(Index));
        }

        var display = (customDomainDisplay ?? string.Empty).Trim();
        if (display.Length > 0 && !string.Equals(display, website.CustomDomain, StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = string.IsNullOrEmpty(website.CustomDomain)
                ? "\"Shown in your emails as\" needs your custom domain first."
                : $"\"Shown in your emails as\" must be {website.CustomDomain} with your own capitals: the same letters, nothing added or left out.";
            return RedirectToAction(nameof(Domains));
        }

        website.CustomDomainDisplay = display;
        await _websites.UpdateAsync(website);
        TempData["Success"] = display.Length == 0 ? "Your emails show your domain in small letters." : $"Your emails show {display}.";
        return RedirectToAction(nameof(Domains));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish()
    {
        var gate = await RequireWebsiteAccessAsync();
        if (gate != null) return gate;

        // 496: the same provisioning sign-up now runs (WebsiteProvisioning), so the two cannot drift.
        var agent = await _agents.GetByIdAsync(AgentId);
        if (agent == null) return RedirectToAction(nameof(Index));
        await WebsiteProvisioning.EnsurePublishedAsync(_db, _websites, agent);

        TempData["Success"] = "Your website is now live!";
        return RedirectToAction(nameof(Index));
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Unpublish() { var gate = await RequireWebsiteAccessAsync(); if (gate != null) return gate; await _websites.UnpublishAsync(AgentId); TempData["Warning"] = "Website taken offline."; return RedirectToAction(nameof(Index)); }

    [HttpGet]
    public async Task<IActionResult> PreviewTemplate(int templateId, bool useDefaults = false)
    {
        var gate = await RequireWebsiteAccessAsync();
        if (gate != null) return gate;

        var model = await TemplatePreviewBuilder.BuildAsync(_db, AgentId, templateId, useDefaults);
        if (model == null) return NotFound();

        ViewBag.IsTemplatePreview = true;
        return View("~/Views/PublicWebsite/Index.cshtml", model);
    }

    private async Task<IActionResult?> RequireWebsiteAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.InstantWebsite);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Index", "Billing");
    }

    private async Task LoadWebsiteContextAsync(int? selectedTemplateId)
    {
        var agent = await _agents.GetByIdAsync(AgentId);
        ViewBag.OpenDomainGuide = TempData["OpenDomainGuide"] as int?;
        // 592: a domain still to be proven always has a value to show (a row saved by the version before 592
        // during its release has none yet).
        // Only where it is still empty: a value already shown is never replaced under the adviser.
        var withoutToken = await _db.AgentDomains
            .Where(d => d.AgentUserId == AgentId && d.OwnershipVerifiedAt == null && d.VerificationToken == "")
            .Select(d => new { d.Id, d.DomainName })
            .ToListAsync();
        foreach (var row in withoutToken)
        {
            var token = await DomainClaimTokens.GetOrCreateAsync(_db, AgentId, DomainOwnershipProof.ProvenNameOf(row.DomainName), DomainOwnershipProof.NewToken());
            await _db.AgentDomains
                .Where(d => d.Id == row.Id && d.VerificationToken == "")
                .ExecuteUpdateAsync(set => set.SetProperty(d => d.VerificationToken, token));
        }
        ViewBag.Templates = await _db.WebsiteTemplates
            .AsNoTracking()
            .Where(t => t.IsActive || t.Id == selectedTemplateId)
            .OrderByDescending(t => t.IsDefault)
            .ThenBy(t => t.Name)
            .ToListAsync();
        ViewBag.TemplateRetired = selectedTemplateId.HasValue && await _db.WebsiteTemplates
            .AnyAsync(t => t.Id == selectedTemplateId.Value && !t.IsActive);
        ViewBag.TemporaryDomain = agent?.DomainName ?? string.Empty;
        ViewBag.TemporaryRootDomain = _configuration["App:TemporarySiteRootDomain"] ?? "247advisers.com";
        ViewBag.WebsiteDnsTarget = _configuration["App:WebsiteDnsTarget"] ?? "ipro-prod-web.azurewebsites.net";
        // 553: the two values a short address needs to point straight at the platform -- the A
        // record's address and the TXT record that proves the domain to Azure. Neither is a secret
        // (both are published in every connected domain's DNS). Blank hides that way of doing it.
        ViewBag.WebsiteAddress = (_configuration["App:WebsiteAddress"] ?? string.Empty).Trim();
        ViewBag.DomainVerificationId = (_configuration["App:DomainVerificationId"] ?? string.Empty).Trim();
        ViewBag.PrimaryDomain = await _db.AgentDomains
            .AsNoTracking()
            .Where(d => d.AgentUserId == AgentId && d.IsPrimary)
            .OrderByDescending(d => d.UpdatedAt)
            .FirstOrDefaultAsync();
        ViewBag.AgentDomains = await _db.AgentDomains
            .AsNoTracking()
            .Where(d => d.AgentUserId == AgentId)
            .OrderByDescending(d => d.IsPrimary)
            .ThenBy(d => d.DomainName)
            .ToListAsync();
        // 595: each domain's guide, from what the domain check has recorded and the last look the page took (kept a few
        // seconds); the page then looks again on its own. Opening My Website never waits for DNS.
        var settings = DomainSetupGuide.Settings.From(_configuration);
        var now = DateTime.UtcNow;
        ViewBag.DomainGuides = ((List<AgentDomain>)ViewBag.AgentDomains).ToDictionary(d => d.Id,
            d => DomainSetupGuide.Describe(d, _cache != null && _cache.TryGetValue(LookKey(d), out DomainSetupLook? kept) ? kept : null, settings, now));
        ViewBag.DomainAccess = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.MultiDomainSupport);
        ViewBag.AgentTimeZone = AgentTimeZoneHelper.Normalize(agent?.TimeZone);
    }

    private static string NormalizeThemeColor(string? value)
    {
        var color = value?.Trim() ?? string.Empty;
        return color.Length == 7 && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit)
            ? color
            : "#1457d9";
    }

    private static int NormalizeFontSize(int value, int min, int max)
    {
        return value <= 0 ? 0 : Math.Clamp(value, min, max);
    }

    private static string NormalizeOptionalColor(string? value)
    {
        var color = value?.Trim() ?? string.Empty;
        return color.Length == 7 && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit) ? color : string.Empty;
    }

    private static string NormalizeOptionalOption(string? value, params string[] allowed)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return allowed.Contains(normalized) ? normalized : string.Empty;
    }

    // 592: each add can drop a lapsed claim and ask Azure to remove its addresses; each remove asks Azure.
    [EnableRateLimiting(EndpointRateLimits.DomainRetry)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddDomain(string domainName)
    {
        var gate = await RequireWebsiteAccessAsync();
        if (gate != null) return gate;

        var normalized = NormalizeDomain(domainName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            TempData["Error"] = "Enter a valid domain name.";
            return RedirectToAction(nameof(Domains));
        }

        var existingWebsite = await _websites.GetByAgentIdAsync(AgentId);
        if (existingWebsite == null)
        {
            TempData["Error"] = "Save your website settings before adding custom domains.";
            return RedirectToAction(nameof(Index));
        }

        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.MultiDomainSupport);
        if (!access.IsIncluded)
        {
            TempData["Error"] = access.UpgradeMessage;
            return RedirectToAction(nameof(Domains));
        }

        var currentCount = await _db.AgentDomains.CountAsync(d => d.AgentUserId == AgentId);
        if (access.LimitValue.HasValue && access.LimitValue.Value >= 0 && currentCount >= access.LimitValue.Value)
        {
            TempData["Error"] = $"Your current package allows {access.LimitValue.Value} custom domain(s). Upgrade to add more.";
            return RedirectToAction(nameof(Domains));
        }

        var claim = await DescribeDomainClaimAsync(normalized, AgentId);
        if (claim != null)
        {
            TempData["Error"] = claim;
            return RedirectToAction(nameof(Domains));
        }

        var takeover = await TakeOverUnprovenClaimsAsync(normalized, AgentId);
        if (takeover.Error != null)
        {
            TempData["Error"] = takeover.Error;
            return RedirectToAction(nameof(Domains));
        }

        var parts = BuildDomainParts(normalized);
        _db.AgentDomains.Add(new AgentDomain
        {
            AgentUserId = AgentId,
            AgentWebsiteId = existingWebsite.Id,
            DomainName = normalized,
            RootDomain = parts.Root,
            WwwDomain = parts.Www,
            DnsTarget = _configuration["App:WebsiteDnsTarget"] ?? "ipro-prod-web.azurewebsites.net",
            DnsStatus = AgentDomainStatus.PendingDns,
            AzureBindingStatus = AgentDomainStatus.BindingPending,
            SslStatus = AgentDomainStatus.BindingPending,
            VerificationToken = takeover.Token,                                    // 592
            OwnershipVerifiedAt = takeover.ProvenNow ? DateTime.UtcNow : null,     // 592: moved here on its record
            IsPrimary = currentCount == 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        if (currentCount == 0)
        {
            FollowPrimaryDomain(existingWebsite, normalized);
            await _websites.UpdateAsync(existingWebsite);
        }

        // 595: its guide opens on My Website, from step 1, whatever other domains are connected.
        TempData["OpenDomainGuide"] = await NewDomainIdAsync(normalized);
        TempData["Success"] = takeover.ProvenNow
            ? $"{normalized} was added. Its TXT record shows it is yours; follow the next steps in its guide below."
            : $"{normalized} was added. Follow the steps in its guide below.";
        return RedirectToAction(nameof(Domains));
    }

    [EnableRateLimiting(EndpointRateLimits.DomainRetry)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveDomain(int id, string confirmDomainName)
    {
        var domain = await _db.AgentDomains.FirstOrDefaultAsync(d => d.Id == id && d.AgentUserId == AgentId);
        if (domain == null)
        {
            TempData["Error"] = "That domain could not be found.";
            return RedirectToAction(nameof(Domains));
        }

        if (!string.Equals(confirmDomainName?.Trim(), domain.DomainName, StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "Type the exact domain name to confirm removal.";
            return RedirectToAction(nameof(Domains));
        }

        var domainNameForCleanup = domain.DomainName;
        var wasPrimary = domain.IsPrimary;
        // 592: the domain's addresses in Azure -- 553's short address too, when it was pointed straight at the
        // platform -- are written down in the same save that removes the row, and removed from Azure until Azure
        // confirms (DomainCleanupQueue). Asking once and dropping the answer is how www.411trades.com stayed bound
        // after it left its adviser's account, ready for the next adviser to take over.
        var cleanups = DomainCleanupQueue.Add(_db, domain, DomainCleanupReasons.RemovedByAdviser);
        // The adviser's value for the name goes too: the record they published must not prove it again later.
        await DomainClaimTokens.ForgetAsync(_db, AgentId, DomainOwnershipProof.ProvenName(domain), domain.Id);
        _db.AgentDomains.Remove(domain);
        await _db.SaveChangesAsync();

        var website = await _websites.GetByAgentIdAsync(AgentId);
        if (website != null && wasPrimary)
        {
            var next = await _db.AgentDomains
                .Where(d => d.AgentUserId == AgentId)
                .OrderBy(d => d.DomainName)
                .FirstOrDefaultAsync();
            if (next != null)
            {
                next.IsPrimary = true;
                next.UpdatedAt = DateTime.UtcNow;
                FollowPrimaryDomain(website, next.DomainName);
            }
            else
            {
                FollowPrimaryDomain(website, string.Empty);
            }

            await _db.SaveChangesAsync();
            await _websites.UpdateAsync(website);
        }

        // 591: a host another domain row still names is that row's binding and certificate (DomainCleanupQueue
        // keeps it). A removal Azure does not confirm now is asked again by the domain job.
        await DomainCleanupQueue.AttemptAsync(_db, _azureDomains, _configuration, _logger, cleanups);

        TempData["Success"] = $"{domainNameForCleanup} was removed.";
        return RedirectToAction(nameof(Domains));
    }

    [EnableRateLimiting(EndpointRateLimits.DomainRetry)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryDomain(int id)
    {
        var domain = await _db.AgentDomains.FirstOrDefaultAsync(d => d.Id == id && d.AgentUserId == AgentId);
        if (domain == null)
        {
            TempData["Error"] = "That domain could not be found.";
            return RedirectToAction(nameof(Domains));
        }

        // 15 seconds, not 2 minutes. The background job now also touches fully-bound domains, so
        // LastCheckedAt moves on its own -- an agent clicking this button had a good chance of
        // landing inside a 2-minute cooldown and being told to wait, with the message rendered at
        // the top of a long page they were scrolled to the bottom of. From their side the button
        // simply did nothing. This is a logged-in agent rechecking their own domain; 15 seconds is
        // ample protection.
        var cooldown = TimeSpan.FromSeconds(15);
        if (domain.LastCheckedAt.HasValue && DateTime.UtcNow - domain.LastCheckedAt.Value < cooldown)
        {
            TempData["Error"] = $"Just checked {domain.DomainName} a moment ago — give it a few seconds and try again.";
            return RedirectToAction(nameof(Domains));
        }

        var bound = await _domainCheck.CheckAsync(domain);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // 592: moved to the account whose record is published while it was being checked.
            TempData["Error"] = "That domain could not be found.";
            return RedirectToAction(nameof(Domains));
        }

        // Always say what was found, including the forwarding result. Previously a successful check
        // that left forwarding unchanged produced a message about binding only, so the button looked
        // inert to anyone watching the forwarding badge.
        // 553: the short address has two ways to work now, and one of them (pointed straight at
        // us) has stages of its own; RootLastError already says which stage in plain words.
        var rootPart = string.Empty;
        if (ShortAddressState.HasShortAddress(domain))
        {
            var shortAddress = ShortAddressState.Describe(domain);
            rootPart = shortAddress.Direct
                ? shortAddress.Working
                    ? $" {domain.RootDomain} is connected and secured too."
                    : " " + (string.IsNullOrWhiteSpace(domain.RootLastError) ? $"{domain.RootDomain} is being connected." : domain.RootLastError)
                : shortAddress.Working
                    ? $" {domain.RootDomain} forwards correctly."
                    : $" {domain.RootDomain} is not forwarding yet.";
        }

        TempData[bound ? "Success" : "Error"] = bound
            ? $"Checked just now: {domain.DomainName} is connected and secured.{rootPart}"
            : DomainErrorTranslator.ToAgentMessage(domain.LastError) + rootPart;

        return RedirectToAction(nameof(Domains));
    }

    // 595: the guide's own refresh, asked by My Website about every 20 seconds while a domain's guide is open (and by its
    // Check now). The adviser's own row only. What DNS shows is looked at for that row's names alone
    // (DomainCheckService.LookAsync) and kept 15 seconds, so several open panels or clicks ask DNS once. The domain
    // check, which asks Azure, runs only when DNS is ahead of the row or the adviser pressed Check now (asked), at most
    // once a minute (ShouldCheckNow); the five-minute job does the rest. A POST with the page's antiforgery token: it can
    // start that check. Counted per adviser here (GuideLooks) as well as per address by the rate limiter.
    [EnableRateLimiting(EndpointRateLimits.DomainGuide)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DomainGuide(int id, bool asked = false)
    {
        if (!GuideLooks.Allow(AgentId, DateTime.UtcNow)) return TooMany();
        var domain = await _db.AgentDomains.FirstOrDefaultAsync(d => d.Id == id && d.AgentUserId == AgentId);
        if (domain == null) return NotFound();

        var settings = DomainSetupGuide.Settings.From(_configuration);
        var look = await LookAsync(domain);
        var now = DateTime.UtcNow;
        var guide = DomainSetupGuide.Describe(domain, look, settings, now);
        if (DomainSetupGuide.ShouldCheckNow(domain, guide, now, asked))
        {
            // 595's review (H1): a check the page asks for is not one of the five-minute job's tries. One that finds
            // nothing new gives the job its budget back, so a page left open can never use it up and strand the domain
            // (the job's checks are also what raise the overdue-certificate alert). Progress resets it in CheckAsync.
            var budget = (domain.RetryCount, domain.NextRetryAt, domain.AutoRetryExhausted, domain.LastFailedAt);
            var bound = await _domainCheck.CheckAsync(domain);
            if (!bound && domain.RetryCount > budget.RetryCount)
            {
                (domain.RetryCount, domain.NextRetryAt, domain.AutoRetryExhausted, domain.LastFailedAt) = budget;
            }
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // 592: moved to the account whose record is published while it was being checked.
                return NotFound();
            }
            guide = DomainSetupGuide.Describe(domain, look, settings, DateTime.UtcNow);
            guide.CheckedNow = true;
        }

        return Json(guide, GuideJson);
    }

    // 595: "I need help" -- a new support ticket, written for the adviser with where they are and, for every record, what
    // iPro expects beside what it sees. They read it, add a line if they like, and send it like any other ticket.
    [EnableRateLimiting(EndpointRateLimits.DomainGuide)]
    [HttpGet]
    public async Task<IActionResult> DomainHelp(int id)
    {
        if (!GuideLooks.Allow(AgentId, DateTime.UtcNow)) return TooMany();
        var domain = await _db.AgentDomains.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id && d.AgentUserId == AgentId);
        if (domain == null) return NotFound();

        var guide = DomainSetupGuide.Describe(domain, await LookAsync(domain), DomainSetupGuide.Settings.From(_configuration), DateTime.UtcNow);
        var subject = $"Help connecting {domain.DomainName}";
        ViewBag.Subject = subject.Length <= 200 ? subject : subject[..200];   // the ticket's subject column holds 200
        ViewBag.Message = "I need help connecting my domain. Here is where I am:\n\n" + DomainSetupGuide.SupportMessage(guide);
        return View("~/Views/Support/Create.cshtml");
    }

    private IActionResult TooMany()
    {
        Response.Headers.RetryAfter = "20";
        return StatusCode(StatusCodes.Status429TooManyRequests);
    }

    // 595's review (L-a): the guide's looks per adviser, 20 a minute -- several guides on screen and Check now fit, a
    // script does not. The rate limiter cannot do this: it runs before the sign-in is read, so it counts per address.
    internal static class GuideLooks
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Queue<DateTime>> Recent = new();
        internal const int PerWindow = 20;
        internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        internal static bool Allow(int agentId, DateTime utcNow)
        {
            if (Recent.Count > 10000) Recent.Clear();
            var times = Recent.GetOrAdd(agentId, _ => new Queue<DateTime>());
            lock (times)
            {
                while (times.Count > 0 && utcNow - times.Peek() > Window) times.Dequeue();
                if (times.Count >= PerWindow) return false;
                times.Enqueue(utcNow);
                return true;
            }
        }
    }

    private static readonly JsonSerializerOptions GuideJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static string LookKey(AgentDomain domain) => $"595-look:{domain.Id}:{domain.DomainName}";

    // 595's review (M4): at most ten seconds; a domain whose name servers hang shows what is recorded instead.
    private static readonly TimeSpan LookBudget = TimeSpan.FromSeconds(10);

    private async Task<DomainSetupLook?> LookAsync(AgentDomain domain)
    {
        if (_cache != null && _cache.TryGetValue(LookKey(domain), out DomainSetupLook? kept)) return kept;
        var aborted = HttpContext?.RequestAborted ?? default;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        budget.CancelAfter(LookBudget);
        DomainSetupLook? look;
        try
        {
            look = await _domainCheck.LookAsync(domain, budget.Token);
        }
        catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
        {
            look = null;
        }
        _cache?.Set(LookKey(domain), look, TimeSpan.FromSeconds(15));
        return look;
    }

    private Task<int> NewDomainIdAsync(string domainName) =>
        _db.AgentDomains.Where(d => d.AgentUserId == AgentId && d.DomainName == domainName).Select(d => d.Id).FirstOrDefaultAsync();

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPrimaryDomain(int id)
    {
        var selected = await _db.AgentDomains.FirstOrDefaultAsync(d => d.Id == id && d.AgentUserId == AgentId);
        if (selected == null)
        {
            TempData["Error"] = "That domain could not be found.";
            return RedirectToAction(nameof(Domains));
        }

        var domains = await _db.AgentDomains.Where(d => d.AgentUserId == AgentId).ToListAsync();
        foreach (var domain in domains)
        {
            domain.IsPrimary = domain.Id == selected.Id;
            domain.UpdatedAt = DateTime.UtcNow;
        }

        var website = await _websites.GetByAgentIdAsync(AgentId);
        if (website != null)
        {
            FollowPrimaryDomain(website, selected.DomainName);
            await _websites.UpdateAsync(website);
        }

        await _db.SaveChangesAsync();
        TempData["Success"] = $"{selected.DomainName} is now your primary custom domain.";
        return RedirectToAction(nameof(Domains));
    }

    // 552: the address on the emails is the primary domain with the adviser's own capitals. When the primary domain
    // changes, one typed for another domain goes (595's third review, M-1): the emails use small letters until the
    // adviser types the new one on the Domain page.
    private static void FollowPrimaryDomain(AgentWebsite website, string customDomain)
    {
        website.CustomDomain = customDomain;
        if (!string.Equals(website.CustomDomainDisplay, customDomain, StringComparison.OrdinalIgnoreCase))
            website.CustomDomainDisplay = string.Empty;
    }

    private async Task SyncPrimaryDomainAsync(AgentWebsite website, string customDomain, string? claimToken = null, bool provenNow = false)
    {
        var currentPrimary = await _db.AgentDomains
            .Where(d => d.AgentUserId == AgentId && d.IsPrimary)
            .OrderByDescending(d => d.UpdatedAt)
            .FirstOrDefaultAsync();

        if (string.IsNullOrWhiteSpace(customDomain))
        {
            if (currentPrimary != null)
            {
                currentPrimary.IsPrimary = false;
                currentPrimary.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }

            return;
        }

        var domainParts = BuildDomainParts(customDomain);
        var dnsTarget = _configuration["App:WebsiteDnsTarget"] ?? "ipro-prod-web.azurewebsites.net";
        // Scoped to THIS agent deliberately. Without the ownership filter this lookup could return
        // another agent's row and the writes below would re-parent it -- serving their branded domain
        // from this agent's site and capturing every lead posted there. That is the CRITICAL closed on
        // 2026-08-05 (post-mortem further down this file); until now it was blocked only by
        // DescribeDomainClaimAsync 400 lines earlier, which is both racy and one refactor from being
        // bypassed. An unowned row now simply isn't found, so the claim check stays the only way in.
        var domain = await _db.AgentDomains.FirstOrDefaultAsync(d => d.DomainName == customDomain && d.AgentUserId == AgentId);
        if (domain == null)
        {
            domain = new AgentDomain
            {
                AgentUserId = AgentId,
                AgentWebsiteId = website.Id,
                DomainName = customDomain,
                RootDomain = domainParts.Root,
                WwwDomain = domainParts.Www,
                DnsTarget = dnsTarget,
                DnsStatus = AgentDomainStatus.PendingDns,
                AzureBindingStatus = AgentDomainStatus.BindingPending,
                SslStatus = AgentDomainStatus.BindingPending,
                // 592: the adviser's value for the name, kept across removals.
                VerificationToken = claimToken ?? await DomainClaimTokens.GetOrCreateAsync(_db, AgentId, DomainOwnershipProof.ProvenNameOf(customDomain), DomainOwnershipProof.NewToken()),
                OwnershipVerifiedAt = provenNow ? DateTime.UtcNow : null,
                IsPrimary = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.AgentDomains.Add(domain);
        }
        else
        {
            domain.AgentUserId = AgentId;
            domain.AgentWebsiteId = website.Id;
            domain.RootDomain = domainParts.Root;
            domain.WwwDomain = domainParts.Www;
            domain.DnsTarget = dnsTarget;
            domain.IsPrimary = true;
            domain.UpdatedAt = DateTime.UtcNow;
            if (domain.DnsStatus == AgentDomainStatus.Failed)
            {
                domain.DnsStatus = AgentDomainStatus.PendingDns;
            }
        }

        var otherPrimaries = await _db.AgentDomains
            .Where(d => d.AgentUserId == AgentId && d.Id != domain.Id && d.IsPrimary)
            .ToListAsync();
        foreach (var other in otherPrimaries)
        {
            other.IsPrimary = false;
            other.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
    }

    // Returns an error message when this agent may not claim `normalized`, or null when it is free.
    //
    // WHY THIS IS CENTRAL AND STRICT (2026-08-05 audit, Critical)
    // The old check asked only "does an AgentDomains row already have this DomainName". That missed
    // the two places a domain is far more likely to already be spoken for:
    //
    //   1. AgentUsers.DomainName -- every agent's auto-provisioned {name}.247advisers.com site. No
    //      agent has an AgentDomains row for it, so the old check never objected.
    //   2. AgentWebsites.CustomDomain -- set directly by the Save path.
    //
    // That mattered because PublicWebsiteController.FindWebsiteForHostAsync resolves a request by
    // querying AgentDomains FIRST and only falls back to AgentUser.DomainName afterwards. So an agent
    // who added another agent's provisioned domain took over serving it: the victim's branded URL
    // rendered the attacker's site, and every lead, form and testimonial posted there was written
    // with the attacker's AgentUserId. No DNS control was needed -- the row alone was enough.
    //
    // It also only compared DomainName, while host resolution matches RootDomain and WwwDomain too,
    // so "www.x.example" could collide with a victim owning "x.example". All three are compared here.
    private async Task<string?> DescribeDomainClaimAsync(string normalized, int agentId)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return null;

        var parts = BuildDomainParts(normalized);
        var variants = new[] { normalized, parts.Root, parts.Www }
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.ToLowerInvariant())
            .Distinct()
            .ToList();

        // Custom domains are domains the agent owns and points at us. The platform's own zone is
        // never one of those, and every agent's free site already lives under it -- so allowing a
        // claim here can only ever be a land-grab on somebody else's site. Blocking the whole zone
        // is broader than the exact bug and deliberately so: it needs no lookup and cannot be raced.
        var platformZone = (_configuration["App:TemporarySiteRootDomain"] ?? "247advisers.com").ToLowerInvariant();
        if (variants.Any(v => v == platformZone || v.EndsWith("." + platformZone, StringComparison.Ordinal)))
        {
            return $"{normalized} belongs to the IPRO platform. Your free site is already reachable there — "
                 + "add a custom domain only for a domain you own and control.";
        }

        // 589: and none of iPro's other names -- the platform's own address, the brand names, the admin host,
        // the App Service's own name (PlatformHostNames). Removing such a row sent Azure a DELETE for that
        // name's binding and certificate on the production web app.
        if (variants.Any(v => PlatformHostNames.IsPlatformHost(v, _configuration)))
        {
            return $"{normalized} is one of IPRO's own addresses, so it cannot be your custom domain. "
                 + "Add a domain you own and control.";
        }

        // 589's review: the domain automation also refuses a name that differs from one of iPro's only in a dot
        // or a dash (its certificate would carry the same name), and anything that is no host name at all; say
        // so now rather than leave a domain that can never connect.
        if (variants.Any(v => PlatformHostNames.SharesCertificateNameWithPlatformHost(v, _configuration)))
        {
            return $"{normalized} is too close to one of IPRO's own addresses to be connected here. "
                 + "Add a domain you own and control.";
        }

        if (variants.Any(v => !PlatformHostNames.TryCanonicalHost(v, out _)))
        {
            return "Enter a valid domain name.";
        }

        // 592: proven rows only; another adviser's unproven row is TakeOverUnprovenClaimsAsync's to decide.
        if (await _db.AgentDomains.AnyAsync(d => d.AgentUserId != agentId && d.OwnershipVerifiedAt != null &&
                (variants.Contains(d.DomainName.ToLower())
                 || variants.Contains(d.RootDomain.ToLower())
                 || variants.Contains(d.WwwDomain.ToLower()))))
        {
            return "That custom domain is already connected to another IPRO account.";
        }

        if (await _db.AgentUsers.AnyAsync(u => u.Id != agentId && variants.Contains(u.DomainName.ToLower())))
        {
            return "That domain is already in use by another IPRO account.";
        }

        // 592: a website whose adviser's claim to the name is an unproven row is that row's case, as above.
        if (await _db.AgentWebsites.AnyAsync(w => w.AgentUserId != agentId && variants.Contains(w.CustomDomain.ToLower()) &&
                !_db.AgentDomains.Any(d => d.AgentUserId == w.AgentUserId && d.OwnershipVerifiedAt == null &&
                    (variants.Contains(d.DomainName.ToLower()) || variants.Contains(d.RootDomain.ToLower()) || variants.Contains(d.WwwDomain.ToLower())))))
        {
            return "That custom domain is already connected to another IPRO account.";
        }

        return null;
    }

    // 592: what to do with a name another adviser's UNPROVEN row holds (any of its three forms). The claim check
    // above lets such a row through, so the real owner is never locked out by an account that added their domain
    // and never proved it; it moves only on the strength of a record:
    //   - the holder's own record is published: the domain is theirs; their row is proven now, this one refused;
    //   - this adviser's record (their value for this name, DomainClaimTokens) is published: the holder's rows are
    //     dropped like a Remove of theirs, and this adviser's row is created proven;
    //   - neither: refused, with the record that would move it here.
    // Called after every other refusal and the package checks, so a request that is refused anyway changes nothing.
    // Returns this adviser's value for the name, and whether the row is to be created proven.
    private async Task<(string? Error, string Token, bool ProvenNow)> TakeOverUnprovenClaimsAsync(string normalized, int agentId)
    {
        var parts = BuildDomainParts(normalized);
        var variants = new[] { normalized, parts.Root, parts.Www }
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.ToLowerInvariant())
            .Distinct()
            .ToList();
        var provenName = DomainOwnershipProof.ProvenNameOf(normalized);
        var token = await DomainClaimTokens.GetOrCreateAsync(_db, agentId, provenName, DomainOwnershipProof.NewToken());

        var holders = await _db.AgentDomains.AsNoTracking()
            .Where(d => d.AgentUserId != agentId && d.OwnershipVerifiedAt == null &&
                        (variants.Contains(d.DomainName.ToLower()) || variants.Contains(d.RootDomain.ToLower()) || variants.Contains(d.WwwDomain.ToLower())))
            .ToListAsync();
        if (holders.Count == 0) return (null, token, false);

        // Each look asks two public DNS resolvers; an adviser gets a handful of them in ten minutes, not a stream.
        if (!TakeoverLooks.Allow(agentId, DateTime.UtcNow))
        {
            return ("We checked that domain's DNS several times just now. Please wait a few minutes and try again.", token, false);
        }

        foreach (var holder in holders.Where(h => !string.IsNullOrWhiteSpace(h.VerificationToken) &&
                                                  DomainOwnershipProof.Covers(h, h.RootDomain) && DomainOwnershipProof.Covers(h, h.WwwDomain)))
        {
            if (await _domainCheck.IsOwnershipPublishedAsync(DomainOwnershipProof.ProvenName(holder), holder.VerificationToken) == true)
            {
                await _db.AgentDomains.Where(d => d.Id == holder.Id && d.OwnershipVerifiedAt == null)
                    .ExecuteUpdateAsync(set => set.SetProperty(d => d.OwnershipVerifiedAt, DateTime.UtcNow));
                _logger.LogWarning("Custom domain {Domain} (row {Id}, agent {Owner}) proven by its record when agent {Agent} tried to add it",
                    PlatformHostNames.Printable(holder.DomainName), holder.Id, holder.AgentUserId, agentId);
                return ("That custom domain is already connected to another IPRO account.", token, false);
            }
        }

        if (await _domainCheck.IsOwnershipPublishedAsync(provenName, token) != true)
        {
            return ($"Another IPRO account has added {provenName} but has not proven it is theirs. If the domain is yours, add this " +
                    $"TXT record where its DNS is managed, then add the domain again: it then moves to your account. " +
                    $"Name: {DomainOwnershipProof.NameToTypeFor(provenName)}  Value: {token}", token, false);
        }

        // All the holders or none: one of them proving it in the meantime keeps every one of them.
        await using var move = await _db.Database.BeginTransactionAsync();
        foreach (var holder in holders)
        {
            // Only while it is still unproven: a holder that proved it in the meantime keeps it.
            var dropped = await _db.AgentDomains.Where(d => d.Id == holder.Id && d.OwnershipVerifiedAt == null).ExecuteDeleteAsync();
            if (dropped != 1)
            {
                await move.RollbackAsync();
                _db.ChangeTracker.Clear();
                return ("That custom domain is already connected to another IPRO account.", token, false);
            }

            _logger.LogWarning("Unproven domain {Domain} (row {Id}, agent {Owner}) moved to agent {Agent}, whose own record is published",
                PlatformHostNames.Printable(holder.DomainName), holder.Id, holder.AgentUserId, agentId);
            // Its addresses are left to the domain job, ten minutes on: by then this adviser's proven row names them and
            // keeps them. The holder's value for the name goes with its row.
            DomainCleanupQueue.Add(_db, holder, DomainCleanupReasons.UnprovenClaimMoved, TimeSpan.FromMinutes(10));
            await DomainClaimTokens.ForgetAsync(_db, holder.AgentUserId, DomainOwnershipProof.ProvenName(holder), holder.Id);
            var website = await _db.AgentWebsites.FirstOrDefaultAsync(w => w.AgentUserId == holder.AgentUserId);
            if (website != null && string.Equals(website.CustomDomain, holder.DomainName, StringComparison.OrdinalIgnoreCase))
            {
                var next = await _db.AgentDomains.Where(d => d.AgentUserId == holder.AgentUserId).OrderBy(d => d.DomainName).FirstOrDefaultAsync();
                if (next != null)
                {
                    next.IsPrimary = true;
                    next.UpdatedAt = DateTime.UtcNow;
                }
                website.CustomDomain = next?.DomainName ?? string.Empty;
                website.CustomDomainDisplay = string.Empty;
            }
            await _db.SaveChangesAsync();
        }

        await move.CommitAsync();
        return (null, token, true);
    }

    // 592: how often one adviser's attempts to add a name another account holds unproven may ask DNS (two public
    // resolvers per look, up to five seconds each). Per app instance; the add itself is rate limited as well.
    internal static class TakeoverLooks
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Queue<DateTime>> Recent = new();
        internal const int PerWindow = 10;
        internal static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

        internal static bool Allow(int agentId, DateTime utcNow)
        {
            var times = Recent.GetOrAdd(agentId, _ => new Queue<DateTime>());
            lock (times)
            {
                while (times.Count > 0 && utcNow - times.Peek() > Window) times.Dequeue();
                if (times.Count >= PerWindow) return false;
                times.Enqueue(utcNow);
                return true;
            }
        }
    }

    private static (string Root, string Www) BuildDomainParts(string domain)
    {
        if (domain.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            var root = domain[4..];
            return (root, domain);
        }

        return (domain, ShouldUseWwwHost(domain) ? "www." + domain : domain);
    }

    internal static string NormalizeDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return string.Empty;

        var value = domain.Trim().ToLowerInvariant();
        value = value.Replace("https://", string.Empty).Replace("http://", string.Empty);
        foreach (var separator in new[] { '/', '?', '#' })
        {
            var index = value.IndexOf(separator);
            if (index >= 0)
            {
                value = value[..index];
            }
        }

        // 492 (audit L1): "user@example.com" pasted into the domain box is the domain, not a mailbox.
        var atIndex = value.LastIndexOf('@');
        if (atIndex >= 0)
        {
            value = value[(atIndex + 1)..];
        }

        value = value.Trim().Trim('.');
        var portIndex = value.IndexOf(':');
        if (portIndex >= 0)
        {
            // 589's review: and trimmed again, or "example.com.:443" kept its dot -- a name the claim check
            // compares as different from another adviser's example.com, and the domain automation reads as it.
            value = value[..portIndex].Trim().Trim('.');
        }

        return ShouldUseWwwHost(value) ? "www." + value : value;
    }

    private static bool ShouldUseWwwHost(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;
        if (domain.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return false;

        var labels = domain.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return labels.Length == 2;
    }

    private static string BuildDefaultSiteTitle(AgentUser? agent)
    {
        if (agent == null) return "IPRO Advisers";

        var fullName = string.Join(" ", new[] { agent.FirstName, agent.LastName }
            .Where(part => !string.IsNullOrWhiteSpace(part)))
            .Trim();

        return string.IsNullOrWhiteSpace(fullName) ? agent.CompanyName : fullName;
    }

}
