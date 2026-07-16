using System.Security.Claims;
using System.Text.RegularExpressions;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Models;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class WebsitePagesController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IBlobStorageService _blob;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public WebsitePagesController(IPRODbContext db, IPackageEntitlementService entitlements, IBlobStorageService blob)
    {
        _db = db;
        _entitlements = entitlements;
        _blob = blob;
    }

    public async Task<IActionResult> Index()
    {
        var access = await RequireWebsiteAccessAsync();
        if (access != null) return access;

        var website = await GetWebsiteAsync();
        if (website == null)
        {
            TempData["Error"] = "Save your website settings before managing pages.";
            return RedirectToAction("Index", "Website");
        }

        await EnsureStarterPagesAsync(website);
        var pages = await _db.WebsitePages
            .AsNoTracking()
            .Where(p => p.AgentWebsiteId == website.Id)
            .Include(p => p.Blocks)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Title)
            .ToListAsync();
        return View(pages);
    }

    public async Task<IActionResult> Create()
    {
        var website = await GetWebsiteAsync();
        if (website == null) return RedirectToAction("Index", "Website");
        var nextOrder = await _db.WebsitePages.CountAsync(p => p.AgentWebsiteId == website.Id);
        return View("Edit", new WebsitePageEditViewModel
        {
            Page = new WebsitePage { AgentWebsiteId = website.Id, IsPublished = false, ShowInNavigation = true, SortOrder = nextOrder },
            AvailableParents = await GetParentChoicesAsync(website.Id, 0),
            MediaAssets = await GetMediaAssetsAsync(website.Id)
        });
    }

    public async Task<IActionResult> Navigation()
    {
        var access = await RequireWebsiteAccessAsync();
        if (access != null) return access;
        var website = await GetWebsiteAsync();
        if (website == null) return RedirectToAction("Index", "Website");
        await EnsureStarterPagesAsync(website);
        return View(new WebsiteNavigationViewModel
        {
            Website = website,
            Header = WebsiteHeaderSettings.FromJson(website.HeaderSettingsJson),
            Pages = await _db.WebsitePages.AsNoTracking().Where(p => p.AgentWebsiteId == website.Id)
                .OrderBy(p => p.SortOrder).ThenBy(p => p.Title).ToListAsync()
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveHeader(string style, string logoPosition, string logoSize,
        bool sticky, bool showPhone, bool showEmail, string buttonText, string buttonUrl)
    {
        var website = await GetWebsiteAsync();
        if (website == null) return RedirectToAction("Index", "Website");
        var settings = WebsiteHeaderSettings.FromJson(website.HeaderSettingsJson);
        settings.Style = new[] { "standard", "compact", "transparent" }.Contains(style) ? style : "standard";
        settings.LogoPosition = logoPosition == "center" ? "center" : "left";
        settings.LogoSize = new[] { "small", "medium", "large" }.Contains(logoSize) ? logoSize : "medium";
        settings.Sticky = sticky;
        settings.ShowPhone = showPhone;
        settings.ShowEmail = showEmail;
        settings.ButtonText = buttonText?.Trim() ?? string.Empty;
        settings.ButtonUrl = NormalizeLink(buttonUrl);
        website.HeaderSettingsJson = settings.ToJson();
        website.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        TempData["Success"] = "Header settings saved.";
        return RedirectToAction(nameof(Navigation));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveNavigationItem(int id, string navigationLabel, int? parentPageId, bool showInNavigation)
    {
        var page = await OwnedPages().FirstOrDefaultAsync(p => p.Id == id);
        if (page == null) return NotFound();
        page.NavigationLabel = string.IsNullOrWhiteSpace(navigationLabel) ? page.Title : navigationLabel.Trim();
        page.ShowInNavigation = showInNavigation;
        page.ParentPageId = !page.IsHomePage && parentPageId.HasValue && parentPageId != page.Id &&
            await _db.WebsitePages.AnyAsync(p => p.Id == parentPageId && p.AgentWebsiteId == page.AgentWebsiteId && p.ParentPageId == null)
            ? parentPageId : null;
        page.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        TempData["Success"] = $"Navigation settings saved for {page.Title}.";
        return RedirectToAction(nameof(Navigation));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveNavigationItem(int id, string direction)
    {
        var page = await OwnedPages().FirstOrDefaultAsync(p => p.Id == id);
        if (page == null) return NotFound();
        var pages = await _db.WebsitePages.Where(p => p.AgentWebsiteId == page.AgentWebsiteId && p.ParentPageId == page.ParentPageId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync();
        MoveItem(pages, page, direction, p => p.SortOrder, (p, value) => p.SortOrder = value);
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Navigation));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCustomLink(string label, string url)
    {
        var website = await GetWebsiteAsync();
        if (website == null) return RedirectToAction("Index", "Website");
        var normalized = NormalizeUrl(url);
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(normalized))
        {
            TempData["Error"] = "Enter a label and a complete http or https URL.";
            return RedirectToAction(nameof(Navigation));
        }
        var settings = WebsiteHeaderSettings.FromJson(website.HeaderSettingsJson);
        settings.CustomLinks.Add(new WebsiteCustomNavigationLink { Label = label.Trim(), Url = normalized, SortOrder = settings.CustomLinks.Count });
        website.HeaderSettingsJson = settings.ToJson();
        await _db.SaveChangesAsync();
        TempData["Success"] = "Custom navigation link added.";
        return RedirectToAction(nameof(Navigation));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCustomLink(string id)
    {
        var website = await GetWebsiteAsync();
        if (website == null) return RedirectToAction("Index", "Website");
        var settings = WebsiteHeaderSettings.FromJson(website.HeaderSettingsJson);
        settings.CustomLinks.RemoveAll(link => link.Id == id);
        website.HeaderSettingsJson = settings.ToJson();
        await _db.SaveChangesAsync();
        TempData["Success"] = "Custom navigation link removed.";
        return RedirectToAction(nameof(Navigation));
    }

    public async Task<IActionResult> Footer()
    {
        var access = await RequireWebsiteAccessAsync();
        if (access != null) return access;
        var website = await GetWebsiteAsync();
        if (website == null) return RedirectToAction("Index", "Website");
        return View(new WebsiteFooterViewModel
        {
            Website = website,
            Footer = WebsiteFooterSettings.FromJson(website.FooterSettingsJson)
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveFooter(string copyrightText, string phone, string email, string address,
        bool showDisclaimer, string disclaimerText)
    {
        var website = await GetWebsiteAsync();
        if (website == null) return RedirectToAction("Index", "Website");
        var settings = WebsiteFooterSettings.FromJson(website.FooterSettingsJson);
        settings.CopyrightText = copyrightText?.Trim() ?? string.Empty;
        settings.Phone = phone?.Trim() ?? string.Empty;
        settings.Email = email?.Trim() ?? string.Empty;
        settings.Address = address?.Trim() ?? string.Empty;
        settings.ShowDisclaimer = showDisclaimer;
        settings.DisclaimerText = disclaimerText?.Trim() ?? string.Empty;
        website.FooterSettingsJson = settings.ToJson();
        website.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        TempData["Success"] = "Footer settings saved.";
        return RedirectToAction(nameof(Footer));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddSocialLink(string platform, string url)
    {
        var website = await GetWebsiteAsync();
        if (website == null) return RedirectToAction("Index", "Website");
        var normalized = NormalizeUrl(url);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            TempData["Error"] = "Enter a complete http or https URL.";
            return RedirectToAction(nameof(Footer));
        }
        var settings = WebsiteFooterSettings.FromJson(website.FooterSettingsJson);
        var normalizedPlatform = WebsiteFooterSettings.KnownPlatforms.Contains(platform, StringComparer.OrdinalIgnoreCase)
            ? platform.ToLowerInvariant()
            : "other";
        settings.SocialLinks.Add(new WebsiteSocialLink { Platform = normalizedPlatform, Url = normalized, SortOrder = settings.SocialLinks.Count });
        website.FooterSettingsJson = settings.ToJson();
        await _db.SaveChangesAsync();
        TempData["Success"] = "Social link added.";
        return RedirectToAction(nameof(Footer));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSocialLink(string id)
    {
        var website = await GetWebsiteAsync();
        if (website == null) return RedirectToAction("Index", "Website");
        var settings = WebsiteFooterSettings.FromJson(website.FooterSettingsJson);
        settings.SocialLinks.RemoveAll(link => link.Id == id);
        website.FooterSettingsJson = settings.ToJson();
        await _db.SaveChangesAsync();
        TempData["Success"] = "Social link removed.";
        return RedirectToAction(nameof(Footer));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddLegalLink(string label, string url)
    {
        var website = await GetWebsiteAsync();
        if (website == null) return RedirectToAction("Index", "Website");
        var normalized = NormalizeUrl(url);
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(normalized))
        {
            TempData["Error"] = "Enter a label and a complete http or https URL.";
            return RedirectToAction(nameof(Footer));
        }
        var settings = WebsiteFooterSettings.FromJson(website.FooterSettingsJson);
        settings.LegalLinks.Add(new WebsiteFooterLink { Label = label.Trim(), Url = normalized, SortOrder = settings.LegalLinks.Count });
        website.FooterSettingsJson = settings.ToJson();
        await _db.SaveChangesAsync();
        TempData["Success"] = "Legal link added.";
        return RedirectToAction(nameof(Footer));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteLegalLink(string id)
    {
        var website = await GetWebsiteAsync();
        if (website == null) return RedirectToAction("Index", "Website");
        var settings = WebsiteFooterSettings.FromJson(website.FooterSettingsJson);
        settings.LegalLinks.RemoveAll(link => link.Id == id);
        website.FooterSettingsJson = settings.ToJson();
        await _db.SaveChangesAsync();
        TempData["Success"] = "Legal link removed.";
        return RedirectToAction(nameof(Footer));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var page = await OwnedPages().Include(p => p.Blocks).FirstOrDefaultAsync(p => p.Id == id);
        if (page == null) return NotFound();
        page.Blocks = page.Blocks.OrderBy(b => b.SortOrder).ToList();
        return View(new WebsitePageEditViewModel
        {
            Page = page,
            AvailableParents = await GetParentChoicesAsync(page.AgentWebsiteId, page.Id),
            MediaAssets = await GetMediaAssetsAsync(page.AgentWebsiteId)
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> UploadImage(int pageId, IFormFile? image)
    {
        var page = await OwnedPages().FirstOrDefaultAsync(p => p.Id == pageId);
        if (page == null) return NotFound();
        if (image == null || image.Length == 0)
        {
            TempData["Error"] = "Choose an image to upload.";
            return RedirectToAction(nameof(Edit), new { id = pageId });
        }
        if (image.Length > 8 * 1024 * 1024)
        {
            TempData["Error"] = "Images must be 8 MB or smaller.";
            return RedirectToAction(nameof(Edit), new { id = pageId });
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
            TempData["Error"] = "Only JPG, JPEG, PNG, GIF, and WebP image files are allowed.";
            return RedirectToAction(nameof(Edit), new { id = pageId });
        }

        await using var stream = image.OpenReadStream();
        if (!await HasValidImageSignatureAsync(stream, extension))
        {
            TempData["Error"] = "That file does not contain a valid supported image.";
            return RedirectToAction(nameof(Edit), new { id = pageId });
        }
        stream.Position = 0;
        var url = await _blob.UploadAsync(stream, image.FileName, "website-media", expectedContentType);
        _db.WebsiteMediaAssets.Add(new WebsiteMediaAsset
        {
            AgentWebsiteId = page.AgentWebsiteId,
            OriginalFileName = Path.GetFileName(image.FileName),
            BlobUrl = url,
            ContentType = expectedContentType,
            FileSize = image.Length
        });
        await _db.SaveChangesAsync();
        TempData["Success"] = "Image uploaded and added to your media library.";
        return RedirectToAction(nameof(Edit), new { id = pageId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteImage(int id, int pageId)
    {
        var page = await OwnedPages().FirstOrDefaultAsync(p => p.Id == pageId);
        if (page == null) return NotFound();
        var asset = await _db.WebsiteMediaAssets.FirstOrDefaultAsync(a => a.Id == id && a.AgentWebsiteId == page.AgentWebsiteId);
        if (asset == null) return NotFound();
        var blocks = await _db.WebsiteContentBlocks
            .Where(b => b.WebsitePage.AgentWebsiteId == page.AgentWebsiteId && b.ImageUrl == asset.BlobUrl)
            .ToListAsync();
        foreach (var block in blocks) block.ImageUrl = string.Empty;
        await _blob.DeleteAsync(asset.BlobUrl);
        _db.WebsiteMediaAssets.Remove(asset);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Image removed from the media library.";
        return RedirectToAction(nameof(Edit), new { id = pageId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePage(int id, string title, string slug, string navigationLabel,
        string metaTitle, string metaDescription, int? parentPageId, bool showInNavigation, bool isPublished, bool isHomePage,
        string starterPreset = "blank")
    {
        var website = await GetWebsiteAsync();
        if (website == null) return RedirectToAction("Index", "Website");

        var page = id == 0 ? null : await OwnedPages().FirstOrDefaultAsync(p => p.Id == id);
        if (id != 0 && page == null) return NotFound();
        var isNewPage = page is null;
        starterPreset = WebsitePageStarterPresetCatalog.IsKnown(starterPreset) ? starterPreset : "blank";

        title = title?.Trim() ?? string.Empty;
        slug = NormalizeSlug(string.IsNullOrWhiteSpace(slug) ? title : slug);
        navigationLabel = string.IsNullOrWhiteSpace(navigationLabel) ? title : navigationLabel.Trim();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(slug))
        {
            TempData["Error"] = "Page title and URL slug are required.";
            return id == 0 ? RedirectToAction(nameof(Create)) : RedirectToAction(nameof(Edit), new { id });
        }

        if (isHomePage) slug = "home";
        if (await _db.WebsitePages.AnyAsync(p => p.AgentWebsiteId == website.Id && p.Slug == slug && p.Id != id))
        {
            TempData["Error"] = "Another page already uses that URL slug.";
            return id == 0 ? RedirectToAction(nameof(Create)) : RedirectToAction(nameof(Edit), new { id });
        }

        if (parentPageId.HasValue && !await _db.WebsitePages.AnyAsync(p => p.Id == parentPageId && p.AgentWebsiteId == website.Id && p.Id != id))
        {
            parentPageId = null;
        }

        if (page == null)
        {
            page = new WebsitePage
            {
                AgentWebsiteId = website.Id,
                SortOrder = await _db.WebsitePages.CountAsync(p => p.AgentWebsiteId == website.Id),
                CreatedAt = DateTime.UtcNow
            };
            _db.WebsitePages.Add(page);
        }

        page.Title = title;
        page.Slug = slug;
        page.NavigationLabel = navigationLabel;
        page.MetaTitle = metaTitle?.Trim() ?? string.Empty;
        page.MetaDescription = metaDescription?.Trim() ?? string.Empty;
        page.ParentPageId = parentPageId;
        page.ShowInNavigation = showInNavigation;
        page.IsPublished = isPublished;
        page.IsHomePage = isHomePage;
        page.UpdatedAt = DateTime.UtcNow;

        if (isHomePage)
        {
            var otherHomes = await _db.WebsitePages.Where(p => p.AgentWebsiteId == website.Id && p.Id != page.Id && p.IsHomePage).ToListAsync();
            foreach (var other in otherHomes) other.IsHomePage = false;
        }

        await _db.SaveChangesAsync();
        if (isNewPage)
        {
            var starterBlockTypes = GetStarterBlockTypes(starterPreset, isHomePage);
            for (var index = 0; index < starterBlockTypes.Length; index++)
            {
                _db.WebsiteContentBlocks.Add(NewBlock(page.Id, starterBlockTypes[index], index));
            }

            await _db.SaveChangesAsync();
        }
        else if (!await _db.WebsiteContentBlocks.AnyAsync(b => b.WebsitePageId == page.Id))
        {
            _db.WebsiteContentBlocks.Add(NewBlock(page.Id, isHomePage ? WebsiteBlockTypes.Hero : WebsiteBlockTypes.Text, 0));
            await _db.SaveChangesAsync();
        }

        TempData["Success"] = id == 0 ? "Page created." : "Page settings saved.";
        return RedirectToAction(nameof(Edit), new { id = page.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Duplicate(int id)
    {
        var source = await OwnedPages().Include(p => p.Blocks).FirstOrDefaultAsync(p => p.Id == id);
        if (source == null) return NotFound();
        var slug = await UniqueSlugAsync(source.AgentWebsiteId, source.Slug + "-copy");
        var copy = new WebsitePage
        {
            AgentWebsiteId = source.AgentWebsiteId,
            Title = source.Title + " Copy",
            Slug = slug,
            NavigationLabel = source.NavigationLabel + " Copy",
            MetaTitle = source.MetaTitle,
            MetaDescription = source.MetaDescription,
            ShowInNavigation = false,
            IsPublished = false,
            SortOrder = await _db.WebsitePages.CountAsync(p => p.AgentWebsiteId == source.AgentWebsiteId),
            Blocks = source.Blocks.OrderBy(b => b.SortOrder).Select(CloneBlock).ToList()
        };
        _db.WebsitePages.Add(copy);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Page duplicated as a draft.";
        return RedirectToAction(nameof(Edit), new { id = copy.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var page = await OwnedPages().FirstOrDefaultAsync(p => p.Id == id);
        if (page == null) return NotFound();
        if (page.IsHomePage)
        {
            TempData["Error"] = "Choose another home page before deleting this one.";
            return RedirectToAction(nameof(Index));
        }
        _db.WebsitePages.Remove(page);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Page deleted.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MovePage(int id, string direction)
    {
        var page = await OwnedPages().FirstOrDefaultAsync(p => p.Id == id);
        if (page == null) return NotFound();
        var pages = await _db.WebsitePages.Where(p => p.AgentWebsiteId == page.AgentWebsiteId).OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync();
        MoveItem(pages, page, direction, p => p.SortOrder, (p, value) => p.SortOrder = value);
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddBlock(int pageId, string blockType)
    {
        var page = await OwnedPages().FirstOrDefaultAsync(p => p.Id == pageId);
        if (page == null) return NotFound();
        if (!WebsiteBlockTypes.All.Contains(blockType)) blockType = WebsiteBlockTypes.Text;
        var order = await _db.WebsiteContentBlocks.CountAsync(b => b.WebsitePageId == pageId);
        _db.WebsiteContentBlocks.Add(NewBlock(pageId, blockType, order));
        await _db.SaveChangesAsync();
        TempData["Success"] = $"{SplitName(blockType)} block added.";
        return RedirectToAction(nameof(Edit), new { id = pageId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateBlock(int id, string heading, string subheading, string body,
        string imageUrl, string buttonText, string buttonUrl, bool isVisible,
        string heroLayout = "split", string imagePosition = "center", string textAlignment = "left",
        string bannerHeight = "standard", int overlayStrength = 45)
    {
        var block = await _db.WebsiteContentBlocks
            .Include(b => b.WebsitePage).ThenInclude(p => p.AgentWebsite)
            .FirstOrDefaultAsync(b => b.Id == id && b.WebsitePage.AgentWebsite.AgentUserId == AgentId);
        if (block == null) return NotFound();
        block.Heading = heading?.Trim() ?? string.Empty;
        block.Subheading = subheading?.Trim() ?? string.Empty;
        block.Body = body?.Trim() ?? string.Empty;
        block.ImageUrl = NormalizeUrl(imageUrl);
        block.ButtonText = buttonText?.Trim() ?? string.Empty;
        block.ButtonUrl = NormalizeLink(buttonUrl);
        block.IsVisible = isVisible;
        if (block.BlockType == WebsiteBlockTypes.Hero)
        {
            block.SettingsJson = new WebsiteHeroSettings
            {
                Layout = heroLayout,
                ImagePosition = imagePosition,
                TextAlignment = textAlignment,
                Height = bannerHeight,
                OverlayStrength = overlayStrength
            }.ToJson();
        }
        block.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        TempData["Success"] = "Content block saved.";
        return RedirectToAction(nameof(Edit), new { id = block.WebsitePageId });
    }

    [HttpGet]
    public IActionResult ApplyImageToBlock(int? pageId)
    {
        TempData["Error"] = "Choose an image from the page editor, select a destination block, then click Use this image.";
        if (pageId.GetValueOrDefault() > 0)
        {
            return RedirectToAction(nameof(Edit), new { id = pageId.Value });
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyImageToBlock(int pageId, int blockId, string imageUrl)
    {
        var block = await _db.WebsiteContentBlocks
            .Include(b => b.WebsitePage).ThenInclude(p => p.AgentWebsite)
            .FirstOrDefaultAsync(b => b.Id == blockId
                && b.WebsitePageId == pageId
                && b.WebsitePage.AgentWebsite.AgentUserId == AgentId);
        if (block == null) return NotFound();

        block.ImageUrl = NormalizeUrl(imageUrl);
        block.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Image applied to content block.";
        return RedirectToAction(nameof(Edit), new { id = pageId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteBlock(int id)
    {
        var block = await _db.WebsiteContentBlocks
            .Include(b => b.WebsitePage).ThenInclude(p => p.AgentWebsite)
            .FirstOrDefaultAsync(b => b.Id == id && b.WebsitePage.AgentWebsite.AgentUserId == AgentId);
        if (block == null) return NotFound();
        var pageId = block.WebsitePageId;
        _db.WebsiteContentBlocks.Remove(block);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Content block removed.";
        return RedirectToAction(nameof(Edit), new { id = pageId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveBlock(int id, string direction)
    {
        var block = await _db.WebsiteContentBlocks
            .Include(b => b.WebsitePage).ThenInclude(p => p.AgentWebsite)
            .FirstOrDefaultAsync(b => b.Id == id && b.WebsitePage.AgentWebsite.AgentUserId == AgentId);
        if (block == null) return NotFound();
        var blocks = await _db.WebsiteContentBlocks.Where(b => b.WebsitePageId == block.WebsitePageId).OrderBy(b => b.SortOrder).ThenBy(b => b.Id).ToListAsync();
        MoveItem(blocks, block, direction, b => b.SortOrder, (b, value) => b.SortOrder = value);
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Edit), new { id = block.WebsitePageId });
    }

    private IQueryable<WebsitePage> OwnedPages() => _db.WebsitePages.Where(p => p.AgentWebsite.AgentUserId == AgentId);

    private Task<AgentWebsite?> GetWebsiteAsync() => _db.AgentWebsites.FirstOrDefaultAsync(w => w.AgentUserId == AgentId);

    private async Task EnsureStarterPagesAsync(AgentWebsite website)
    {
        if (await _db.WebsitePages.AnyAsync(p => p.AgentWebsiteId == website.Id)) return;
        var agent = await _db.AgentUsers.AsNoTracking().FirstAsync(a => a.Id == AgentId);
        var candidates = await _db.WebsiteStarterPages
            .AsNoTracking()
            .Include(p => p.Blocks)
            .Where(p => p.IsActive &&
                        (p.BusinessType == agent.BusinessType || p.BusinessType == "All") &&
                        (!p.BillingRuleId.HasValue || p.BillingRuleId == agent.PackageId))
            .ToListAsync();
        var selected = candidates
            .GroupBy(p => p.Slug, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(p => p.BusinessType == agent.BusinessType)
                .ThenByDescending(p => p.BillingRuleId == agent.PackageId)
                .First())
            .OrderBy(p => p.SortOrder)
            .ToList();
        foreach (var starter in selected)
        {
            _db.WebsitePages.Add(new WebsitePage
            {
                AgentWebsiteId = website.Id,
                Title = starter.Title,
                Slug = starter.Slug,
                NavigationLabel = starter.NavigationLabel,
                MetaTitle = starter.MetaTitle,
                MetaDescription = starter.MetaDescription,
                IsHomePage = starter.IsHomePage,
                ShowInNavigation = starter.ShowInNavigation,
                IsPublished = true,
                SortOrder = starter.SortOrder,
                Blocks = starter.Blocks.OrderBy(b => b.SortOrder).Select(b => new WebsiteContentBlock
                {
                    BlockType = b.BlockType, Heading = b.Heading, Subheading = b.Subheading, Body = b.Body,
                    ImageUrl = b.ImageUrl, ButtonText = b.ButtonText, ButtonUrl = b.ButtonUrl,
                    SettingsJson = b.SettingsJson, SortOrder = b.SortOrder, IsVisible = b.IsVisible
                }).ToList()
            });
        }
        await _db.SaveChangesAsync();
    }

    private async Task<List<WebsitePage>> GetParentChoicesAsync(int websiteId, int excludedId) => await _db.WebsitePages
        .AsNoTracking().Where(p => p.AgentWebsiteId == websiteId && p.Id != excludedId && p.ParentPageId == null)
        .OrderBy(p => p.SortOrder).ToListAsync();

    private Task<List<WebsiteMediaAsset>> GetMediaAssetsAsync(int websiteId) => _db.WebsiteMediaAssets
        .AsNoTracking().Where(a => a.AgentWebsiteId == websiteId).OrderByDescending(a => a.CreatedAt).ToListAsync();

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

    private async Task<IActionResult?> RequireWebsiteAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.InstantWebsite);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Index", "Billing");
    }

    private async Task<string> UniqueSlugAsync(int websiteId, string preferred)
    {
        var baseSlug = NormalizeSlug(preferred);
        var slug = baseSlug;
        var suffix = 2;
        while (await _db.WebsitePages.AnyAsync(p => p.AgentWebsiteId == websiteId && p.Slug == slug)) slug = $"{baseSlug}-{suffix++}";
        return slug;
    }

    private static string[] GetStarterBlockTypes(string starterPreset, bool isHomePage)
    {
        return starterPreset.ToLowerInvariant() switch
        {
            "about" => new[] { WebsiteBlockTypes.Text, WebsiteBlockTypes.Testimonials, WebsiteBlockTypes.CallToAction },
            "services" => new[] { WebsiteBlockTypes.Hero, WebsiteBlockTypes.Services, WebsiteBlockTypes.CallToAction },
            "contact" => new[] { WebsiteBlockTypes.Hero, WebsiteBlockTypes.ContactForm },
            "landing" => new[] { WebsiteBlockTypes.Hero, WebsiteBlockTypes.Services, WebsiteBlockTypes.Testimonials, WebsiteBlockTypes.CallToAction },
            _ => new[] { isHomePage ? WebsiteBlockTypes.Hero : WebsiteBlockTypes.Text }
        };
    }

    private static WebsiteContentBlock NewBlock(int pageId, string type, int order) => new()
    {
        WebsitePageId = pageId, BlockType = type, SortOrder = order, IsVisible = true,
        Heading = type switch
        {
            WebsiteBlockTypes.Hero => "A clear headline for your business",
            WebsiteBlockTypes.Services => "Our services",
            WebsiteBlockTypes.CallToAction => "Ready to connect?",
            WebsiteBlockTypes.ContactForm => "Contact us",
            WebsiteBlockTypes.Testimonials => "What clients say",
            WebsiteBlockTypes.NewsletterSignup => "Stay informed",
            _ => "New content section"
        },
        Subheading = "Add a short supporting message.",
        Body = type == WebsiteBlockTypes.Services ? "Service one\nService two\nService three" : "Add your content here.",
        ButtonText = type is WebsiteBlockTypes.Hero or WebsiteBlockTypes.CallToAction ? "Learn more" : string.Empty
    };

    private static WebsiteContentBlock CloneBlock(WebsiteContentBlock b) => new()
    {
        BlockType = b.BlockType, Heading = b.Heading, Subheading = b.Subheading, Body = b.Body,
        ImageUrl = b.ImageUrl, ButtonText = b.ButtonText, ButtonUrl = b.ButtonUrl,
        SettingsJson = b.SettingsJson, SortOrder = b.SortOrder, IsVisible = b.IsVisible
    };

    private static void MoveItem<T>(List<T> items, T item, string direction, Func<T, int> getOrder, Action<T, int> setOrder)
    {
        var index = items.IndexOf(item);
        var target = direction.Equals("up", StringComparison.OrdinalIgnoreCase) ? index - 1 : index + 1;
        if (index < 0 || target < 0 || target >= items.Count) return;
        var currentOrder = getOrder(item);
        setOrder(item, getOrder(items[target]));
        setOrder(items[target], currentOrder);
    }

    private static string NormalizeSlug(string value)
    {
        var slug = Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "page" : slug;
    }

    private static string NormalizeUrl(string? value)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal))
        {
            return value;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.ToString()
            : string.Empty;
    }
    private static string NormalizeLink(string? value)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.StartsWith('/')) return value;
        return NormalizeUrl(value);
    }
    private static string SplitName(string value) => Regex.Replace(value, "([a-z])([A-Z])", "$1 $2");
}
