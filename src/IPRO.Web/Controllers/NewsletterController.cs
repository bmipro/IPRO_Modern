using System.Security.Claims;
using System.Net;
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
    public async Task<IActionResult> Create()
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;

        await LoadNewsletterContextAsync();
        return View(new NewsLetter());
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(NewsLetter model)
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;

        PrepareNewsletterForValidation(model);
        ValidateNewsletter(model);
        if (!ModelState.IsValid)
        {
            await LoadNewsletterContextAsync();
            return View(model);
        }

        var nl = await _newsletters.CreateAsync(model);
        TempData["Success"] = "Newsletter draft saved.";
        return RedirectToAction(nameof(Preview), new { id = nl.Id });
    }
    public async Task<IActionResult> Edit(int id)
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;

        var nl = await _newsletters.GetByIdAsync(id);
        if (nl == null || nl.AgentUserId != AgentId) return NotFound();

        await LoadNewsletterContextAsync();
        ViewBag.Articles = await _newsletters.GetArticlesAsync(id);
        return View(nl);
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(NewsLetter model)
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;

        var existing = await _newsletters.GetByIdAsync(model.Id);
        if (existing == null || existing.AgentUserId != AgentId) return NotFound();

        PrepareNewsletterForValidation(model);
        ValidateNewsletter(model);
        if (!ModelState.IsValid)
        {
            await LoadNewsletterContextAsync();
            ViewBag.Articles = await _newsletters.GetArticlesAsync(model.Id);
            return View(model);
        }

        existing.Subject = model.Subject;
        existing.HtmlBody = model.HtmlBody;
        existing.TextBody = model.TextBody;
        await _newsletters.UpdateAsync(existing);
        TempData["Success"] = "Newsletter updated.";
        return RedirectToAction(nameof(Preview), new { id = existing.Id });
    }
    public async Task<IActionResult> Preview(int id)
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;

        var nl = await _newsletters.GetByIdAsync(id);
        if (nl == null || nl.AgentUserId != AgentId) return NotFound();

        await LoadNewsletterContextAsync();
        ViewBag.Articles = await _newsletters.GetArticlesAsync(id);
        return View(nl);
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Schedule(int id, DateTime scheduledAt) { var gate = await RequireNewsletterAccessAsync(); if (gate != null) return gate; await _newsletters.ScheduleAsync(id, scheduledAt); TempData["Success"] = "Newsletter scheduled!"; return RedirectToAction(nameof(Index)); }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddArticle(NewsLetterArticle article) { var gate = await RequireNewsletterAccessAsync(); if (gate != null) return gate; await _newsletters.AddArticleAsync(article); return RedirectToAction(nameof(Edit), new { id = article.NewsLetterId }); }
    public async Task<IActionResult> Subscribers() { var gate = await RequireNewsletterAccessAsync(); return gate ?? View(await _clients.GetNewsletterSubscribersAsync(AgentId)); }

    private async Task LoadNewsletterContextAsync()
    {
        var subscribers = await _clients.GetNewsletterSubscribersAsync(AgentId);
        ViewBag.SubscriberCount = subscribers.Count();
    }

    private async Task<IActionResult?> RequireNewsletterAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.Newsletters);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Index", "Billing");
    }

    private void PrepareNewsletterForValidation(NewsLetter model)
    {
        model.AgentUserId = AgentId;
        model.Subject = model.Subject?.Trim() ?? "";
        model.HtmlBody = model.HtmlBody?.Trim() ?? "";
        model.TextBody = model.TextBody?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(model.HtmlBody) && !string.IsNullOrWhiteSpace(model.TextBody))
        {
            model.HtmlBody = ConvertPlainTextToHtml(model.TextBody);
        }

        foreach (var key in new[]
        {
            nameof(NewsLetter.HtmlBody),
            nameof(NewsLetter.TextBody),
            nameof(NewsLetter.AgentUser),
            nameof(NewsLetter.Articles),
            nameof(NewsLetter.AgentUserId),
            nameof(NewsLetter.Status),
            nameof(NewsLetter.ScheduledAt),
            nameof(NewsLetter.SentAt)
        })
        {
            ModelState.Remove(key);
        }
    }

    private void ValidateNewsletter(NewsLetter model)
    {
        if (string.IsNullOrWhiteSpace(model.Subject))
        {
            ModelState.AddModelError(nameof(NewsLetter.Subject), "Subject line is required.");
        }

        if (string.IsNullOrWhiteSpace(model.HtmlBody) && string.IsNullOrWhiteSpace(model.TextBody))
        {
            ModelState.AddModelError(nameof(NewsLetter.HtmlBody), "Newsletter body or plain text version is required.");
        }
    }

    private static string ConvertPlainTextToHtml(string text)
    {
        var paragraphs = text
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.TrimEntries);

        return string.Join(
            Environment.NewLine,
            paragraphs.Select(line =>
                string.IsNullOrWhiteSpace(line)
                    ? "<br>"
                    : $"<p>{WebUtility.HtmlEncode(line)}</p>"));
    }
}
