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
        var sentPolls = await _db.PollSurveys
            .Where(s => s.AgentUserId == AgentId && s.Status != PollSurveyStatus.Draft)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
        var agentDocuments = await _db.AgentDocuments
            .Where(d => d.AgentUserId == AgentId)
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync();
        return View(new WebsitePageEditViewModel
        {
            Page = page,
            AvailableParents = await GetParentChoicesAsync(page.AgentWebsiteId, page.Id),
            MediaAssets = await GetMediaAssetsAsync(page.AgentWebsiteId),
            AvailableSentPolls = sentPolls,
            AvailableAgentDocuments = agentDocuments
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
        bool showContactPhoto = true)
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
            showAgentPhoto, showAgentDesignation, showAgentAddress, showAgentPhone, showAgentEmail, showContactPhoto);

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

        return new PublicWebsiteViewModel
        {
            Website = website,
            Pages = pages,
            CurrentPage = page,
            ApprovedTestimonials = approvedTestimonials,
            PollResultsByBlockId = pollResultsByBlockId
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
        if (blockType == WebsiteBlockTypes.TestimonialForm)
        {
            var testimonialAccess = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.TestimonialManager);
            if (!testimonialAccess.IsIncluded)
            {
                TempData["Error"] = testimonialAccess.UpgradeMessage;
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
        bool showContactPhoto = true)
    {
        var block = await _db.WebsiteContentBlocks
            .Include(b => b.WebsitePage).ThenInclude(p => p.AgentWebsite)
            .FirstOrDefaultAsync(b => b.Id == id && b.WebsitePage.AgentWebsite.AgentUserId == AgentId);
        if (block == null) return NotFound();

        await ApplyBlockFieldsAsync(block, heading, subheading, body, imageUrl, buttonText, buttonUrl, isVisible,
            heroLayout, imagePosition, textAlignment, bannerHeight, overlayStrength, layoutVariant,
            pollSurveyId, agentDocumentId, reviewPlatform, reviewUrl, reviewRating, reviewCount,
            showAgentPhoto, showAgentDesignation, showAgentAddress, showAgentPhone, showAgentEmail, showContactPhoto);
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
        bool showContactPhoto)
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
            "about" => new[] { WebsiteBlockTypes.Text, WebsiteBlockTypes.CallToAction },
            "services" => new[] { WebsiteBlockTypes.Hero, WebsiteBlockTypes.Services, WebsiteBlockTypes.CallToAction },
            "contact" => new[] { WebsiteBlockTypes.Hero, WebsiteBlockTypes.ContactForm },
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
