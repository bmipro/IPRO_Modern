using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests.Accessibility;

// 578 (2026-10-15). Audit finding 5 (WCAG 1.1.1): every hero, text, article and blog picture was
// published as decoration and nothing could describe it. A library picture is now described once, in
// the page editor, and says so wherever it is used; an article's picture has its own description.
// Audit finding 9: the website's language is set in My Website and a page's own on the page.
public class PictureAndLanguageSettings578Tests
{
    // ---------------------------------------------------------------- pictures on the page --

    [Theory]
    [InlineData(PublicSitePages.Modern)]
    [InlineData(PublicSitePages.Classic)]
    [InlineData(PublicSitePages.Editorial)]
    public async Task A_described_picture_says_what_it_shows_and_an_undescribed_one_stays_decoration(string design)
    {
        var site = PublicSitePages.Build(design);
        var home = site.Pages.Single(p => p.Slug == "home");
        home.Blocks.Single(b => b.BlockType == WebsiteBlockTypes.Hero).ImageUrl = "team.png";
        home.Blocks.Single(b => b.BlockType == WebsiteBlockTypes.Text).ImageUrl = "chart.png";
        var model = site.Model("home");
        model.PictureDescriptions["team.png"] = "Dana and two colleagues outside the King Street office";
        var html = await PublicSitePages.RenderAsync(model);

        Assert.Contains("src=\"team.png\" alt=\"Dana and two colleagues outside the King Street office\"", html);
        Assert.Contains("src=\"chart.png\" alt=\"\"", html);
    }

    [Theory]
    [InlineData(PublicSitePages.Modern)]
    [InlineData(PublicSitePages.Classic)]
    [InlineData(PublicSitePages.Editorial)]
    public async Task An_articles_picture_carries_its_description_in_the_article_and_the_blog(string design)
    {
        var site = PublicSitePages.Build(design);
        site.Articles.Values.Single().ImageDescription = "A couple reviewing their tax return";
        site.Blogs.Values.Single().Posts.Single().ImageDescription = "The federal budget document";
        var html = await PublicSitePages.RenderAsync(site.Model("resources"));

        Assert.Contains("alt=\"A couple reviewing their tax return\"", html);
        Assert.Contains("alt=\"The federal budget document\"", html);
    }

    [Theory]
    [InlineData("  Our   team\n outside ", "Our team outside")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void A_description_is_one_line_of_at_most_300_characters(string? typed, string kept)
    {
        Assert.Equal(kept, PictureDescriptions.Clean(typed));
        Assert.Equal(300, PictureDescriptions.Clean(new string('a', 400)).Length);
    }

    // ---------------------------------------------------------------- the page editor --

    [Fact]
    public async Task A_library_picture_is_described_once_and_shown_by_every_block_that_uses_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId, pageId) = await SiteAsync(db);
        var asset = new WebsiteMediaAsset { AgentWebsiteId = websiteId, OriginalFileName = "team.jpg", BlobUrl = "https://blob.example.test/website-media/team.jpg", ContentType = "image/jpeg" };
        db.Add(asset);
        db.Add(new WebsiteContentBlock { WebsitePageId = pageId, BlockType = WebsiteBlockTypes.Hero, ImageUrl = asset.BlobUrl, IsVisible = true });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await PagesController(db, agentId).DescribeImage(asset.Id, pageId, "  Dana and   two colleagues \n outside ");
        Assert.Equal("image-library", Assert.IsType<RedirectToActionResult>(result).Fragment);

