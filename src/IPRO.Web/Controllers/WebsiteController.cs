using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public WebsiteController(IWebsiteService websites, IBlobStorageService blob, IPackageEntitlementService entitlements, IAgentService agents, IConfiguration configuration, IPRODbContext db,
        IDomainCheckService domainCheck, IAzureDomainAutomationService azureDomains, ILogger<WebsiteController> logger)
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
    }

    public async Task<IActionResult> Index()
    {
        var gate = await RequireWebsiteAccessAsync();
        if (gate != null) return gate;

        var website = await _websites.GetByAgentIdAsync(AgentId);
        await LoadWebsiteContextAsync(website?.TemplateId);
        return View(website);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> Save(AgentWebsite model, IFormFile? logo, bool applyTemplateDefaults = false)
    {
        var gate = await RequireWebsiteAccessAsync();
        if (gate != null) return gate;

        if (model.TemplateId <= 0)
        {
            var agent = await _agents.GetByIdAsync(AgentId);
            model.TemplateId = (await _websites.EnsureDefaultTemplateForPackageAsync(agent?.PackageId, agent?.BusinessType)).Id;
        }

        var existing = await _websites.GetByAgentIdAsync(AgentId);
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

            var logoExtension = Path.GetExtension(logo.FileName).ToLowerInvariant();
            var expectedLogoContentType = logoExtension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => string.Empty
            };
            if (string.IsNullOrEmpty(expectedLogoContentType) ||
                !string.Equals(logo.ContentType, expectedLogoContentType, StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Only JPG, JPEG, PNG, GIF, and WebP image files are allowed for your logo.";
                return RedirectToAction(nameof(Index));
            }

            await using var logoStream = logo.OpenReadStream();
            if (!await HasValidImageSignatureAsync(logoStream, logoExtension))
            {
                TempData["Error"] = "That file does not contain a valid supported image.";
                return RedirectToAction(nameof(Index));
            }
            logoStream.Position = 0;

            model.LogoUrl = await _blob.UploadAsync(logoStream, logo.FileName, "agent-logos", expectedLogoContentType, isPrivate: false);
        }

        model.CustomDomain = NormalizeDomain(model.CustomDomain);
        model.SiteTitle = model.SiteTitle?.Trim() ?? string.Empty;
        model.TagLine = model.TagLine?.Trim() ?? string.Empty;
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

        if (existing == null)
        {
            model.AgentUserId = AgentId;
            existing = await _websites.CreateAsync(model);
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
            // Capture the logo being replaced so it can be removed after the new one is safely stored.
            // Without this every re-upload stranded the previous file forever: agent-logos was holding
            // more blobs than there are agents, including four copies of the same logo. The agent-photo
            // path in AccountController already did this correctly; this is the same shape.
            var replacedLogoUrl = !string.IsNullOrEmpty(model.LogoUrl) && existing.LogoUrl != model.LogoUrl
                ? existing.LogoUrl
                : null;

            if (!string.IsNullOrEmpty(model.LogoUrl)) existing.LogoUrl = model.LogoUrl;
            await _websites.UpdateAsync(existing);

            // Only after the new URL is persisted -- deleting first would lose the old logo if the save failed.
            if (!string.IsNullOrWhiteSpace(replacedLogoUrl))
            {
                try { await _blob.DeleteAsync(replacedLogoUrl); }
                catch (Exception ex) { _logger.LogError(ex, "Replaced logo {Url} could not be deleted", replacedLogoUrl); }
            }
        }

        if (existing != null)
        {
            await SyncPrimaryDomainAsync(existing, model.CustomDomain);
        }

        TempData["Success"] = "Website settings saved!";
        return RedirectToAction(nameof(Index));
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish()
    {
        var gate = await RequireWebsiteAccessAsync();
        if (gate != null) return gate;

        var existing = await _websites.GetByAgentIdAsync(AgentId);
        if (existing == null)
        {
            var agent = await _agents.GetByIdAsync(AgentId);
            var template = await _websites.EnsureDefaultTemplateForPackageAsync(agent?.PackageId, agent?.BusinessType);
            existing = await _websites.CreateAsync(new AgentWebsite
            {
                AgentUserId = AgentId,
                TemplateId = template.Id,
                SiteTitle = BuildDefaultSiteTitle(agent),
                TagLine = "Professional service and client support.",
                ThemeColor = "#1457d9",
                IsPublished = true
            });
        }
        else
        {
            await _websites.PublishAsync(AgentId);
        }

        await WebsiteStarterPagesHelper.EnsureStarterPagesAsync(_db, existing, AgentId);
        await WebsiteStarterResourcesHelper.EnsureResourcesAsync(_db, existing, AgentId);

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

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddDomain(string domainName)
    {
        var gate = await RequireWebsiteAccessAsync();
        if (gate != null) return gate;

        var normalized = NormalizeDomain(domainName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            TempData["Error"] = "Enter a valid domain name.";
            return RedirectToAction(nameof(Index));
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
            return RedirectToAction(nameof(Index));
        }

        var currentCount = await _db.AgentDomains.CountAsync(d => d.AgentUserId == AgentId);
        if (access.LimitValue.HasValue && access.LimitValue.Value >= 0 && currentCount >= access.LimitValue.Value)
        {
            TempData["Error"] = $"Your current package allows {access.LimitValue.Value} custom domain(s). Upgrade to add more.";
            return RedirectToAction(nameof(Index));
        }

        var claim = await DescribeDomainClaimAsync(normalized, AgentId);
        if (claim != null)
        {
            TempData["Error"] = claim;
            return RedirectToAction(nameof(Index));
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
            IsPrimary = currentCount == 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        if (currentCount == 0)
        {
            existingWebsite.CustomDomain = normalized;
            await _websites.UpdateAsync(existingWebsite);
        }

        TempData["Success"] = $"{normalized} was added to your domain queue.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveDomain(int id, string confirmDomainName)
    {
        var domain = await _db.AgentDomains.FirstOrDefaultAsync(d => d.Id == id && d.AgentUserId == AgentId);
        if (domain == null)
        {
            TempData["Error"] = "That domain could not be found.";
            return RedirectToAction(nameof(Index));
        }

        if (!string.Equals(confirmDomainName?.Trim(), domain.DomainName, StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "Type the exact domain name to confirm removal.";
            return RedirectToAction(nameof(Index));
        }

        var domainNameForCleanup = domain.DomainName;
        var wasPrimary = domain.IsPrimary;
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
                website.CustomDomain = next.DomainName;
            }
            else
            {
                website.CustomDomain = string.Empty;
            }

            await _db.SaveChangesAsync();
            await _websites.UpdateAsync(website);
        }

        try
        {
            await _azureDomains.RemoveDomainAsync(domainNameForCleanup);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Best-effort Azure cleanup failed for {Domain} after removal", domainNameForCleanup);
        }

        TempData["Success"] = $"{domainNameForCleanup} was removed.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryDomain(int id)
    {
        var domain = await _db.AgentDomains.FirstOrDefaultAsync(d => d.Id == id && d.AgentUserId == AgentId);
        if (domain == null)
        {
            TempData["Error"] = "That domain could not be found.";
            return RedirectToAction(nameof(Index));
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
            return RedirectToAction(nameof(Index));
        }

        var bound = await _domainCheck.CheckAsync(domain);
        await _db.SaveChangesAsync();

        // Always say what was found, including the forwarding result. Previously a successful check
        // that left forwarding unchanged produced a message about binding only, so the button looked
        // inert to anyone watching the forwarding badge.
        var rootPart = string.IsNullOrWhiteSpace(domain.RootDomain) ||
                       string.Equals(domain.RootDomain, domain.DomainName, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : domain.RootRedirectsToWww
                ? $" {domain.RootDomain} forwards correctly."
                : $" {domain.RootDomain} is not forwarding yet.";

        TempData[bound ? "Success" : "Error"] = bound
            ? $"Checked just now: {domain.DomainName} is connected and secured.{rootPart}"
            : DomainErrorTranslator.ToAgentMessage(domain.LastError) + rootPart;

        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPrimaryDomain(int id)
    {
        var selected = await _db.AgentDomains.FirstOrDefaultAsync(d => d.Id == id && d.AgentUserId == AgentId);
        if (selected == null)
        {
            TempData["Error"] = "That domain could not be found.";
            return RedirectToAction(nameof(Index));
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
            website.CustomDomain = selected.DomainName;
            await _websites.UpdateAsync(website);
        }

        await _db.SaveChangesAsync();
        TempData["Success"] = $"{selected.DomainName} is now your primary custom domain.";
        return RedirectToAction(nameof(Index));
    }

    private async Task SyncPrimaryDomainAsync(AgentWebsite website, string customDomain)
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
        var domain = await _db.AgentDomains.FirstOrDefaultAsync(d => d.DomainName == customDomain);
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

        if (await _db.AgentDomains.AnyAsync(d => d.AgentUserId != agentId &&
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

        if (await _db.AgentWebsites.AnyAsync(w => w.AgentUserId != agentId && variants.Contains(w.CustomDomain.ToLower())))
        {
            return "That custom domain is already connected to another IPRO account.";
        }

        return null;
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

    private static string NormalizeDomain(string? domain)
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

        value = value.Trim().Trim('.');
        var portIndex = value.IndexOf(':');
        if (portIndex >= 0)
        {
            value = value[..portIndex];
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

    private static async Task<bool> HasValidImageSignatureAsync(Stream stream, string extension)
    {
        var header = new byte[12];
        var read = await stream.ReadAsync(header.AsMemory(0, header.Length));
        if (read < 6) return false;
        return extension switch
        {
            ".jpg" or ".jpeg" => header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".png" => read >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".gif" => System.Text.Encoding.ASCII.GetString(header, 0, 6) is "GIF87a" or "GIF89a",
            ".webp" => read >= 12 && System.Text.Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(header, 8, 4) == "WEBP",
            _ => false
        };
    }
}
