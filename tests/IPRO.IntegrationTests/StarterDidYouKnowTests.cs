using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 470 (2026-09-09). SuperAdmin could add a Did You Know block to a vertical's starter page but
// could not say which articles it shows, and a new agent received the block empty: the block stores
// the agent's own Article ids, which do not exist until the Resources section is built, and Home is
// provisioned before Resources. Now the starter block carries the chosen STARTER article ids; at
// provisioning the agent's Articles are created first (reused by title, the way Resources already
// does) and the block is filled with their real ids. Works for any business type created in
// SuperAdmin, and the prospect preview shows the same teasers.
public class StarterDidYouKnowTests
{
    [Fact]
    public async Task A_new_agents_did_you_know_block_arrives_filled_with_the_chosen_starter_articles()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var vertical = ($"T470-{Guid.NewGuid():N}")[..24];

        var rrsp = await SeedStarterArticleAsync(db, vertical, "Why RRSP timing matters", 0);
        var slips = await SeedStarterArticleAsync(db, vertical, "Tax slips checklist", 1);
        var retired = await SeedStarterArticleAsync(db, vertical, "A retired tip", 2, isActive: false);
        var starterPageId = await SeedHomePageWithDidYouKnowAsync(db, vertical,
            new WebsiteStarterDidYouKnowSettings { StarterArticleIds = { slips.Id, rrsp.Id, retired.Id }, LayoutStyle = "grid-2x3" });

        var agentId = await SeedAgentAsync(db, vertical);
        var website = await SeedWebsiteAsync(db, agentId);

        await WebsiteStarterPagesHelper.EnsureStarterPagesAsync(db, website, agentId);
        await WebsiteStarterResourcesHelper.EnsureResourcesAsync(db, website, agentId);
        db.ChangeTracker.Clear();

        var home = await db.WebsitePages.Include(p => p.Blocks).SingleAsync(p => p.AgentWebsiteId == website.Id && p.Slug == "home");
        var block = Assert.Single(home.Blocks, b => b.BlockType == WebsiteBlockTypes.DidYouKnow);
        var settings = WebsiteDidYouKnowSettings.FromJson(block.SettingsJson);

        var articles = await db.Articles.Where(a => a.AgentUserId == agentId).ToListAsync();
        var agentSlips = Assert.Single(articles, a => a.Title == "Tax slips checklist");
        var agentRrsp = Assert.Single(articles, a => a.Title == "Why RRSP timing matters");
        Assert.True(agentSlips.IsPublished);
        Assert.DoesNotContain(articles, a => a.Title == "A retired tip");

        // The block points at the agent's own articles, in the order SuperAdmin chose, minus the retired one.
        Assert.Equal(new[] { agentSlips.Id, agentRrsp.Id }, settings.ArticleIds);
        Assert.Equal("grid-2x3", settings.LayoutStyle);