        db.ChangeTracker.Clear();
        Assert.Equal("Dana and two colleagues outside", (await db.WebsiteMediaAssets.SingleAsync(a => a.Id == asset.Id)).Description);
        var page = await db.WebsitePages.Include(p => p.Blocks).SingleAsync(p => p.Id == pageId);
        var found = await PictureDescriptions.ForPageAsync(db, websiteId, page);
        Assert.Equal("Dana and two colleagues outside", found[asset.BlobUrl]);
    }

    [Fact]
    public async Task Only_the_websites_own_pictures_can_be_described_or_looked_up()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId, pageId) = await SiteAsync(db);
        var (_, otherWebsiteId, otherPageId) = await SiteAsync(db);
        var theirs = new WebsiteMediaAsset { AgentWebsiteId = otherWebsiteId, OriginalFileName = "x.jpg", BlobUrl = "https://blob.example.test/website-media/x.jpg", ContentType = "image/jpeg", Description = "Their picture" };
        db.Add(theirs);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.IsType<NotFoundResult>(await PagesController(db, agentId).DescribeImage(theirs.Id, pageId, "mine now"));
        Assert.IsType<NotFoundResult>(await PagesController(db, agentId).DescribeImage(theirs.Id, otherPageId, "mine now"));
        db.ChangeTracker.Clear();
        Assert.Equal("Their picture", (await db.WebsiteMediaAssets.SingleAsync(a => a.Id == theirs.Id)).Description);

        // A page of this website pointing at the other website's picture does not borrow its words.
        var page = new WebsitePage { AgentWebsiteId = websiteId, Blocks = { new WebsiteContentBlock { BlockType = WebsiteBlockTypes.Text, ImageUrl = theirs.BlobUrl, IsVisible = true } } };
        Assert.Empty(await PictureDescriptions.ForPageAsync(db, websiteId, page));
    }

    [Fact]
    public async Task A_page_keeps_its_own_language_and_its_duplicate_takes_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, _, pageId) = await SiteAsync(db);

        await PagesController(db, agentId).SavePage(pageId, "Accueil", "accueil", "Accueil", "", "", null, true, true, false, pageLanguage: "fr");
        db.ChangeTracker.Clear();
        Assert.Equal("fr", (await db.WebsitePages.SingleAsync(p => p.Id == pageId)).Language);

        // A save that does not send it (an older form) leaves it alone.
        await PagesController(db, agentId).SavePage(pageId, "Accueil", "accueil", "Accueil", "", "", null, true, true, false);
        db.ChangeTracker.Clear();
        Assert.Equal("fr", (await db.WebsitePages.SingleAsync(p => p.Id == pageId)).Language);

        await PagesController(db, agentId).Duplicate(pageId);
        db.ChangeTracker.Clear();
        Assert.Equal("fr", (await db.WebsitePages.SingleAsync(p => p.Slug == "accueil-copy")).Language);

        // "Same as the website" posts "site": an empty value would bind as null, which means "not sent".
        Assert.Contains("<option value=\"site\"", System.IO.File.ReadAllText(System.IO.Path.Combine(PublicSitePages.RepoRoot(), "src", "IPRO.Web", "Views", "WebsitePages", "Edit.cshtml")));
        await PagesController(db, agentId).SavePage(pageId, "Accueil", "accueil", "Accueil", "", "", null, true, true, false, pageLanguage: "site");
        db.ChangeTracker.Clear();
        Assert.Equal("", (await db.WebsitePages.SingleAsync(p => p.Id == pageId)).Language);
    }

    [Fact]
    public async Task My_Website_saves_the_websites_language()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId, _) = await SiteAsync(db);
        var templateId = await db.AgentWebsites.Where(w => w.Id == websiteId).Select(w => w.TemplateId).SingleAsync();

        await WebsiteController(db, agentId).Save(Posted(templateId, "fr"), logo: null);
        db.ChangeTracker.Clear();
        Assert.Equal("fr", (await db.AgentWebsites.SingleAsync(w => w.Id == websiteId)).SiteLanguage);

        await WebsiteController(db, agentId).Save(Posted(templateId, "klingon"), logo: null);
        db.ChangeTracker.Clear();
        Assert.Equal("en", (await db.AgentWebsites.SingleAsync(w => w.Id == websiteId)).SiteLanguage);
    }

    // ---------------------------------------------------------------- plumbing --

    private static AgentWebsite Posted(int templateId, string language) => new()
    {
        TemplateId = templateId, SiteTitle = "Mine", TagLine = "A line", ThemeColor = "#224466", SiteLanguage = language,
        CustomDomain = string.Empty, CustomDomainDisplay = string.Empty, LogoUrl = string.Empty
    };

    private static async Task<(int AgentId, int WebsiteId, int PageId)> SiteAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"L578-{Guid.NewGuid():N}")[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        var template = new WebsiteTemplate { Name = "Modern", TemplateKey = ($"l578-{Guid.NewGuid():N}")[..16], BusinessType = "All", IsActive = true };
        db.AddRange(rule, template);
        await db.SaveChangesAsync();
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.InstantWebsite, FeatureName = "Website", IsIncluded = true });
        var agent = new AgentUser
        {
            UserName = ($"l578-{Guid.NewGuid():N}")[..20], Email = ($"l578-{Guid.NewGuid():N}")[..14] + "@example.test",
            FirstName = "Dana", LastName = "Rivers", CompanyName = "Rivers Financial", DomainName = ($"l578-{Guid.NewGuid():N}")[..24],
            Country = "Canada", Province = "Ontario", PackageId = rule.Id, TrialEndsAt = null
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        db.Add(new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 60m, Status = BillingStatus.Active,
            Period = BillingPeriod.Monthly, StartDate = DateTime.UtcNow.AddDays(-40), NextBillingDate = DateTime.UtcNow.AddDays(20)
        });
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, SiteTitle = "Mine" };
        db.Add(website);
        await db.SaveChangesAsync();
        var page = new WebsitePage { AgentWebsiteId = website.Id, Title = "Home", Slug = "home", NavigationLabel = "Home", IsHomePage = true };
        db.Add(page);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (agent.Id, website.Id, page.Id);
    }

    private static WebsitePagesController PagesController(IPRODbContext db, int agentId)
    {
        var controller = new WebsitePagesController(db, new PackageEntitlementService(new UnitOfWork(db), db), new NullBlob());
        Sign(controller, agentId);
        return controller;
    }

    private static WebsiteController WebsiteController(IPRODbContext db, int agentId)
    {
        var controller = new WebsiteController(
            new WebsiteService(new UnitOfWork(db)), new NullBlob(), new PackageEntitlementService(new UnitOfWork(db), db),
            new AgentService(new UnitOfWork(db), new PasswordHasher<AgentUser>()), new ConfigurationBuilder().Build(), db,
            null!, null!, NullLogger<WebsiteController>.Instance);
        Sign(controller, agentId);
        return controller;
    }

    private static void Sign(Controller controller, int agentId)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString(CultureInfo.InvariantCulture)) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, new NullTempData());
    }

    private sealed class NullBlob : IPRO.Utility.IBlobStorageService
    {
        public Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate) => Task.FromResult($"https://blob.example.test/{containerName}/{fileName}");
        public Task<bool> DeleteAsync(string blobUrl) => Task.FromResult(true);
        public Task<Stream?> DownloadAsync(string blobUrl) => Task.FromResult<Stream?>(null);
        public Task<List<string>> ListAsync(string containerName) => Task.FromResult(new List<string>());
        public string GetPublicUrl(string containerName, string fileName) => $"https://blob.example.test/{containerName}/{fileName}";
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => Task.CompletedTask;
    }

    private sealed class NullTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
