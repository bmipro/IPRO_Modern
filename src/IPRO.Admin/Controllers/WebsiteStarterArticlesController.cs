using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Admin.Controllers;

// SuperAdmin management for the Resources starter article library (WebsiteStarterArticleSeeder's
// content used to be the only way to change this -- a code edit and a redeploy for every wording
// tweak). Modelled directly on StarterContentController/NewsletterTemplatesController: no file
// upload, no live-reference risk on delete -- each article is copied into a real, independent
// Article row the moment it's provisioned to an agent, so removing or editing the master row here
// never touches an agent's already-provisioned copy.
[Authorize(Policy = "AdminAccess")]
public class WebsiteStarterArticlesController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IAdminAuditLogService _auditLog;

    public WebsiteStarterArticlesController(IPRODbContext db, IAdminAuditLogService auditLog)
    {
        _db = db;
        _auditLog = auditLog;
    }

    private int CurrentAdminId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    private string CurrentAdminUsername => User.Identity?.Name ?? "unknown";

    public async Task<IActionResult> Index()
    {
        var articles = await _db.WebsiteStarterArticles.AsNoTracking()
            .OrderBy(a => a.BusinessType).ThenBy(a => a.Category).ThenBy(a => a.SortOrder)
            .ToListAsync();
        return View(articles);
    }

    public IActionResult Create() => View("Edit", new WebsiteStarterArticle { BusinessType = "All", IsActive = true });

    public async Task<IActionResult> Edit(int id)
    {
        var article = await _db.WebsiteStarterArticles.FirstOrDefaultAsync(a => a.Id == id);
        if (article == null) return NotFound();
        return View(article);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(WebsiteStarterArticle model)
    {
        model.BusinessType = string.IsNullOrWhiteSpace(model.BusinessType) ? "All" : model.BusinessType.Trim();
        model.Category = string.IsNullOrWhiteSpace(model.Category) ? null : model.Category.Trim();
        model.Title = model.Title?.Trim() ?? string.Empty;
        model.Summary = model.Summary?.Trim() ?? string.Empty;
        // Renders as @Html.Raw on every public site that uses it -- same reason Newsletter and Drip
        // Campaign bodies are sanitized (see DOCS/09_TROUBLESHOOTING.md, H-3/H-4).
        model.Content = HtmlContentSanitizer.Sanitize(model.Content?.Trim());
        model.ImageUrl = model.ImageUrl?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(model.Title))
            ModelState.AddModelError(nameof(model.Title), "Title is required.");
        if (string.IsNullOrWhiteSpace(model.Content))
            ModelState.AddModelError(nameof(model.Content), "Content is required.");

        if (!ModelState.IsValid) return View(model);

        var isNew = model.Id == 0;
        if (isNew)
        {
            model.CreatedAt = DateTime.UtcNow;
            model.UpdatedAt = DateTime.UtcNow;
            _db.WebsiteStarterArticles.Add(model);
        }
        else
        {
            var existing = await _db.WebsiteStarterArticles.FirstOrDefaultAsync(a => a.Id == model.Id);
            if (existing == null) return NotFound();

            existing.BusinessType = model.BusinessType;
            existing.Category = model.Category;
            existing.Title = model.Title;
            existing.Summary = model.Summary;
            existing.Content = model.Content;
            existing.ImageUrl = model.ImageUrl;
            existing.IsActive = model.IsActive;
            existing.SortOrder = model.SortOrder;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername,
            isNew ? "StarterArticleCreate" : "StarterArticleEdit",
            $"Starter article '{model.Title}' ({model.BusinessType}) {(isNew ? "created" : "updated")}");
        TempData["Success"] = "Starter article saved. New signups for this vertical will see it; agents already provisioned keep what they have.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Deactivate(int id) => await SetActiveAsync(id, false);

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(int id) => await SetActiveAsync(id, true);

    private async Task<IActionResult> SetActiveAsync(int id, bool active)
    {
        var article = await _db.WebsiteStarterArticles.FirstOrDefaultAsync(a => a.Id == id);
        if (article == null) return NotFound();
        article.IsActive = active;
        article.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername,
            active ? "StarterArticleRestore" : "StarterArticleDeactivate",
            $"Starter article '{article.Title}' {(active ? "restored" : "deactivated")}");
        TempData["Success"] = active
            ? $"{article.Title} will be offered to new signups again."
            : $"{article.Title} is no longer offered to new signups. Agents who already have it keep their copy.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var article = await _db.WebsiteStarterArticles.FirstOrDefaultAsync(a => a.Id == id);
        if (article == null) return NotFound();
        _db.WebsiteStarterArticles.Remove(article);
        await _db.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "StarterArticleDelete",
            $"Starter article '{article.Title}' ({article.BusinessType}) permanently deleted");
        TempData["Success"] = $"{article.Title} was deleted. Agents who already have a copy are unaffected.";
        return RedirectToAction(nameof(Index));
    }
}
