using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class ArticlesController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IBlobStorageService _blob;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private readonly IPRO.Business.Interfaces.IAiSuggestionService _aiSuggestions;

    public ArticlesController(IPRODbContext db, IPackageEntitlementService entitlements, IBlobStorageService blob, IPRO.Business.Interfaces.IAiSuggestionService aiSuggestions)
    {
        _db = db;
        _entitlements = entitlements;
        _blob = blob;
        _aiSuggestions = aiSuggestions;
    }

    // "Draft with AI" on the article editor. Deliberately mirrors NewsletterController.DraftWithAi:
    // gated by AiDailyAssistant (the ONE shared AI flag -- never per-feature), usage recorded the
    // same way, and the result FILLS THE FORM for the agent to review and edit. It never saves and
    // never publishes: the authors are regulated advisers, so the author of record stays human.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DraftWithAi(string topic)
    {
        var access = await _entitlements.GetAccessAsync(AgentId, IPRO.Entities.PackageFeatureCodes.AiDailyAssistant);
        if (!access.IsIncluded)
        {
            return Json(new { success = false, error = access.UpgradeMessage });
        }

        if (string.IsNullOrWhiteSpace(topic))
        {
            return Json(new { success = false, error = "Enter a topic first, then draft with AI." });
        }

        var result = await _aiSuggestions.DraftBlogPostAsync(topic.Trim());
        if (result.InputTokens > 0 || result.OutputTokens > 0)
        {
            await IPRO.Business.Services.AiUsageRecorder.RecordAsync(_db, 1, result.InputTokens, result.OutputTokens);
            await _db.SaveChangesAsync();
        }

        if (string.IsNullOrWhiteSpace(result.BodyHtml))
        {
            return Json(new { success = false, error = "AI drafting isn't available right now — try again in a moment, or write the article yourself." });
        }

        return Json(new { success = true, title = result.Title ?? "", summary = result.Summary ?? "", body = result.BodyHtml });
    }

    public async Task<IActionResult> Index()
    {
        var gate = await RequireArticlesAccessAsync();
        if (gate != null) return gate;

        var articles = await _db.Articles
            .Where(a => a.AgentUserId == AgentId)
            .OrderByDescending(a => a.UpdatedAt)
            .ToListAsync();
        return View(articles);
    }

    public async Task<IActionResult> Create()
    {
        var gate = await RequireArticlesAccessAsync();
        if (gate != null) return gate;

        ViewBag.AiAccess = await _entitlements.GetAccessAsync(AgentId, IPRO.Entities.PackageFeatureCodes.AiDailyAssistant);
        return View(new Article());
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> Create(Article model, IFormFile? image)
    {
        var gate = await RequireArticlesAccessAsync();
        if (gate != null) return gate;

        if (string.IsNullOrWhiteSpace(model.Title))
        {
            ModelState.AddModelError(nameof(Article.Title), "Title is required.");
        }
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var imageUrl = string.Empty;
        if (image != null && image.Length > 0)
        {
            // M9: articles were the ONE upload path that never consulted the shared
            // FileUploadCapacity pool -- documents, gallery photos and portal uploads all check;
            // article images counted AGAINST the pool (AgentStorageUsage sums ImageSizeBytes) but
            // were accepted unconditionally, so an agent at their limit could keep uploading
            // through this door while every other door refused them.
            var quotaError = await CheckStorageQuotaAsync(image.Length, replacedBytes: 0);
            if (quotaError != null)
            {
                TempData["Error"] = quotaError;
                return View(model);
            }
            var uploadResult = await ValidateAndUploadImageAsync(image);
            if (uploadResult.Error != null)
            {
                TempData["Error"] = uploadResult.Error;
                return View(model);
            }
            imageUrl = uploadResult.Url ?? string.Empty;
        }

        var now = DateTime.UtcNow;
        var article = new Article
        {
            AgentUserId = AgentId,
            Title = model.Title.Trim(),
            Summary = (model.Summary ?? string.Empty).Trim(),
            Content = HtmlContentSanitizer.Sanitize((model.Content ?? string.Empty).Trim()),
            ImageUrl = imageUrl,
            ImageSizeBytes = image?.Length ?? 0,
            IsPublished = model.IsPublished,
            PublishedAt = model.IsPublished ? now : null,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Articles.Add(article);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Article created.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var gate = await RequireArticlesAccessAsync();
        if (gate != null) return gate;

        var article = await _db.Articles.FirstOrDefaultAsync(a => a.Id == id && a.AgentUserId == AgentId);
        if (article == null) return NotFound();
        return View(article);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> Edit(Article model, IFormFile? image, bool removeImage = false)
    {
        var gate = await RequireArticlesAccessAsync();
        if (gate != null) return gate;

        var existing = await _db.Articles.FirstOrDefaultAsync(a => a.Id == model.Id && a.AgentUserId == AgentId);
        if (existing == null) return NotFound();

        if (string.IsNullOrWhiteSpace(model.Title))
        {
            ModelState.AddModelError(nameof(Article.Title), "Title is required.");
        }
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        string? replacedImageUrl = null;
        // Owner-found 2026-08-28: there was no way to take a cover OFF an article -- only replace
        // it or delete the whole article. Removal clears the pointer and the quota bytes; the
        // blob itself goes through the same shared-reference guard as a replacement, so starter
        // artwork other agents still point at is never destroyed. A new upload in the same save
        // wins over the tick -- replacing IS removing plus adding.
        if (removeImage && (image == null || image.Length == 0) && !string.IsNullOrWhiteSpace(existing.ImageUrl))
        {
            replacedImageUrl = existing.ImageUrl;
            existing.ImageUrl = string.Empty;
            existing.ImageSizeBytes = 0;
        }
        if (image != null && image.Length > 0)
        {
            // M9, replacement flavour: the outgoing image's bytes leave the pool as the new ones
            // arrive, so the check is against the NET change -- replacing a 5 MB image with a
            // 2 MB one must succeed even at the limit.
            var quotaError = await CheckStorageQuotaAsync(image.Length, replacedBytes: existing.ImageSizeBytes);
            if (quotaError != null)
            {
                TempData["Error"] = quotaError;
                return View(model);
            }
            var uploadResult = await ValidateAndUploadImageAsync(image);
            if (uploadResult.Error != null)
            {
                TempData["Error"] = uploadResult.Error;
                return View(model);
            }
            replacedImageUrl = existing.ImageUrl;
            existing.ImageUrl = uploadResult.Url ?? string.Empty;
            existing.ImageSizeBytes = image?.Length ?? 0;
        }

        existing.Title = model.Title.Trim();
        existing.Summary = (model.Summary ?? string.Empty).Trim();
        existing.Content = HtmlContentSanitizer.Sanitize((model.Content ?? string.Empty).Trim());
        if (model.IsPublished && !existing.IsPublished)
        {
            existing.PublishedAt = DateTime.UtcNow;
        }
        existing.IsPublished = model.IsPublished;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await DeleteImageIfNotSharedAsync(replacedImageUrl);
        TempData["Success"] = "Article updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var gate = await RequireArticlesAccessAsync();
        if (gate != null) return gate;

        var article = await _db.Articles.FirstOrDefaultAsync(a => a.Id == id && a.AgentUserId == AgentId);
        if (article != null)
        {
            var imageUrl = article.ImageUrl;
            _db.Articles.Remove(article);
            await _db.SaveChangesAsync();
            await DeleteImageIfNotSharedAsync(imageUrl);
            TempData["Success"] = "Article deleted.";
        }
        return RedirectToAction(nameof(Index));
    }

    // Article images were never cleaned up -- not on replace, not on delete -- so every edit leaked a
    // file that nothing referenced again.
    //
    // The guard matters: an agent's ImageUrl is usually their own upload, but starter provisioning
    // copies WebsiteStarterArticle.ImageUrl (a URL a Super Admin types on the Starter Articles screen,
    // and can therefore point anywhere) verbatim into every agent's Article. One agent deleting their
    // copy must not destroy artwork the starter library and every other agent still point at.
    private async Task DeleteImageIfNotSharedAsync(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)) return;
        // BlobReferences covers what the two ad-hoc checks that used to sit here covered (starter
        // library, other articles) PLUS the references they missed (A5-H14): newsletter and drip
        // HTML that copied this image by URL. Deleting a file a sent newsletter embeds blanks the
        // image in mail already delivered, with no undo — so anything still referenced is kept.
        if (await IPRO.DataAccess.BlobReferences.IsReferencedAsync(_db, imageUrl)) return;

        // Best effort, same as the agent-photo replacement in AccountController: the article change the
        // agent asked for has already been saved, so a storage hiccup must not fail their request.
        try { await _blob.DeleteAsync(imageUrl); } catch { }
    }

    // Mirrors DocumentsController's check against the same shared pool; a null return means the
    // upload fits. limitBytes <= 0 (an explicit 0 limit) keeps its pre-existing "no quota" meaning.
    private async Task<string?> CheckStorageQuotaAsync(long incomingBytes, long replacedBytes)
    {
        var access = await _entitlements.GetAccessAsync(AgentId, IPRO.Entities.PackageFeatureCodes.FileUploadCapacity);
        var limitBytes = IPRO.Web.Infrastructure.AgentStorageUsage.LimitBytes(access.LimitValue);
        if (limitBytes <= 0) return null;
        var usedBytes = await IPRO.Web.Infrastructure.AgentStorageUsage.TotalBytesAsync(_db, AgentId);
        if (usedBytes - replacedBytes + incomingBytes <= limitBytes) return null;
        return $"That image would exceed your storage limit " +
               $"({IPRO.Web.Infrastructure.AgentStorageUsage.ToMb(usedBytes)} MB of " +
               $"{IPRO.Web.Infrastructure.AgentStorageUsage.DisplayLimitMb(access.LimitValue)} MB used, counting documents and website photos). " +
               "Delete unused documents or gallery photos to free up space, or contact us to increase your storage.";
    }

    private async Task<(string? Url, string? Error)> ValidateAndUploadImageAsync(IFormFile image)
    {
        if (image.Length > 8 * 1024 * 1024)
        {
            return (null, "Images must be 8 MB or smaller.");
        }

        var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
        var expectedContentType = extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => string.Empty
        };
        if (string.IsNullOrEmpty(expectedContentType) ||
            !string.Equals(image.ContentType, expectedContentType, StringComparison.OrdinalIgnoreCase))
        {
            return (null, "Only JPG, JPEG, PNG, GIF, and WebP image files are allowed.");
        }

        await using var stream = image.OpenReadStream();
        if (!await HasValidImageSignatureAsync(stream, extension))
        {
            return (null, "That file does not contain a valid supported image.");
        }
        stream.Position = 0;
        var url = await _blob.UploadAsync(stream, image.FileName, "article-media", expectedContentType, isPrivate: false);
        return (url, null);
        // (size is read from IFormFile.Length at the call sites -- see ImageSizeBytes assignments)
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

    private async Task<IActionResult?> RequireArticlesAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.Newsletters);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Index", "Billing");
    }
}
