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
        await EnsureResourcesAsync(website);
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
        await EnsureResourcesAsync(website);
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
        page.ParentPageId = await ResolveParentAsync(page.AgentWebsiteId, page.Id, page.IsHomePage, parentPageId);
        page.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        // A5-M-PARENT (fixed 2026-08-20): a rejected parent (missing, circular, or too deep) used
        // to fall back to top-level silently while the flash said everything worked.
        if (parentPageId.HasValue && page.ParentPageId != parentPageId)
        {
            TempData["Warning"] = $"Navigation settings saved for {page.Title}, but the menu position you chose was not valid (it would create a loop or exceed the 3-level menu), so it was placed at the top level instead.";
        }
        else
        {
            TempData["Success"] = $"Navigation settings saved for {page.Title}.";
        }
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
        var pages = await _db.WebsitePages
            .AsNoTracking()
            .Where(p => p.AgentWebsiteId == website.Id)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Title)
            .ToListAsync();
        return View(new WebsiteFooterViewModel
        {
            Website = website,
            Footer = WebsiteFooterSettings.FromJson(website.FooterSettingsJson),
            Pages = pages
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
        var sentPolls = await _db.PollSurveys
            .Where(s => s.AgentUserId == AgentId && s.Status != PollSurveyStatus.Draft)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
        var agentDocuments = await _db.AgentDocuments
            .Where(d => d.AgentUserId == AgentId)
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync();
        var availableForms = await _db.WebsiteForms
            .Where(f => f.AgentUserId == AgentId && f.IsActive)
            .OrderByDescending(f => f.UpdatedAt)
            .ToListAsync();
        var articles = await _db.Articles
            .Where(a => a.AgentUserId == AgentId)
            .OrderByDescending(a => a.UpdatedAt)
            .ToListAsync();
        if (page.Blocks.Any(b => b.BlockType == WebsiteBlockTypes.Gallery))
        {
            var storageAccess = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.FileUploadCapacity);
            ViewBag.StorageAccess = storageAccess;
            ViewBag.StorageUsedMb = IPRO.Web.Infrastructure.AgentStorageUsage.ToMb(
                await IPRO.Web.Infrastructure.AgentStorageUsage.TotalBytesAsync(_db, AgentId));
        }
        return View(new WebsitePageEditViewModel
        {
            Page = page,
            AvailableParents = await GetParentChoicesAsync(page.AgentWebsiteId, page.Id),
            MediaAssets = await GetMediaAssetsAsync(page.AgentWebsiteId),
            AvailableSentPolls = sentPolls,
            AvailableAgentDocuments = agentDocuments,
            AvailableForms = availableForms,
            AvailableArticles = articles
        });
    }

    // Shows this page exactly as it will render publicly, using whatever is currently saved -- regardless of
    // the page's own Published toggle or the site's overall Publish state. This is the only way an agent can
    // see their own draft work; the real public site (PublicWebsiteController) only ever shows published content.
    [HttpGet]
    public async Task<IActionResult> Preview(int id)
    {
        var page = await OwnedPages()
            .Include(p => p.Blocks)
            .Include(p => p.AgentWebsite).ThenInclude(w => w.AgentUser)
            .Include(p => p.AgentWebsite).ThenInclude(w => w.Template)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (page == null) return NotFound();

        var model = await BuildPreviewViewModelAsync(page);
        ViewBag.IsTemplatePreview = true;
        return View("~/Views/PublicWebsite/Index.cshtml", model);
    }

    // Same rendering as Preview, but applies the submitted (not-yet-saved) form values to the one block being
    // edited before rendering -- nothing is written to the database. This is deliberately the primary way to
    // check a change: for an already-published page, Save Block goes live immediately, so Preview-after-save
    // offers no safety net. This lets an agent see the effect of a change before ever committing to it.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PreviewUnsaved(int id, string heading, string subheading, string body,
        string imageUrl, string buttonText, string buttonUrl, bool isVisible,
        string heroLayout = "split", string imagePosition = "center", string textAlignment = "left",
        string bannerHeight = "standard", int overlayStrength = 45, string layoutVariant = "",
        int pollSurveyId = 0, int agentDocumentId = 0,
        string reviewPlatform = "Google", string reviewUrl = "", decimal reviewRating = 5.0m, int reviewCount = 0,
        bool showAgentPhoto = true, bool showAgentDesignation = true, bool showAgentAddress = true, bool showAgentPhone = true, bool showAgentEmail = true,
        bool showContactPhoto = true, string mapAddress = "", string mapHeight = "standard", int websiteFormId = 0, int[]? articleIds = null, string layoutStyle = "auto", int articleId = 0,
        int blogPostCount = 6, bool blogShowImages = true,
        string videoUrl = "", string calculatorKind = "")
    {
        var ownedPageId = await _db.WebsiteContentBlocks
            .Where(b => b.Id == id && b.WebsitePage.AgentWebsite.AgentUserId == AgentId)
            .Select(b => b.WebsitePageId)
            .FirstOrDefaultAsync();
        if (ownedPageId == 0) return NotFound();

        var page = await _db.WebsitePages
            .AsNoTracking()
            .Include(p => p.Blocks)
            .Include(p => p.AgentWebsite).ThenInclude(w => w.AgentUser)
            .Include(p => p.AgentWebsite).ThenInclude(w => w.Template)
            .FirstOrDefaultAsync(p => p.Id == ownedPageId);
        if (page == null) return NotFound();

        var block = page.Blocks.FirstOrDefault(b => b.Id == id);
        if (block == null) return NotFound();

        await ApplyBlockFieldsAsync(block, heading, subheading, body, imageUrl, buttonText, buttonUrl, isVisible,
            heroLayout, imagePosition, textAlignment, bannerHeight, overlayStrength, layoutVariant,
            pollSurveyId, agentDocumentId, reviewPlatform, reviewUrl, reviewRating, reviewCount,
            showAgentPhoto, showAgentDesignation, showAgentAddress, showAgentPhone, showAgentEmail, showContactPhoto,
            mapAddress, mapHeight, websiteFormId, articleIds, layoutStyle, articleId,
            blogPostCount, blogShowImages, videoUrl, calculatorKind);

        var model = await BuildPreviewViewModelAsync(page);
        ViewBag.IsTemplatePreview = true;
        ViewBag.IsUnsavedPreview = true;
        return View("~/Views/PublicWebsite/Index.cshtml", model);
    }

    private async Task<PublicWebsiteViewModel> BuildPreviewViewModelAsync(WebsitePage page)
    {
        var website = page.AgentWebsite;
        var pages = await _db.WebsitePages
            .AsNoTracking()
            .Where(p => p.AgentWebsiteId == website.Id)
            .Include(p => p.Blocks)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Title)
            .ToListAsync();
        var pageIndex = pages.FindIndex(p => p.Id == page.Id);
        if (pageIndex >= 0) pages[pageIndex] = page;

        var approvedTestimonials = page.Blocks.Any(b => b.BlockType == WebsiteBlockTypes.TestimonialForm)
            ? await _db.TestimonialSubmissions
                .Where(t => t.AgentUserId == AgentId && t.Status == TestimonialStatus.Approved)
                .OrderByDescending(t => t.ReviewedAt)
                .ToListAsync()
            : new List<TestimonialSubmission>();

        var pollResultsByBlockId = await IPRO.Web.Infrastructure.PollResultsBuilder.BuildAsync(_db, AgentId, page, isOwnerPreview: true);
        var formsByBlockId = await IPRO.Web.Infrastructure.PublicFormBuilder.BuildAsync(_db, AgentId, page);
        var didYouKnowByBlockId = await IPRO.Web.Infrastructure.DidYouKnowBuilder.BuildAsync(_db, AgentId, page);
        var articleContentByBlockId = await IPRO.Web.Infrastructure.ArticleContentBuilder.BuildAsync(_db, AgentId, page);

        return new PublicWebsiteViewModel
        {
            Website = website,
            Pages = pages,
            CurrentPage = page,
            ApprovedTestimonials = approvedTestimonials,
            PollResultsByBlockId = pollResultsByBlockId,
            FormsByBlockId = formsByBlockId,
            DidYouKnowByBlockId = didYouKnowByBlockId,
            ArticleContentByBlockId = articleContentByBlockId
        };
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

        // Media-library images share the FileUploadCapacity pool with documents, gallery photos and
        // portal files, but this path checked no quota and its bytes were never counted -- so an
        // agent could push unlimited images through the page editor while the paths that DO enforce
        // the limit reported a usage figure far below reality (2026-08-14 ultra-audit).
        var capacity = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.FileUploadCapacity);
        var limitBytes = IPRO.Web.Infrastructure.AgentStorageUsage.LimitBytes(capacity.LimitValue);
        var usedBytes = await IPRO.Web.Infrastructure.AgentStorageUsage.TotalBytesAsync(_db, AgentId);
        if (limitBytes > 0 && usedBytes + image.Length > limitBytes)
        {
            TempData["Error"] = $"That upload would exceed your storage limit " +
                $"({IPRO.Web.Infrastructure.AgentStorageUsage.ToMb(usedBytes)} MB of {IPRO.Web.Infrastructure.AgentStorageUsage.DisplayLimitMb(capacity.LimitValue)} MB used, counting documents, " +
                "website photos and client portal files). Delete something to free up space, or contact us to increase your storage.";
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
        var url = await _blob.UploadAsync(stream, image.FileName, "website-media", expectedContentType, isPrivate: false);
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
        _db.WebsiteMediaAssets.Remove(asset);
        await _db.SaveChangesAsync();
        // Rows first, file second -- and the file only goes when nothing else still references it
        // (another agent's page, an article, or newsletter HTML that copied the URL; A5-H12/H14).
        if (!await IPRO.DataAccess.BlobReferences.IsReferencedAsync(_db, asset.BlobUrl))
        {
            await _blob.DeleteAsync(asset.BlobUrl);
        }
        TempData["Success"] = "Image removed from the media library.";
        return RedirectToAction(nameof(Edit), new { id = pageId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> UploadGalleryImage(int blockId, IFormFile? image)
    {
        var block = await _db.WebsiteContentBlocks
            .Include(b => b.WebsitePage).ThenInclude(p => p.AgentWebsite)
            .FirstOrDefaultAsync(b => b.Id == blockId && b.BlockType == WebsiteBlockTypes.Gallery && b.WebsitePage.AgentWebsite.AgentUserId == AgentId);
        if (block == null) return NotFound();

        if (image == null || image.Length == 0)
        {
            TempData["Error"] = "Choose an image to upload.";
            return RedirectToAction(nameof(Edit), new { id = block.WebsitePageId });
        }
        if (image.Length > 8 * 1024 * 1024)
        {
            TempData["Error"] = "Images must be 8 MB or smaller.";
            return RedirectToAction(nameof(Edit), new { id = block.WebsitePageId });
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
            return RedirectToAction(nameof(Edit), new { id = block.WebsitePageId });
        }

        await using var stream = image.OpenReadStream();
        if (!await HasValidImageSignatureAsync(stream, extension))
        {
            TempData["Error"] = "That file does not contain a valid supported image.";
            return RedirectToAction(nameof(Edit), new { id = block.WebsitePageId });
        }
        stream.Position = 0;

        // Gallery photos share the same per-package storage pool as Agent Documents, rather than
        // getting a separate quota to configure -- see the "gallery images" section of the file
        // upload capacity feature in DOCS.
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.FileUploadCapacity);
        var limitBytes = IPRO.Web.Infrastructure.AgentStorageUsage.LimitBytes(access.LimitValue);
        var usedBytes = await IPRO.Web.Infrastructure.AgentStorageUsage.TotalBytesAsync(_db, AgentId);
        if (limitBytes > 0 && usedBytes + image.Length > limitBytes)
        {
            var usedMb = IPRO.Web.Infrastructure.AgentStorageUsage.ToMb(usedBytes);
            var limitMb = IPRO.Web.Infrastructure.AgentStorageUsage.DisplayLimitMb(access.LimitValue);   // M10
            TempData["Error"] = $"That upload would exceed your storage limit ({usedMb} MB of {limitMb} MB used). Delete unused documents or gallery photos to free up space, or contact us to increase your storage.";
            return RedirectToAction(nameof(Edit), new { id = block.WebsitePageId });
        }

        var url = await _blob.UploadAsync(stream, image.FileName, "website-gallery", expectedContentType, isPrivate: false);
        var gallerySettings = WebsiteGallerySettings.FromJson(block.SettingsJson);
        gallerySettings.Images.Add(new WebsiteGalleryImage { Url = url, FileSizeBytes = image.Length });
        block.SettingsJson = gallerySettings.ToJson();
        block.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Photo added to gallery.";
        return RedirectToAction(nameof(Edit), new { id = block.WebsitePageId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteGalleryImage(int blockId, string url)
    {
        var block = await _db.WebsiteContentBlocks
            .Include(b => b.WebsitePage).ThenInclude(p => p.AgentWebsite)
            .FirstOrDefaultAsync(b => b.Id == blockId && b.BlockType == WebsiteBlockTypes.Gallery && b.WebsitePage.AgentWebsite.AgentUserId == AgentId);
        if (block == null) return NotFound();

        var gallerySettings = WebsiteGallerySettings.FromJson(block.SettingsJson);
        if (gallerySettings.Images.RemoveAll(i => i.Url == url) > 0)
        {
            block.SettingsJson = gallerySettings.ToJson();
            block.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            // Row first, file second (the old order destroyed the file even when the save failed),
            // and only when no other gallery/article/newsletter still references it.
            if (!await IPRO.DataAccess.BlobReferences.IsReferencedAsync(_db, url))
            {
                await _blob.DeleteAsync(url);
            }
            TempData["Success"] = "Photo removed from gallery.";
        }
        return RedirectToAction(nameof(Edit), new { id = block.WebsitePageId });
    }

    // GetGalleryBytesUsedAsync moved to Infrastructure/AgentStorageUsage so the documents upload can
    // use the same figure. Keeping a private copy here is what let the two paths disagree about how
    // much storage an agent had used.

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

        var requestedParentId = parentPageId;
        parentPageId = await ResolveParentAsync(website.Id, id, isHomePage, parentPageId);
        if (requestedParentId.HasValue && parentPageId != requestedParentId)
        {
            // Same honesty as SaveNavigationItem: the save continues (matching the established
            // fallback) but the agent is told their placement was rejected.
            TempData["Warning"] = "The parent page you chose was not valid (it would create a loop or exceed the 3-level menu), so this page was saved at the top level instead.";
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
        // The FK is ON DELETE SET NULL, which would promote every child straight to the top menu --
        // deleting one category would dump all of its article pages into the main nav. Move them up
        // exactly one level instead, so they stay under the section they belonged to.
        var orphans = await _db.WebsitePages.Where(p => p.ParentPageId == page.Id).ToListAsync();
        foreach (var orphan in orphans)
        {
            orphan.ParentPageId = page.ParentPageId;
            orphan.UpdatedAt = DateTime.UtcNow;
        }

        _db.WebsitePages.Remove(page);
        await _db.SaveChangesAsync();
        TempData["Success"] = orphans.Count == 0
            ? "Page deleted."
            : $"Page deleted. {orphans.Count} page{(orphans.Count == 1 ? "" : "s")} that sat under it moved up one level.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MovePage(int id, string direction)
    {
        var page = await OwnedPages().FirstOrDefaultAsync(p => p.Id == id);
        if (page == null) return NotFound();
        // Scoped to siblings, matching MoveNavigationItem -- reordering across the whole site ignores
        // parentage and produces an order the menu can't actually express.
        var pages = await _db.WebsitePages.Where(p => p.AgentWebsiteId == page.AgentWebsiteId && p.ParentPageId == page.ParentPageId).OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync();
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
        if (blockType == WebsiteBlockTypes.TestimonialForm)
        {
            var testimonialAccess = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.TestimonialManager);
            if (!testimonialAccess.IsIncluded)
            {
                TempData["Error"] = testimonialAccess.UpgradeMessage;
                return RedirectToAction(nameof(Edit), new { id = pageId });
            }
        }
        // The Blog block is a paid feature (Platinum/Broker today). Gated HERE, not only in the
        // picker, because a crafted POST reaches this action directly -- the same hole M2 closed on
        // RebuildRequestMeeting.
        if (blockType == WebsiteBlockTypes.Blog)
        {
            var blogAccess = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.ManagedBlog);
            if (!blogAccess.IsIncluded)
            {
                TempData["Error"] = blogAccess.UpgradeMessage;
                return RedirectToAction(nameof(Edit), new { id = pageId });
            }
        }
        if (blockType == WebsiteBlockTypes.PollResults)
        {
            var pollAccess = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.PollSurveys);
            if (!pollAccess.IsIncluded)
            {
                TempData["Error"] = pollAccess.UpgradeMessage;
                return RedirectToAction(nameof(Edit), new { id = pageId });
            }
        }
        if (blockType == WebsiteBlockTypes.LeadMagnet)
        {
            var leadMagnetAccess = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.LeadMagnet);
            if (!leadMagnetAccess.IsIncluded)
            {
                TempData["Error"] = leadMagnetAccess.UpgradeMessage;
                return RedirectToAction(nameof(Edit), new { id = pageId });
            }
        }
        if (blockType == WebsiteBlockTypes.Form)
        {
            var formAccess = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.CustomForms);
            if (!formAccess.IsIncluded)
            {
                TempData["Error"] = formAccess.UpgradeMessage;
                return RedirectToAction(nameof(Edit), new { id = pageId });
            }
        }
        if (blockType == WebsiteBlockTypes.DidYouKnow || blockType == WebsiteBlockTypes.ArticleContent)
        {
            var articlesAccess = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.Newsletters);
            if (!articlesAccess.IsIncluded)
            {
                TempData["Error"] = articlesAccess.UpgradeMessage;
                return RedirectToAction(nameof(Edit), new { id = pageId });
            }
        }
        var order = await _db.WebsiteContentBlocks.CountAsync(b => b.WebsitePageId == pageId);
        _db.WebsiteContentBlocks.Add(NewBlock(pageId, blockType, order));
        await _db.SaveChangesAsync();
        TempData["Success"] = $"{WebsiteBlockTypes.DisplayName(blockType)} block added.";
        return RedirectToAction(nameof(Edit), new { id = pageId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateBlock(int id, string heading, string subheading, string body,
        string imageUrl, string buttonText, string buttonUrl, bool isVisible,
        string heroLayout = "split", string imagePosition = "center", string textAlignment = "left",
        string bannerHeight = "standard", int overlayStrength = 45, string layoutVariant = "",
        int pollSurveyId = 0, int agentDocumentId = 0,
        string reviewPlatform = "Google", string reviewUrl = "", decimal reviewRating = 5.0m, int reviewCount = 0,
        bool showAgentPhoto = true, bool showAgentDesignation = true, bool showAgentAddress = true, bool showAgentPhone = true, bool showAgentEmail = true,
        bool showContactPhoto = true, string mapAddress = "", string mapHeight = "standard", int websiteFormId = 0, int[]? articleIds = null, string layoutStyle = "auto", int articleId = 0,
        int blogPostCount = 6, bool blogShowImages = true,
        string videoUrl = "", string calculatorKind = "")
    {
        var block = await _db.WebsiteContentBlocks
            .Include(b => b.WebsitePage).ThenInclude(p => p.AgentWebsite)
            .FirstOrDefaultAsync(b => b.Id == id && b.WebsitePage.AgentWebsite.AgentUserId == AgentId);
        if (block == null) return NotFound();

        await ApplyBlockFieldsAsync(block, heading, subheading, body, imageUrl, buttonText, buttonUrl, isVisible,
            heroLayout, imagePosition, textAlignment, bannerHeight, overlayStrength, layoutVariant,
            pollSurveyId, agentDocumentId, reviewPlatform, reviewUrl, reviewRating, reviewCount,
            showAgentPhoto, showAgentDesignation, showAgentAddress, showAgentPhone, showAgentEmail, showContactPhoto,
            mapAddress, mapHeight, websiteFormId, articleIds, layoutStyle, articleId,
            blogPostCount, blogShowImages, videoUrl, calculatorKind);
        block.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        TempData["Success"] = "Content block saved.";
        return RedirectToAction(nameof(Edit), new { id = block.WebsitePageId });
    }

    // Applies submitted form values to a block in memory. Does not save -- callers decide whether/when to
    // call SaveChangesAsync, so this same logic can back both a real save (UpdateBlock) and a no-write
    // preview (PreviewUnsaved).
    private async Task ApplyBlockFieldsAsync(WebsiteContentBlock block, string heading, string subheading, string body,
        string imageUrl, string buttonText, string buttonUrl, bool isVisible,
        string heroLayout, string imagePosition, string textAlignment, string bannerHeight, int overlayStrength, string layoutVariant,
        int pollSurveyId, int agentDocumentId,
        string reviewPlatform, string reviewUrl, decimal reviewRating, int reviewCount,
        bool showAgentPhoto, bool showAgentDesignation, bool showAgentAddress, bool showAgentPhone, bool showAgentEmail,
        bool showContactPhoto, string mapAddress, string mapHeight, int websiteFormId, int[]? articleIds, string layoutStyle = "auto", int articleId = 0,
        int blogPostCount = 6, bool blogShowImages = true,
        string videoUrl = "", string calculatorKind = "")
    {
        block.Heading = heading?.Trim() ?? string.Empty;
        block.Subheading = subheading?.Trim() ?? string.Empty;
        block.Body = body?.Trim() ?? string.Empty;
        block.ImageUrl = NormalizeUrl(imageUrl);
        block.ButtonText = buttonText?.Trim() ?? string.Empty;
        block.ButtonUrl = NormalizeLink(buttonUrl);
        block.IsVisible = isVisible;
        block.LayoutVariant = WebsiteBlockLayoutVariants.Normalize(block.BlockType, layoutVariant);
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
        else if (block.BlockType == WebsiteBlockTypes.PollResults)
        {
            var pollBelongsToAgent = pollSurveyId > 0 && await _db.PollSurveys.AnyAsync(s => s.Id == pollSurveyId && s.AgentUserId == AgentId);
            block.SettingsJson = new WebsitePollResultsSettings
            {
                PollSurveyId = pollBelongsToAgent ? pollSurveyId : 0
            }.ToJson();
        }
        else if (block.BlockType == WebsiteBlockTypes.Blog)
        {
            // Count is clamped by EffectivePostCount on READ as well, so even a hand-crafted POST
            // cannot make a public page ask for thousands of rows.
            block.SettingsJson = new WebsiteBlogSettings
            {
                PostCount = blogPostCount,
                ShowImages = blogShowImages
            }.ToJson();
        }
        else if (block.BlockType == WebsiteBlockTypes.LeadMagnet)
        {
            var docBelongsToAgent = agentDocumentId > 0 && await _db.AgentDocuments.AnyAsync(d => d.Id == agentDocumentId && d.AgentUserId == AgentId);
            block.SettingsJson = new WebsiteLeadMagnetSettings
            {
                AgentDocumentId = docBelongsToAgent ? agentDocumentId : 0
            }.ToJson();
        }
        else if (block.BlockType == WebsiteBlockTypes.Reviews)
        {
            block.SettingsJson = new WebsiteReviewSettings
            {
                Platform = reviewPlatform,
                ReviewUrl = NormalizeUrl(reviewUrl),
                Rating = reviewRating,
                ReviewCount = reviewCount
            }.ToJson();
        }
        else if (block.BlockType == WebsiteBlockTypes.Calculator)
        {
            block.SettingsJson = new WebsiteCalculatorSettings
            {
                CalculatorKind = CalculatorKinds.Normalize(calculatorKind)
            }.ToJson();
        }
        else if (block.BlockType == WebsiteBlockTypes.AgentInfo)
        {
            block.SettingsJson = new WebsiteAgentInfoSettings
            {
                ShowPhoto = showAgentPhoto,
                ShowDesignation = showAgentDesignation,
                ShowAddress = showAgentAddress,
                ShowPhone = showAgentPhone,
                ShowEmail = showAgentEmail
            }.ToJson();
        }
        else if (block.BlockType == WebsiteBlockTypes.ContactForm)
        {
            block.SettingsJson = new WebsiteContactFormSettings
            {
                ShowPhoto = showContactPhoto
            }.ToJson();
        }
        else if (block.BlockType == WebsiteBlockTypes.Maps)
        {
            block.SettingsJson = new WebsiteMapSettings
            {
                Address = mapAddress?.Trim() ?? string.Empty,
                Height = mapHeight
            }.ToJson();
        }
        else if (block.BlockType == WebsiteBlockTypes.Form)
        {
            var formBelongsToAgent = websiteFormId > 0 && await _db.WebsiteForms.AnyAsync(f => f.Id == websiteFormId && f.AgentUserId == AgentId);
            block.SettingsJson = new WebsiteFormSettings
            {
                WebsiteFormId = formBelongsToAgent ? websiteFormId : 0
            }.ToJson();
        }
        else if (block.BlockType == WebsiteBlockTypes.DidYouKnow)
        {
            var requestedArticleIds = (articleIds ?? Array.Empty<int>()).Where(id => id > 0).Distinct().ToList();
            var ownedArticleIds = requestedArticleIds.Count == 0
                ? new List<int>()
                : await _db.Articles.Where(a => requestedArticleIds.Contains(a.Id) && a.AgentUserId == AgentId).Select(a => a.Id).ToListAsync();
            block.SettingsJson = new WebsiteDidYouKnowSettings
            {
                // Preserve the order the agent picked them in, not whatever order the DB returned.
                ArticleIds = requestedArticleIds.Where(id => ownedArticleIds.Contains(id)).ToList(),
                LayoutStyle = layoutStyle == "grid-2x3" ? "grid-2x3" : "auto"
            }.ToJson();
        }
        else if (block.BlockType == WebsiteBlockTypes.ArticleContent)
        {
            var articleBelongsToAgent = articleId > 0 && await _db.Articles.AnyAsync(a => a.Id == articleId && a.AgentUserId == AgentId);
            block.SettingsJson = new WebsiteArticleContentSettings
            {
                ArticleId = articleBelongsToAgent ? articleId : 0
            }.ToJson();
        }
        else if (block.BlockType == WebsiteBlockTypes.Video)
        {
            block.SettingsJson = new WebsiteVideoSettings
            {
                VideoUrl = videoUrl?.Trim() ?? string.Empty
            }.ToJson();
        }
        // Gallery's SettingsJson (the image list) is managed entirely by UploadGalleryImage/DeleteGalleryImage --
        // deliberately not touched here so that saving the block's Heading/Subheading/Body never clobbers it.
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

        return NoContent();
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

    private Task EnsureStarterPagesAsync(AgentWebsite website) =>
        IPRO.Web.Infrastructure.WebsiteStarterPagesHelper.EnsureStarterPagesAsync(_db, website, AgentId);

    private Task EnsureResourcesAsync(AgentWebsite website) =>
        IPRO.Web.Infrastructure.WebsiteStarterResourcesHelper.EnsureResourcesAsync(_db, website, AgentId);

    // _PublicNavigation.cshtml renders three levels, so the page tree is capped at three. Every write
    // path routes through ResolveParentAsync rather than repeating the rule inline: the dropdown
    // source, the menu editor's Placement select and the page-settings form each used to enforce it
    // differently, and SavePage never enforced it at all -- a crafted post could park a page below
    // the deepest tier the nav can render, where it silently disappeared from the menu.
    private const int MaxNavigationDepth = 3;

    private async Task<int?> ResolveParentAsync(int websiteId, int pageId, bool isHomePage, int? requestedParentId)
    {
        if (isHomePage || !requestedParentId.HasValue || requestedParentId.Value == pageId) return null;

        var pages = await _db.WebsitePages.AsNoTracking().Where(p => p.AgentWebsiteId == websiteId).ToListAsync();
        if (pages.All(p => p.Id != requestedParentId.Value)) return null;

        var childrenByParent = pages.ToLookup(p => p.ParentPageId, p => p.Id);
        var parentById = pages.ToDictionary(p => p.Id, p => p.ParentPageId);

        // Depth of the requested parent (1 = top level), guarding against a cycle on the way up.
        var depth = 1;
        var cursor = requestedParentId.Value;
        while (parentById.TryGetValue(cursor, out var next) && next.HasValue)
        {
            if (next.Value == pageId) return null;
            depth++;
            cursor = next.Value;
            if (depth > MaxNavigationDepth) return null;
        }

        // A page that already has children of its own can't be nested as deep as a leaf can, or its
        // own children would land a tier below anything the nav can show.
        var movingHeight = pageId == 0 ? 1 : SubtreeHeight(pageId, childrenByParent);
        return depth + movingHeight <= MaxNavigationDepth ? requestedParentId : null;
    }

    private static int SubtreeHeight(int rootId, ILookup<int?, int> childrenByParent)
    {
        var height = 1;
        var level = childrenByParent[rootId].ToList();
        while (level.Count > 0 && height < MaxNavigationDepth)
        {
            height++;
            level = level.SelectMany(id => childrenByParent[id]).ToList();
        }
        return height;
    }

    private static HashSet<int> DescendantIds(int rootId, ILookup<int?, int> childrenByParent)
    {
        var found = new HashSet<int>();
        var pending = new Queue<int>(childrenByParent[rootId]);
        while (pending.Count > 0)
        {
            var id = pending.Dequeue();
            if (!found.Add(id)) continue;
            foreach (var child in childrenByParent[id]) pending.Enqueue(child);
        }
        return found;
    }

    // Returned tree-ordered (each top-level page followed by its own children) so the Parent Menu
    // and Placement dropdowns can indent second-level options and stay readable at three tiers.
    private async Task<List<WebsitePage>> GetParentChoicesAsync(int websiteId, int excludedId)
    {
        var pages = await _db.WebsitePages.AsNoTracking()
            .Where(p => p.AgentWebsiteId == websiteId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Id)
            .ToListAsync();

        var childrenByParent = pages.ToLookup(p => p.ParentPageId, p => p.Id);
        var movingHeight = excludedId == 0 ? 1 : SubtreeHeight(excludedId, childrenByParent);
        var descendants = excludedId == 0 ? new HashSet<int>() : DescendantIds(excludedId, childrenByParent);
        var choices = new List<WebsitePage>();

        foreach (var top in pages.Where(p => p.ParentPageId == null))
        {
            AddChoice(top, 1);
            foreach (var child in pages.Where(p => p.ParentPageId == top.Id))
            {
                AddChoice(child, 2);
            }
        }
        return choices;

        void AddChoice(WebsitePage candidate, int depth)
        {
            if (candidate.Id == excludedId || descendants.Contains(candidate.Id)) return;
            if (depth + movingHeight > MaxNavigationDepth) return;
            choices.Add(candidate);
        }
    }

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
            "about" => new[] { WebsiteBlockTypes.Text, WebsiteBlockTypes.CallToAction },
            "services" => new[] { WebsiteBlockTypes.Hero, WebsiteBlockTypes.Services, WebsiteBlockTypes.CallToAction },
            "contact" => new[] { WebsiteBlockTypes.Hero, WebsiteBlockTypes.ContactForm, WebsiteBlockTypes.Maps },
            "landing" => new[] { WebsiteBlockTypes.Hero, WebsiteBlockTypes.Services, WebsiteBlockTypes.CallToAction },
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
            WebsiteBlockTypes.NewsletterSignup => "Stay informed",
            WebsiteBlockTypes.TestimonialForm => "Client testimonials",
            WebsiteBlockTypes.Reviews => "What our clients say",
            WebsiteBlockTypes.AgentInfo => "Meet Your Agent",
            WebsiteBlockTypes.Maps => "Find Us",
            WebsiteBlockTypes.Form => "Get in touch",
            WebsiteBlockTypes.Video => "Watch Our Video",
            WebsiteBlockTypes.Gallery => "Photo Gallery",
            WebsiteBlockTypes.Calculator => "Try Our Calculator",
            _ => "New content section"
        },
        Subheading = "Add a short supporting message.",
        Body = type == WebsiteBlockTypes.Services ? "Service one\nService two\nService three" : "Add your content here.",
        ButtonText = type switch
        {
            WebsiteBlockTypes.Hero or WebsiteBlockTypes.CallToAction => "Learn more",
            WebsiteBlockTypes.Reviews => "Read Reviews",
            _ => string.Empty
        }
    };

    private static WebsiteContentBlock CloneBlock(WebsiteContentBlock b) => new()
    {
        BlockType = b.BlockType, Heading = b.Heading, Subheading = b.Subheading, Body = b.Body,
        ImageUrl = b.ImageUrl, ButtonText = b.ButtonText, ButtonUrl = b.ButtonUrl,
        SettingsJson = b.SettingsJson, LayoutVariant = b.LayoutVariant, SortOrder = b.SortOrder, IsVisible = b.IsVisible
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
}
