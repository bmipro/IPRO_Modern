using System.Text.RegularExpressions;
using IPRO.Admin.Models;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Admin.Controllers;

[Authorize(Policy = "SuperAdmin")]
public class StarterContentController : Controller
{
    private readonly IPRODbContext _db;
    public StarterContentController(IPRODbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        var pages = await _db.WebsiteStarterPages.AsNoTracking().Include(p => p.BillingRule).Include(p => p.Blocks)
            .OrderBy(p => p.BusinessType).ThenBy(p => p.BillingRuleId).ThenBy(p => p.SortOrder).ToListAsync();
        return View(pages);
    }

    public async Task<IActionResult> Create()
    {
        return View("Edit", new StarterPageEditViewModel
        {
            Page = new WebsiteStarterPage { BusinessType = "All", ShowInNavigation = true, IsActive = true },
            Packages = await PackagesAsync()
        });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var page = await _db.WebsiteStarterPages.Include(p => p.Blocks).FirstOrDefaultAsync(p => p.Id == id);
        if (page == null) return NotFound();
        page.Blocks = page.Blocks.OrderBy(b => b.SortOrder).ToList();
        return View(new StarterPageEditViewModel { Page = page, Packages = await PackagesAsync() });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePage(int id, string businessType, int? billingRuleId, string title, string slug,
        string navigationLabel, string metaTitle, string metaDescription, bool isHomePage, bool showInNavigation, bool isActive)
    {
        businessType = string.IsNullOrWhiteSpace(businessType) ? "All" : businessType.Trim();
        title = title?.Trim() ?? string.Empty;
        slug = NormalizeSlug(string.IsNullOrWhiteSpace(slug) ? title : slug);
        if (isHomePage) slug = "home";
        if (string.IsNullOrWhiteSpace(title))
        {
            TempData["Error"] = "Page title is required.";
            return id == 0 ? RedirectToAction(nameof(Create)) : RedirectToAction(nameof(Edit), new { id });
        }
        if (await _db.WebsiteStarterPages.AnyAsync(p => p.Id != id && p.BusinessType == businessType && p.BillingRuleId == billingRuleId && p.Slug == slug))
        {
            TempData["Error"] = "That business type and package already has a starter page with this URL slug.";
            return id == 0 ? RedirectToAction(nameof(Create)) : RedirectToAction(nameof(Edit), new { id });
        }

        var page = id == 0 ? null : await _db.WebsiteStarterPages.FirstOrDefaultAsync(p => p.Id == id);
        if (id != 0 && page == null) return NotFound();
        if (page == null)
        {
            page = new WebsiteStarterPage { SortOrder = await _db.WebsiteStarterPages.CountAsync(p => p.BusinessType == businessType && p.BillingRuleId == billingRuleId) };
            _db.WebsiteStarterPages.Add(page);
        }
        page.BusinessType = businessType;
        page.BillingRuleId = billingRuleId;
        page.Title = title;
        page.Slug = slug;
        page.NavigationLabel = string.IsNullOrWhiteSpace(navigationLabel) ? title : navigationLabel.Trim();
        page.MetaTitle = metaTitle?.Trim() ?? string.Empty;
        page.MetaDescription = metaDescription?.Trim() ?? string.Empty;
        page.IsHomePage = isHomePage;
        page.ShowInNavigation = showInNavigation;
        page.IsActive = isActive;
        page.UpdatedAt = DateTime.UtcNow;
        if (isHomePage)
        {
            var others = await _db.WebsiteStarterPages.Where(p => p.Id != page.Id && p.BusinessType == businessType && p.BillingRuleId == billingRuleId && p.IsHomePage).ToListAsync();
            foreach (var other in others) other.IsHomePage = false;
        }
        await _db.SaveChangesAsync();
        TempData["Success"] = "Starter page saved. It will be copied to agents who have not created website pages yet.";
        return RedirectToAction(nameof(Edit), new { id = page.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddBlock(int pageId, string blockType)
    {
        if (!await _db.WebsiteStarterPages.AnyAsync(p => p.Id == pageId)) return NotFound();
        if (!WebsiteBlockTypes.All.Contains(blockType)) blockType = WebsiteBlockTypes.Text;
        _db.WebsiteStarterBlocks.Add(new WebsiteStarterBlock
        {
            WebsiteStarterPageId = pageId, BlockType = blockType, Heading = DefaultHeading(blockType),
            Body = blockType == WebsiteBlockTypes.Services ? "Service one\nService two\nService three" : "Add starter content here.",
            SortOrder = await _db.WebsiteStarterBlocks.CountAsync(b => b.WebsiteStarterPageId == pageId), IsVisible = true
        });
        await _db.SaveChangesAsync();
        TempData["Success"] = "Starter content block added.";
        return RedirectToAction(nameof(Edit), new { id = pageId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateBlock(int id, string heading, string subheading, string body,
        string imageUrl, string buttonText, string buttonUrl, bool isVisible)
    {
        var block = await _db.WebsiteStarterBlocks.FirstOrDefaultAsync(b => b.Id == id);
        if (block == null) return NotFound();
        block.Heading = heading?.Trim() ?? string.Empty;
        block.Subheading = subheading?.Trim() ?? string.Empty;
        block.Body = body?.Trim() ?? string.Empty;
        block.ImageUrl = SafeUrl(imageUrl);
        block.ButtonText = buttonText?.Trim() ?? string.Empty;
        block.ButtonUrl = SafeLink(buttonUrl);
        block.IsVisible = isVisible;
        await _db.SaveChangesAsync();
        TempData["Success"] = "Starter block saved.";
        return RedirectToAction(nameof(Edit), new { id = block.WebsiteStarterPageId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteBlock(int id)
    {
        var block = await _db.WebsiteStarterBlocks.FirstOrDefaultAsync(b => b.Id == id);
        if (block == null) return NotFound();
        var pageId = block.WebsiteStarterPageId;
        _db.WebsiteStarterBlocks.Remove(block);
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Edit), new { id = pageId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var page = await _db.WebsiteStarterPages.FirstOrDefaultAsync(p => p.Id == id);
        if (page == null) return NotFound();
        _db.WebsiteStarterPages.Remove(page);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Starter page deleted. Existing agent pages were not changed.";
        return RedirectToAction(nameof(Index));
    }

    private Task<List<BillingRule>> PackagesAsync() => _db.BillingRules.AsNoTracking().OrderBy(p => p.PackageName).ToListAsync();
    private static string NormalizeSlug(string value) => Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
    private static string DefaultHeading(string type) => type switch { WebsiteBlockTypes.Hero => "Main headline", WebsiteBlockTypes.Services => "Services", WebsiteBlockTypes.CallToAction => "Ready to connect?", WebsiteBlockTypes.ContactForm => "Contact us", _ => "Content heading" };
    private static string SafeUrl(string? value) => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.ToString() : string.Empty;
    private static string SafeLink(string? value) { value = value?.Trim() ?? string.Empty; return value.StartsWith('/') ? value : SafeUrl(value); }
}
