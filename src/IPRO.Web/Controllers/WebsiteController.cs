using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
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
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public WebsiteController(IWebsiteService websites, IBlobStorageService blob, IPackageEntitlementService entitlements, IAgentService agents, IConfiguration configuration, IPRODbContext db)
    {
        _websites = websites;
        _blob = blob;
        _entitlements = entitlements;
        _agents = agents;
        _configuration = configuration;
        _db = db;
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
        var templateChanged = existing is not null && existing.TemplateId != model.TemplateId;
        var selectedTemplate = await _db.WebsiteTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == model.TemplateId);
        if (selectedTemplate == null || (!selectedTemplate.IsActive && existing?.TemplateId != selectedTemplate.Id))
        {
            TempData["Error"] = "That website template is no longer available. Choose another active template.";
            return RedirectToAction(nameof(Index));
        }
        if (logo != null && logo.Length > 0)
        {
            using var s = logo.OpenReadStream();
            model.LogoUrl = await _blob.UploadAsync(s, logo.FileName, "agent-logos", logo.ContentType);
        }

        model.CustomDomain = NormalizeDomain(model.CustomDomain);
        model.SiteTitle = model.SiteTitle?.Trim() ?? string.Empty;
        model.TagLine = model.TagLine?.Trim() ?? string.Empty;
        model.ThemeColor = applyTemplateDefaults || existing is null || templateChanged
            ? WebsiteTemplateDesign.FromTemplate(selectedTemplate).AccentColor
            : NormalizeThemeColor(model.ThemeColor);

        if (applyTemplateDefaults || existing is null || templateChanged)
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

        if (!string.IsNullOrWhiteSpace(model.CustomDomain) &&
            await _db.AgentDomains.AnyAsync(d => d.DomainName == model.CustomDomain && d.AgentUserId != AgentId))
        {
            TempData["Error"] = "That custom domain is already connected to another IPRO account.";
            return RedirectToAction(nameof(Index));
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
            if (!string.IsNullOrEmpty(model.LogoUrl)) existing.LogoUrl = model.LogoUrl;
            await _websites.UpdateAsync(existing);
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
            await _websites.CreateAsync(new AgentWebsite
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

        var website = await _db.AgentWebsites
            .AsNoTracking()
            .Include(w => w.AgentUser)
            .FirstOrDefaultAsync(w => w.AgentUserId == AgentId);
        var template = await _db.WebsiteTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId && t.IsActive);
        if (website == null || template == null) return NotFound();

        var pages = await _db.WebsitePages
            .AsNoTracking()
            .Where(p => p.AgentWebsiteId == website.Id && p.IsPublished)
            .Include(p => p.Blocks)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Title)
            .ToListAsync();

        foreach (var page in pages)
        {
            page.Title ??= string.Empty;
            page.NavigationLabel ??= string.Empty;
            page.Slug ??= string.Empty;
            foreach (var block in page.Blocks)
            {
                block.Heading ??= string.Empty;
                block.Subheading ??= string.Empty;
                block.Body ??= string.Empty;
                block.ImageUrl ??= string.Empty;
                block.ButtonText ??= string.Empty;
                block.ButtonUrl ??= string.Empty;
                block.SettingsJson ??= "{}";
            }
        }

        var previewWebsite = new AgentWebsite
        {
            Id = website.Id,
            AgentUserId = website.AgentUserId,
            TemplateId = template.Id,
            CustomDomain = website.CustomDomain ?? string.Empty,
            SiteTitle = website.SiteTitle ?? string.Empty,
            TagLine = website.TagLine ?? string.Empty,
            LogoUrl = website.LogoUrl ?? string.Empty,
            ThemeColor = useDefaults
                ? WebsiteTemplateDesign.FromTemplate(template).AccentColor
                : website.ThemeColor,
            FontFamilyOverride = useDefaults ? string.Empty : website.FontFamilyOverride,
            HeadingFontSizeOverride = useDefaults ? 0 : website.HeadingFontSizeOverride,
            BodyFontSizeOverride = useDefaults ? 0 : website.BodyFontSizeOverride,
            BackgroundColorOverride = useDefaults ? string.Empty : website.BackgroundColorOverride,
            ButtonStyleOverride = useDefaults ? string.Empty : website.ButtonStyleOverride,
            SectionSpacingOverride = useDefaults ? string.Empty : website.SectionSpacingOverride,
            HeroStyleOverride = useDefaults ? string.Empty : website.HeroStyleOverride,
            HeaderSettingsJson = string.IsNullOrWhiteSpace(website.HeaderSettingsJson) ? "{}" : website.HeaderSettingsJson,
            FooterSettingsJson = string.IsNullOrWhiteSpace(website.FooterSettingsJson) ? "{}" : website.FooterSettingsJson,
            IsPublished = website.IsPublished,
            CreatedAt = website.CreatedAt,
            UpdatedAt = website.UpdatedAt,
            AgentUser = website.AgentUser,
            Template = template
        };
        ViewBag.IsTemplatePreview = true;

        return View("~/Views/PublicWebsite/Index.cshtml", new IPRO.Web.Models.PublicWebsiteViewModel
        {
            Website = previewWebsite,
            Pages = pages,
            CurrentPage = pages.FirstOrDefault(p => p.IsHomePage) ?? pages.FirstOrDefault()
        });
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

        if (await _db.AgentDomains.AnyAsync(d => d.DomainName == normalized))
        {
            TempData["Error"] = "That custom domain is already connected to an IPRO account.";
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
    public async Task<IActionResult> RemoveDomain(int id)
    {
        var domain = await _db.AgentDomains.FirstOrDefaultAsync(d => d.Id == id && d.AgentUserId == AgentId);
        if (domain == null)
        {
            TempData["Error"] = "That domain could not be found.";
            return RedirectToAction(nameof(Index));
        }

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

        TempData["Success"] = $"{domain.DomainName} was removed.";
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
}
