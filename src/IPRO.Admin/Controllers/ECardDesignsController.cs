using System.Security.Claims;
using System.Text.RegularExpressions;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IPRO.Admin.Controllers;

// SuperAdmin management for the e-card artwork library. Modelled on NewsletterTemplatesController
// -- same list/edit/deactivate/restore shape -- with artwork upload on top.
//
// Deliberately no hard Delete action, unlike Newsletter Templates: every sent e-card resolves its
// thumbnail and layout through the design Key, so deleting one would blank the artwork on all of
// that agent's history. Deactivate is the only way to take a design out of circulation.
[Authorize(Policy = "SuperAdmin")]
public class ECardDesignsController : Controller
{
    private const string ArtworkContainer = "ecard-art";
    private const long MaxArtworkBytes = 8 * 1024 * 1024;

    private readonly IPRODbContext _db;
    private readonly IServiceProvider _services;
    private readonly IAdminAuditLogService _auditLog;
    private readonly IConfiguration _configuration;

    // Blob storage is resolved lazily rather than injected. Its client throws on construction when
    // the storage connection string isn't configured, and that would take down this whole screen --
    // including listing and retiring designs, which need no storage at all. Resolving it only on
    // the upload path keeps the page usable and turns a misconfiguration into a clear message.
    public ECardDesignsController(IPRODbContext db, IServiceProvider services,
        IAdminAuditLogService auditLog, IConfiguration configuration)
    {
        _db = db;
        _services = services;
        _auditLog = auditLog;
        _configuration = configuration;
    }

    // Seeded artwork lives in IPRO.Web's wwwroot, so admin thumbnails have to point at the web
    // app's host. Resolving against admin's own host is what left every thumbnail blank.
    private string WebBaseUrl => WebAppUrlHelper.GetWebAppBaseUrl(_configuration);

    private int CurrentAdminId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    private string CurrentAdminUsername => User.Identity?.Name ?? "unknown";