        // Resources reused those same articles rather than creating a second copy, and its pages point at them.
        var resourceBlocks = (await db.WebsitePages.Include(p => p.Blocks).Where(p => p.AgentWebsiteId == website.Id).ToListAsync())
            .SelectMany(p => p.Blocks).Where(b => b.BlockType == WebsiteBlockTypes.ArticleContent).ToList();
        Assert.Contains(resourceBlocks, b => b.SettingsJson.Contains($"\"ArticleId\":{agentSlips.Id}"));
        Assert.Contains(resourceBlocks, b => b.SettingsJson.Contains($"\"ArticleId\":{agentRrsp.Id}"));
        Assert.Equal(1, articles.Count(a => a.Title == "Tax slips checklist"));
        Assert.True(starterPageId > 0);
    }

    [Fact]
    public async Task Superadmin_chooses_the_starter_articles_for_the_block_and_only_this_verticals_count()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var vertical = ($"T470-{Guid.NewGuid():N}")[..24];
        var other = ($"T470o-{Guid.NewGuid():N}")[..24];

        var first = await SeedStarterArticleAsync(db, vertical, "First", 0);
        var second = await SeedStarterArticleAsync(db, vertical, "Second", 1);
        var shared = await SeedStarterArticleAsync(db, "All", "Shared with every vertical", 2);
        var foreign = await SeedStarterArticleAsync(db, other, "Another vertical's article", 0);
        var retired = await SeedStarterArticleAsync(db, vertical, "Retired", 3, isActive: false);
        var pageId = await SeedHomePageWithDidYouKnowAsync(db, vertical, new WebsiteStarterDidYouKnowSettings());
        var blockId = await db.WebsiteStarterBlocks.Where(b => b.WebsiteStarterPageId == pageId && b.BlockType == WebsiteBlockTypes.DidYouKnow).Select(b => b.Id).SingleAsync();

        var controller = NewController(db);
        await controller.UpdateBlock(blockId, "Did you know?", "", "Body", "", "", "", true,
            new[] { second.Id, foreign.Id, shared.Id, first.Id, retired.Id, 999999 }, "grid-2x3");
        db.ChangeTracker.Clear();

        var block = await db.WebsiteStarterBlocks.SingleAsync(b => b.Id == blockId);
        var settings = WebsiteStarterDidYouKnowSettings.FromJson(block.SettingsJson);
        Assert.Equal(new[] { second.Id, shared.Id, first.Id }, settings.StarterArticleIds);
        Assert.Equal("grid-2x3", settings.LayoutStyle);
        Assert.Equal("Body", block.Body);
    }

    [Fact]
    public void The_starter_editor_offers_the_article_list_and_the_preview_shows_the_teasers()
    {
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\StarterContent\Edit.cshtml"));
        Assert.Contains("name=\"starterArticleIds\"", view);
        Assert.Contains("name=\"layoutStyle\"", view);

        var preview = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Infrastructure\ProspectWebsitePreviewBuilder.cs"));
        Assert.Contains("DidYouKnowByBlockId = ", preview);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static IPRO.Admin.Controllers.StarterContentController NewController(IPRODbContext db)
    {
        var controller = new IPRO.Admin.Controllers.StarterContentController(db, new NoAudit(), new ServiceCollection().BuildServiceProvider());
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Name, "admin") }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());
        return controller;
    }

    private static async Task<WebsiteStarterArticle> SeedStarterArticleAsync(IPRODbContext db, string businessType, string title, int sortOrder, bool isActive = true)
    {
        var article = new WebsiteStarterArticle
        {
            BusinessType = businessType, Title = title, Summary = $"{title} summary",
            Content = $"<p>{title} content that is long enough to make an excerpt from.</p>",
            IsActive = isActive, SortOrder = sortOrder
        };
        db.WebsiteStarterArticles.Add(article);
        await db.SaveChangesAsync();
        return article;
    }

    private static async Task<int> SeedHomePageWithDidYouKnowAsync(IPRODbContext db, string businessType, WebsiteStarterDidYouKnowSettings settings)
    {
        var page = new WebsiteStarterPage
        {
            BusinessType = businessType, Title = "Home", Slug = "home", NavigationLabel = "Home",
            IsHomePage = true, ShowInNavigation = true, IsActive = true, SortOrder = 0
        };
        page.Blocks.Add(new WebsiteStarterBlock { BlockType = WebsiteBlockTypes.Hero, Heading = "Welcome", Body = "Hello.", SortOrder = 0, IsVisible = true });
        page.Blocks.Add(new WebsiteStarterBlock
        {
            BlockType = WebsiteBlockTypes.DidYouKnow, Heading = "Did you know?", Body = "A few things worth knowing.",
            SettingsJson = settings.ToJson(), SortOrder = 1, IsVisible = true
        });
        db.WebsiteStarterPages.Add(page);
        await db.SaveChangesAsync();
        return page.Id;
    }

    private static async Task<int> SeedAgentAsync(IPRODbContext db, string businessType)
    {
        var rule = new BillingRule { PackageName = ($"T470-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t470-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Dyk", LastName = "Agent", CompanyName = "Dyk Co",
            DomainName = ($"t470-{Guid.NewGuid():N}")[..24],
            PackageId = rule.Id, BusinessType = businessType
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<AgentWebsite> SeedWebsiteAsync(IPRODbContext db, int agentId)
    {
        var template = new WebsiteTemplate { TemplateKey = ($"t470-{Guid.NewGuid():N}")[..16], Name = "T470", BusinessType = "Insurance" };
        db.Add(template);
        await db.SaveChangesAsync();
        var website = new AgentWebsite { AgentUserId = agentId, TemplateId = template.Id, SiteTitle = "Dyk Co" };
        db.AgentWebsites.Add(website);
        await db.SaveChangesAsync();
        return website;
    }

    private sealed class NoAudit : IAdminAuditLogService
    {
        public Task LogAsync(int adminUserId, string adminUsername, string action, string details) => Task.CompletedTask;
    }

    private sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
