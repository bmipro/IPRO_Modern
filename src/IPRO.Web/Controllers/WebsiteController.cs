using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Web.Controllers;

[Authorize]
public class WebsiteController : Controller
{
    private readonly IWebsiteService _websites;
    private readonly IBlobStorageService _blob;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IAgentService _agents;
    private readonly IConfiguration _configuration;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public WebsiteController(IWebsiteService websites, IBlobStorageService blob, IPackageEntitlementService entitlements, IAgentService agents, IConfiguration configuration)
    {
        _websites = websites;
        _blob = blob;
        _entitlements = entitlements;
        _agents = agents;
        _configuration = configuration;
    }

    public async Task<IActionResult> Index()
    {
        var gate = await RequireWebsiteAccessAsync();
        if (gate != null) return gate;

        await LoadWebsiteContextAsync();
        return View(await _websites.GetByAgentIdAsync(AgentId));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(AgentWebsite model, IFormFile? logo)
    {
        var gate = await RequireWebsiteAccessAsync();
        if (gate != null) return gate;

        var templates = (await _websites.GetTemplatesAsync()).ToList();
        if (model.TemplateId <= 0 && templates.Any())
        {
            model.TemplateId = templates.First().Id;
        }
        else if (model.TemplateId <= 0)
        {
            model.TemplateId = (await _websites.EnsureDefaultTemplateAsync()).Id;
        }

        var existing = await _websites.GetByAgentIdAsync(AgentId);
        if (logo != null && logo.Length > 0)
        {
            using var s = logo.OpenReadStream();
            model.LogoUrl = await _blob.UploadAsync(s, logo.FileName, "agent-logos", logo.ContentType);
        }

        model.CustomDomain = NormalizeDomain(model.CustomDomain);
        model.SiteTitle = model.SiteTitle?.Trim() ?? string.Empty;
        model.TagLine = model.TagLine?.Trim() ?? string.Empty;
        model.ThemeColor = string.IsNullOrWhiteSpace(model.ThemeColor) ? "#1457d9" : model.ThemeColor.Trim();

        if (existing == null)
        {
            model.AgentUserId = AgentId;
            await _websites.CreateAsync(model);
        }
        else
        {
            existing.SiteTitle = model.SiteTitle;
            existing.TagLine = model.TagLine;
            existing.ThemeColor = model.ThemeColor;
            existing.TemplateId = model.TemplateId;
            existing.CustomDomain = model.CustomDomain;
            if (!string.IsNullOrEmpty(model.LogoUrl)) existing.LogoUrl = model.LogoUrl;
            await _websites.UpdateAsync(existing);
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
            var template = await _websites.EnsureDefaultTemplateAsync();
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

    private async Task<IActionResult?> RequireWebsiteAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.InstantWebsite);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Index", "Billing");
    }

    private async Task LoadWebsiteContextAsync()
    {
        var agent = await _agents.GetByIdAsync(AgentId);
        ViewBag.Templates = await _websites.GetTemplatesAsync();
        ViewBag.TemporaryDomain = agent?.DomainName ?? string.Empty;
        ViewBag.TemporaryRootDomain = _configuration["App:TemporarySiteRootDomain"] ?? "247advisers.com";
        ViewBag.WebsiteDnsTarget = _configuration["App:WebsiteDnsTarget"] ?? "ipro-prod-web.azurewebsites.net";
    }

    private static string NormalizeDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return string.Empty;

        var value = domain.Trim().ToLowerInvariant();
        value = value.Replace("https://", string.Empty).Replace("http://", string.Empty);
        var slashIndex = value.IndexOf('/');
        if (slashIndex >= 0)
        {
            value = value[..slashIndex];
        }

        return value.Trim().Trim('.');
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
