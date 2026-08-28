using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// The Blog block + AI drafting (owner decision 2026-08-28). The ManagedBlog feature stops being
// the never-delivered "one unique blog per month written and managed" service and becomes a real
// product feature: a block that lists the agent's own published articles, with a ?post= inline
// view (no new route -- the /portal collision cost four fixes; see WebsiteBlogSettings), plus a
// Draft-with-AI button on the article editor gated by the ONE shared AI flag.
public class BlogFeatureTests
{
    // ------------------------------------------------------------------ the public listing --

    [Fact]
    public async Task Blog_block_lists_only_this_agents_published_articles_newest_first()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedBlogSiteAsync(db, postCount: 6);

        // Noise the listing must exclude: this agent's DRAFT, and another agent's PUBLISHED post.
        await AddArticleAsync(db, seed.AgentId, "My draft", published: false, daysAgo: 0);
        var other = await SeedBlogSiteAsync(db, postCount: 6);
        await AddArticleAsync(db, other.AgentId, "Someone else's post", published: true, daysAgo: 0);

        var controller = NewPublicController(db, seed.Host);
        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PublicWebsiteViewModel>(view.Model);
        var blog = Assert.Single(model.BlogByBlockId).Value;

        Assert.Equal(new[] { "Newest post", "Middle post", "Oldest post" },
            blog.Posts.Select(p => p.Title).ToArray());
        Assert.Null(blog.SelectedPost);
    }

    [Fact]
    public async Task Blog_selected_post_must_be_this_agents_and_published()
    {
        // ?post= is visitor-controlled input. A guessed id belonging to another agent -- or to an
        // unpublished draft -- must resolve to nothing, leaving the ordinary list.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedBlogSiteAsync(db, postCount: 6);
        var draftId = await AddArticleAsync(db, seed.AgentId, "Unpublished", published: false, daysAgo: 0);
        var other = await SeedBlogSiteAsync(db, postCount: 6);
        var foreignId = await AddArticleAsync(db, other.AgentId, "Foreign", published: true, daysAgo: 0);

        foreach (var badId in new[] { draftId, foreignId, 999_999_999 })
        {
            var controller = NewPublicController(db, seed.Host, post: badId);
            var view = Assert.IsType<ViewResult>(await controller.Index());
            var blog = Assert.Single(Assert.IsType<PublicWebsiteViewModel>(view.Model).BlogByBlockId).Value;
            Assert.Null(blog.SelectedPost);
            Assert.NotEmpty(blog.Posts);
        }

        // And the agent's own published post DOES resolve.
        var ownId = (await db.Articles.SingleAsync(a => a.Title == "Newest post" && a.AgentUserId == seed.AgentId)).Id;
        var okController = NewPublicController(db, seed.Host, post: ownId);
        var okView = Assert.IsType<ViewResult>(await okController.Index());
        var okBlog = Assert.Single(Assert.IsType<PublicWebsiteViewModel>(okView.Model).BlogByBlockId).Value;
        Assert.Equal(ownId, okBlog.SelectedPost?.Id);
    }

    [Fact]
    public void Blog_post_count_is_clamped_against_hand_edited_settings()
    {
        // The public page must never be talked into thousands of rows by a crafted settings JSON.
        Assert.Equal(WebsiteBlogSettings.MaxPostCount, new WebsiteBlogSettings { PostCount = 5000 }.EffectivePostCount);
        Assert.Equal(WebsiteBlogSettings.MinPostCount, new WebsiteBlogSettings { PostCount = -3 }.EffectivePostCount);
        Assert.Equal(6, WebsiteBlogSettings.FromJson(null).EffectivePostCount);
        Assert.Equal(6, WebsiteBlogSettings.FromJson("not json at all").EffectivePostCount);
    }

    // ----------------------------------------------------------------------- editor gating --

    [Fact]
    public async Task Adding_a_blog_block_requires_the_managed_blog_feature()
    {
        // Gated in the ACTION, not only the picker -- a crafted POST reaches AddBlock directly,
        // the same hole M2 closed on RebuildRequestMeeting.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var silver = await SeedBlogSiteAsync(db, postCount: 6, includeBlogFeature: false);

        // The seed puts one Blog block on every page (an agent can lose the feature after adding
        // one), so the refused AddBlock leaves the count AT one -- the proof is no NEW block.
        var controller = NewPagesController(db, silver.AgentId);
        await controller.AddBlock(silver.PageId, WebsiteBlockTypes.Blog);
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.WebsiteContentBlocks.CountAsync(
            b => b.WebsitePageId == silver.PageId && b.BlockType == WebsiteBlockTypes.Blog));

        var platinum = await SeedBlogSiteAsync(db, postCount: 6);   // includes the feature
        var okController = NewPagesController(db, platinum.AgentId);
        await okController.AddBlock(platinum.PageId, WebsiteBlockTypes.Blog);
        db.ChangeTracker.Clear();
        // The seed already put one Blog block on the page; AddBlock adds a second.
        Assert.Equal(2, await db.WebsiteContentBlocks.CountAsync(
            b => b.WebsitePageId == platinum.PageId && b.BlockType == WebsiteBlockTypes.Blog));
    }

    // -------------------------------------------------------------------- the AI drafting --

    [Fact]
    public async Task Draft_with_ai_is_gated_by_the_shared_flag_and_never_saves_anything()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedBlogSiteAsync(db, postCount: 6, includeAiFeature: false);
        var ai = new RecordingAi();

        // Without AiDailyAssistant: refused, and the AI service is never even called.
        var locked = NewArticlesController(db, seed.AgentId, ai);
        var lockedResult = Assert.IsType<JsonResult>(await locked.DraftWithAi("tax season"));
        Assert.Contains("\"success\":false", System.Text.Json.JsonSerializer.Serialize(lockedResult.Value));
        Assert.Equal(0, ai.Calls);

        // With it: the draft comes back, and NOTHING is saved -- the agent is the author of
        // record, so the button only fills the form.
        var entitled = await SeedBlogSiteAsync(db, postCount: 6);
        var open = NewArticlesController(db, entitled.AgentId, ai);
        var okResult = Assert.IsType<JsonResult>(await open.DraftWithAi("tax season"));
        var json = System.Text.Json.JsonSerializer.Serialize(okResult.Value);
        Assert.Contains("\"success\":true", json);
        Assert.Contains("Drafted title", json);
        Assert.Equal(1, ai.Calls);
        db.ChangeTracker.Clear();
        // The seed creates three published articles; the draft must not have added a fourth --
        // the button fills the form, the agent saves.
        Assert.Equal(3, await db.Articles.CountAsync(a => a.AgentUserId == entitled.AgentId));

        // Usage is recorded against the shared AI ledger.
        Assert.True(await db.AiUsageDailyLogs.AnyAsync(l => l.OutputTokens > 0));
    }

    [Fact]
    public void The_blog_draft_parse_survives_a_model_that_ignores_the_format()
    {
        var full = AnthropicAiSuggestionService.ParseBlogDraft(
            "TITLE: Five RESP questions\nSUMMARY: What parents ask most.\nBODY:\n<p>First...</p>", 10, 20);
        Assert.Equal("Five RESP questions", full.Title);
        Assert.Equal("What parents ask most.", full.Summary);
        Assert.Equal("<p>First...</p>", full.BodyHtml);

        // No SUMMARY line: title still parses, summary null.
        var noSummary = AnthropicAiSuggestionService.ParseBlogDraft(
            "TITLE: Just a title\nBODY:\n<p>Body.</p>", 1, 2);
        Assert.Equal("Just a title", noSummary.Title);
        Assert.Null(noSummary.Summary);

        // Format ignored entirely: everything lands in the body for the agent to salvage, never
        // thrown away.
        var freeform = AnthropicAiSuggestionService.ParseBlogDraft("Here is an article about RESPs...", 1, 2);
        Assert.Null(freeform.Title);
        Assert.Equal("Here is an article about RESPs...", freeform.BodyHtml);
    }

    // ------------------------------------------------------------------------- shell parity --

    [Fact]
    public void All_three_page_shells_render_the_blog_block()
    {
        // The "fixed one app, not both" failure class, view edition: a block added to Modern only
        // would silently vanish for agents on Classic or Editorial.
        foreach (var shell in new[] { "_ModernManagedPage", "_ClassicManagedPage", "_EditorialManagedPage" })
        {
            var razor = File.ReadAllText(FindRepoFile($@"src\IPRO.Web\Views\PublicWebsite\{shell}.cshtml"));
            Assert.True(razor.Contains("WebsiteBlockTypes.Blog"), $"{shell} has no Blog branch");
            Assert.True(razor.Contains("?post=@post.Id"), $"{shell} has no read-more link");
            Assert.True(razor.Contains("All posts"), $"{shell} has no back-to-list link");
        }
    }

    // ------------------------------------------------------------------------------ plumbing --

    private sealed record Seed(int AgentId, int WebsiteId, int PageId, string Host);

    private static async Task<Seed> SeedBlogSiteAsync(
        IPRODbContext db, int postCount, bool includeBlogFeature = true, bool includeAiFeature = true)
    {
        var rule = new BillingRule { PackageName = $"BL-{Guid.NewGuid():N}"[..20], MonthlyPrice = 90m, AnnualPrice = 900m };
        db.Add(rule);
        await db.SaveChangesAsync();
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.Newsletters, FeatureName = "Newsletters", IsIncluded = true });
        if (includeBlogFeature)
            db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.ManagedBlog, FeatureName = "Blog", IsIncluded = true });
        if (includeAiFeature)
            db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.AiDailyAssistant, FeatureName = "AI", IsIncluded = true });

        var host = $"bl-{Guid.NewGuid():N}"[..14] + ".example.test";
        var agent = new AgentUser
        {
            UserName = $"bl-{Guid.NewGuid():N}"[..20],
            Email = $"bl-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Blog", LastName = "Owner",
            DomainName = $"bl-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario"
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        agent.PackageId = rule.Id;
        db.Add(new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 90m,
            Status = BillingStatus.Active, Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddDays(-10), NextBillingDate = DateTime.UtcNow.AddDays(20)
        });

        var template = new WebsiteTemplate { TemplateKey = $"tk-{Guid.NewGuid():N}"[..16], Name = "T", BusinessType = "All" };
        db.Add(template);
        await db.SaveChangesAsync();
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, IsPublished = true, CustomDomain = host };
        db.Add(website);
        await db.SaveChangesAsync();

        var page = new WebsitePage
        {
            AgentWebsiteId = website.Id, Title = "Blog", Slug = "blog",
            IsPublished = true, IsHomePage = true, SortOrder = 0
        };
        db.Add(page);
        await db.SaveChangesAsync();
        db.Add(new WebsiteContentBlock
        {
            WebsitePageId = page.Id, BlockType = WebsiteBlockTypes.Blog,
            Heading = "From the blog", SortOrder = 0, IsVisible = true,
            SettingsJson = new WebsiteBlogSettings { PostCount = postCount }.ToJson()
        });

        // Three published posts with distinct publish dates, oldest first so ordering is proven.
        await db.SaveChangesAsync();
        await AddArticleAsync(db, agent.Id, "Oldest post", published: true, daysAgo: 30);
        await AddArticleAsync(db, agent.Id, "Middle post", published: true, daysAgo: 15);
        await AddArticleAsync(db, agent.Id, "Newest post", published: true, daysAgo: 1);
        db.ChangeTracker.Clear();
        return new Seed(agent.Id, website.Id, page.Id, host);
    }

    private static async Task<int> AddArticleAsync(IPRODbContext db, int agentId, string title, bool published, int daysAgo)
    {
        var article = new Article
        {
            AgentUserId = agentId, Title = title, Summary = $"{title} summary",
            Content = $"<p>{title} body</p>", IsPublished = published,
            PublishedAt = published ? DateTime.UtcNow.AddDays(-daysAgo) : null,
            CreatedAt = DateTime.UtcNow.AddDays(-daysAgo), UpdatedAt = DateTime.UtcNow.AddDays(-daysAgo)
        };
        db.Add(article);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return article.Id;
    }

    private static PublicWebsiteController NewPublicController(IPRODbContext db, string host, int? post = null)
    {
        var controller = new PublicWebsiteController(
            db, new PackageEntitlementService(new UnitOfWork(db), db), new NullEmail(),
            NullLogger<PublicWebsiteController>.Instance, new ConfigurationBuilder().Build(),
            DataProtectionProvider.Create($"blog-tests-{Guid.NewGuid():N}"), new NullBlob(),
            new MemoryCache(new MemoryCacheOptions()), new NullConsent());
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        if (post.HasValue) context.Request.QueryString = new QueryString($"?post={post.Value}");
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NullTempData());
        return controller;
    }

    private static WebsitePagesController NewPagesController(IPRODbContext db, int agentId)
    {
        var controller = new WebsitePagesController(db, new PackageEntitlementService(new UnitOfWork(db), db), new NullBlob());
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NullTempData());
        return controller;
    }

    private static ArticlesController NewArticlesController(IPRODbContext db, int agentId, IAiSuggestionService ai)
    {
        var controller = new ArticlesController(db, new PackageEntitlementService(new UnitOfWork(db), db), new NullBlob(), ai);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NullTempData());
        return controller;
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }

    private sealed class RecordingAi : IAiSuggestionService
    {
        public int Calls;
        public Task<AiActionReasonResult> GenerateActionReasonAsync(string situation, CancellationToken cancellationToken = default)
            => Task.FromResult(AiActionReasonResult.Empty);
        public Task<AiActionReasonResult> DraftSocialPostAsync(string topic, CancellationToken cancellationToken = default)
            => Task.FromResult(AiActionReasonResult.Empty);
        public Task<AiNewsletterDraftResult> DraftNewsletterAsync(string topic, CancellationToken cancellationToken = default)
            => Task.FromResult(AiNewsletterDraftResult.Empty);
        public Task<AiBlogPostDraftResult> DraftBlogPostAsync(string topic, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new AiBlogPostDraftResult("Drafted title", "Drafted summary", "<p>Drafted body</p>", 300, 900));
        }
    }

    private sealed class NullEmail : IPRO.Email.IEmailService
    {
        public Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) => Task.FromResult(true);
        public Task<IPRO.Email.EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) => Task.FromResult(IPRO.Email.EmailSendResult.Sent());
        public Task<bool> SendBulkAsync(IEnumerable<IPRO.Email.EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
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

    private sealed class NullConsent : IEmailConsentService
    {
        public bool IsSuppressed(Client client, EmailChannel channel, bool designSurvivesOptOut = false) => false;
        public Task<SuppressionResult> SuppressAllAsync(Client client, string source) => throw new NotSupportedException();
        public Task ResubscribeAsync(Client client) => throw new NotSupportedException();
        public Task<int> CancelSuppressedDripEnrollmentsAsync(int batchLimit = 500) => Task.FromResult(0);
        public Task<string> GetOrCreateTokenAsync(Client client) => Task.FromResult("tok");
        public string BuildPreferencesUrl(string token) => $"https://example.test/prefs/{token}";
    }

    private sealed class NullTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