    public async Task<IActionResult> Index()
    {
        var designs = await _db.ECardDesigns.AsNoTracking()
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Id)
            .ToListAsync();
        ViewBag.WebBaseUrl = WebBaseUrl;
        ViewBag.ActiveCount = designs.Count(d => d.IsActive);
        return View(designs);
    }

    public IActionResult Create()
    {
        ViewBag.WebBaseUrl = WebBaseUrl;
        return View("Edit", new ECardDesign { IsActive = true, Kind = ECardArtKinds.Image, IsDark = true, SortOrder = 100 });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var design = await _db.ECardDesigns.FirstOrDefaultAsync(d => d.Id == id);
        if (design == null) return NotFound();
        ViewBag.WebBaseUrl = WebBaseUrl;
        return View(design);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<IActionResult> Edit(ECardDesign model, IFormFile? artwork)
    {
        model.Name = model.Name?.Trim() ?? string.Empty;
        model.Occasion = model.Occasion?.Trim() ?? string.Empty;
        model.DefaultHeaderText = model.DefaultHeaderText?.Trim() ?? string.Empty;
        model.DefaultMessage = model.DefaultMessage?.Trim() ?? string.Empty;
        model.Accent = model.Accent?.Trim() ?? string.Empty;
        model.Emoji = model.Emoji?.Trim() ?? string.Empty;
        model.Key = NormaliseKey(model.Key, model.Occasion, model.Name);
        if (!ECardArtKinds.All.Contains(model.Kind)) model.Kind = ECardArtKinds.Image;

        if (string.IsNullOrWhiteSpace(model.Name))
            ModelState.AddModelError(nameof(model.Name), "Design name is required.");
        if (string.IsNullOrWhiteSpace(model.Occasion))
            ModelState.AddModelError(nameof(model.Occasion), "Occasion is required — it groups the design in the agent's picker.");
        if (string.IsNullOrWhiteSpace(model.Key))
            ModelState.AddModelError(nameof(model.Key), "Key is required.");

        var isNew = model.Id == 0;

        // The key is the join between a sent e-card and its artwork, so a collision would make an
        // old card start rendering someone else's design.
        var keyTaken = await _db.ECardDesigns.AnyAsync(d => d.Key == model.Key && d.Id != model.Id);
        if (keyTaken)
            ModelState.AddModelError(nameof(model.Key), "Another design already uses that key.");

        if (model.Kind == ECardArtKinds.Generated)
        {
            if (!Regex.IsMatch(model.Accent, "^#[0-9a-fA-F]{6}$"))
                ModelState.AddModelError(nameof(model.Accent), "Enter a colour as a six-digit hex value, e.g. #1e3a8a.");
            if (string.IsNullOrWhiteSpace(model.Emoji))
                ModelState.AddModelError(nameof(model.Emoji), "Pick an emoji for the card face.");
        }

        string? uploadedUrl = null;
        if (artwork is { Length: > 0 })
        {
            var (ok, error, url) = await TryUploadArtworkAsync(artwork);
            if (!ok) ModelState.AddModelError("artwork", error!);
            else uploadedUrl = url;
        }
        else if (model.Kind == ECardArtKinds.Image && string.IsNullOrWhiteSpace(model.ImageUrl))
        {
            ModelState.AddModelError("artwork", "Upload the card artwork.");
        }

        if (!ModelState.IsValid) { ViewBag.WebBaseUrl = WebBaseUrl; return View(model); }

        if (uploadedUrl != null) model.ImageUrl = uploadedUrl;

        if (isNew)
        {
            model.CreatedAt = DateTime.UtcNow;
            model.UpdatedAt = DateTime.UtcNow;
            _db.ECardDesigns.Add(model);
        }
        else
        {
            var existing = await _db.ECardDesigns.FirstOrDefaultAsync(d => d.Id == model.Id);
            if (existing == null) return NotFound();

            existing.Key = model.Key;
            existing.Occasion = model.Occasion;
            existing.Name = model.Name;
            existing.Kind = model.Kind;
            existing.DefaultHeaderText = model.DefaultHeaderText;
            existing.DefaultMessage = model.DefaultMessage;
            existing.Accent = model.Accent;
            existing.Emoji = model.Emoji;
            existing.IsDark = model.IsDark;
            existing.IsActive = model.IsActive;
            existing.SortOrder = model.SortOrder;
            if (uploadedUrl != null)
            {
                existing.ImageUrl = uploadedUrl;
                existing.Width = model.Width;
                existing.Height = model.Height;
            }
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername,
            isNew ? "ECardDesignCreate" : "ECardDesignEdit",
            $"E-card design '{model.Name}' ({model.Key}) {(isNew ? "created" : "updated")}");
        TempData["Success"] = "E-card design saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Deactivate(int id) => await SetActiveAsync(id, false);

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(int id) => await SetActiveAsync(id, true);

    private async Task<IActionResult> SetActiveAsync(int id, bool active)
    {
        var design = await _db.ECardDesigns.FirstOrDefaultAsync(d => d.Id == id);
        if (design == null) return NotFound();

        design.IsActive = active;
        design.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername,
            active ? "ECardDesignRestore" : "ECardDesignDeactivate",
            $"E-card design '{design.Name}' {(active ? "restored" : "deactivated")}");
        TempData["Success"] = active
            ? $"{design.Name} is available to agents again."
            : $"{design.Name} is no longer offered to agents. Cards already sent with it are unaffected.";
        return RedirectToAction(nameof(Index));
    }

    // Same validation the agent-facing logo and gallery uploads use: extension, declared content
    // type, and the file's actual magic bytes must all agree before anything reaches storage.
    private async Task<(bool Ok, string? Error, string? Url)> TryUploadArtworkAsync(IFormFile file)
    {
        if (file.Length > MaxArtworkBytes)
            return (false, "Artwork must be 8 MB or smaller.", null);

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var expectedContentType = extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => string.Empty
        };
        if (string.IsNullOrEmpty(expectedContentType) ||
            !string.Equals(file.ContentType, expectedContentType, StringComparison.OrdinalIgnoreCase))
            return (false, "Only JPG, JPEG, PNG, GIF and WebP images are allowed.", null);

        await using var stream = file.OpenReadStream();
        if (!await HasValidImageSignatureAsync(stream, extension))
            return (false, "That file does not contain a valid image.", null);

        IBlobStorageService blobs;
        try
        {
            blobs = _services.GetRequiredService<IBlobStorageService>();
        }
        catch (Exception)
        {
            return (false, "Artwork storage isn't configured for the admin app. " +
                           "Set the Azure__StorageConnectionString and Azure__StorageAccountName " +
                           "app settings on ipro-prod-admin, then try again.", null);
        }

        stream.Position = 0;
        var url = await blobs.UploadAsync(stream, $"design{extension}", ArtworkContainer, expectedContentType, isPrivate: false);
        return (true, null, url);
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

    // Keys are URL-ish and stable. If the admin didn't type one, derive it from occasion + name.
    private static string NormaliseKey(string? key, string occasion, string name)
    {
        var source = string.IsNullOrWhiteSpace(key) ? $"{occasion} {name}" : key;
        var slug = Regex.Replace(source.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 80 ? slug[..80] : slug;
    }
}
