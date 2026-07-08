using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Web.Controllers;

[Authorize]
public class NewsletterController : Controller
{
    private readonly INewsLetterService _newsletters;
    private readonly IClientService _clients;
    private readonly IPackageEntitlementService _entitlements;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public NewsletterController(INewsLetterService newsletters, IClientService clients, IPackageEntitlementService entitlements) { _newsletters = newsletters; _clients = clients; _entitlements = entitlements; }

    public async Task<IActionResult> Index() { var gate = await RequireNewsletterAccessAsync(); return gate ?? View(await _newsletters.GetByAgentAsync(AgentId)); }
    public async Task<IActionResult> Create() { var gate = await RequireNewsletterAccessAsync(); return gate ?? View(new NewsLetter()); }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(NewsLetter model) { var gate = await RequireNewsletterAccessAsync(); if (gate != null) return gate; if (!ModelState.IsValid) return View(model); model.AgentUserId = AgentId; var nl = await _newsletters.CreateAsync(model); return RedirectToAction(nameof(Edit), new { id = nl.Id }); }
    public async Task<IActionResult> Edit(int id) { var gate = await RequireNewsletterAccessAsync(); if (gate != null) return gate; var nl = await _newsletters.GetByIdAsync(id); if (nl == null || nl.AgentUserId != AgentId) return NotFound(); ViewBag.Articles = await _newsletters.GetArticlesAsync(id); return View(nl); }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(NewsLetter model) { var gate = await RequireNewsletterAccessAsync(); if (gate != null) return gate; if (!ModelState.IsValid) return View(model); await _newsletters.UpdateAsync(model); return RedirectToAction(nameof(Index)); }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Schedule(int id, DateTime scheduledAt) { var gate = await RequireNewsletterAccessAsync(); if (gate != null) return gate; await _newsletters.ScheduleAsync(id, scheduledAt); TempData["Success"] = "Newsletter scheduled!"; return RedirectToAction(nameof(Index)); }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddArticle(NewsLetterArticle article) { var gate = await RequireNewsletterAccessAsync(); if (gate != null) return gate; await _newsletters.AddArticleAsync(article); return RedirectToAction(nameof(Edit), new { id = article.NewsLetterId }); }
    public async Task<IActionResult> Subscribers() { var gate = await RequireNewsletterAccessAsync(); return gate ?? View(await _clients.GetNewsletterSubscribersAsync(AgentId)); }

    private async Task<IActionResult?> RequireNewsletterAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.Newsletters);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Index", "Billing");
    }
}
