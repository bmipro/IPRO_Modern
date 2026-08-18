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

    public ArticlesController(IPRODbContext db, IPackageEntitlementService entitlements, IBlobStorageService blob)
    {
        _db = db;
        _entitlements = entitlements;
        _blob = blob;
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
    public async Task<IActionResult> Edit(Article model, IFormFile? image)
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
        if (image != null && image.Length > 0)
        {
            var uploadResult = await ValidateAndUploadImageAsync(image);
            if (uploadResult.Error != null)
            {
                TempData["Error"] = uploadResult.Error;
                return View(model);
            }
            replacedImageUrl = existing.ImageUrl;
            existing.ImageUrl = uploadResult.Url ?? string.Empty;
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
