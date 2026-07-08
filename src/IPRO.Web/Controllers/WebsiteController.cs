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
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public WebsiteController(IWebsiteService websites, IBlobStorageService blob, IPackageEntitlementService entitlements) { _websites = websites; _blob = blob; _entitlements = entitlements; }

    public async Task<IActionResult> Index() { var gate = await RequireWebsiteAccessAsync(); if (gate != null) return gate; ViewBag.Templates = await _websites.GetTemplatesAsync(); return View(await _websites.GetByAgentIdAsync(AgentId)); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(AgentWebsite model, IFormFile? logo)
    {
        var gate = await RequireWebsiteAccessAsync();
        if (gate != null) return gate;
        var existing = await _websites.GetByAgentIdAsync(AgentId);
        if (logo != null && logo.Length > 0) { using var s = logo.OpenReadStream(); model.LogoUrl = await _blob.UploadAsync(s, logo.FileName, "agent-logos", logo.ContentType); }
        if (existing == null) { model.AgentUserId = AgentId; await _websites.CreateAsync(model); }
        else { existing.SiteTitle = model.SiteTitle; existing.TagLine = model.TagLine; existing.ThemeColor = model.ThemeColor; existing.TemplateId = model.TemplateId; existing.CustomDomain = model.CustomDomain; if (!string.IsNullOrEmpty(model.LogoUrl)) existing.LogoUrl = model.LogoUrl; await _websites.UpdateAsync(existing); }
        TempData["Success"] = "Website settings saved!";
        return RedirectToAction(nameof(Index));
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish() { var gate = await RequireWebsiteAccessAsync(); if (gate != null) return gate; await _websites.PublishAsync(AgentId); TempData["Success"] = "Your website is now live!"; return RedirectToAction(nameof(Index)); }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Unpublish() { var gate = await RequireWebsiteAccessAsync(); if (gate != null) return gate; await _websites.UnpublishAsync(AgentId); TempData["Warning"] = "Website taken offline."; return RedirectToAction(nameof(Index)); }

    private async Task<IActionResult?> RequireWebsiteAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.InstantWebsite);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Index", "Billing");
    }
}
