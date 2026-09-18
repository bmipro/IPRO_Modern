using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// 495 (2026-09-18). 494 made Generic a business type that takes the shared "All" starter set. The
// owner then asked for what the other three editions have: content written FOR it. The shared set
// was only ever a fallback -- its home page reads "Professional professional services with
// responsive, personal service" because the vertical name is substituted into a sentence that
// already says "professional". So the Generic edition now ships its own six starter pages, a library
// of eight articles in two categories (written for the clients of ANY professional-service
// business: no industry, no regulated advice, no figures), and two forms (a quote request and a
// new-client intake). All of it is editable afterwards under SuperAdmin -> Starter Content.
//
// And the home page's starting-point panel, which sells the four editions, finally has buttons:
// "Start with this edition" opens sign-up with that business type chosen, "Preview it first" opens
// the 30-second preview the same way.
public class GenericStarterPackTests
{
    private const string GenericHero = "Good work starts with a conversation.";
    private static readonly string[] OtherVerticalsWords =
        { "insurance", "mortgage", "retirement", "bookkeeping", "refinanc", "premium", "RRSP", "TFSA" };

    // ---- the pages --------------------------------------------------------------------------

    [Fact]
    public async Task The_generic_pages_are_seeded_once_and_a_generic_adviser_gets_them_instead_of_the_shared_set()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedEverythingAsync(db);
        await WebsiteStarterContentSeeder.SeedGenericEditionAsync(db);   // a second start-up: no duplicates
        db.ChangeTracker.Clear();

        var pages = await db.WebsiteStarterPages.AsNoTracking().Include(p => p.Blocks)
            .Where(p => p.BusinessType == StarterBusinessTypes.Generic).ToListAsync();
        Assert.Equal(new[] { "about", "contact", "free-newsletter", "home", "request-meeting", "testimonials" },
            pages.Select(p => p.Slug).OrderBy(s => s).ToArray());
        Assert.All(pages, p => Assert.True(p.IsActive));
        Assert.Single(pages, p => p.IsHomePage);

        var text = string.Join(" ", pages.SelectMany(p => p.Blocks).SelectMany(b => new[] { b.Heading, b.Subheading, b.Body, b.ButtonText }));
        Assert.DoesNotContain("Professional professional", text);
        foreach (var word in OtherVerticalsWords) Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);

        // A Generic adviser is provisioned from this set...
        var (genericAgent, genericSite) = await SeedAgentWithWebsiteAsync(db, StarterBusinessTypes.Generic);
        await WebsiteStarterPagesHelper.EnsureStarterPagesAsync(db, genericSite, genericAgent);
        // ...and an accountant from theirs, exactly as before.
        var (accountant, accountantSite) = await SeedAgentWithWebsiteAsync(db, "Accountants");
        await WebsiteStarterPagesHelper.EnsureStarterPagesAsync(db, accountantSite, accountant);
        db.ChangeTracker.Clear();

        Assert.Equal(GenericHero, await HeroHeadingAsync(db, genericSite.Id));
        Assert.Equal("Build confidence in your financial records and decisions", await HeroHeadingAsync(db, accountantSite.Id));
    }

    // ---- the library and the forms ----------------------------------------------------------

    [Fact]
    public async Task The_generic_library_and_forms_arrive_through_the_per_item_seeders_on_a_database_already_seeded()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedEverythingAsync(db);

        async Task AssertPackAsync()
        {
            var articles = await db.WebsiteStarterArticles.AsNoTracking().Where(a => a.BusinessType == StarterBusinessTypes.Generic).ToListAsync();
            Assert.Equal(8, articles.Count);
            Assert.Equal(new[] { "Practical Guides", "Working Together" }, articles.Select(a => a.Category).Distinct().OrderBy(c => c).ToArray());
            Assert.All(articles.GroupBy(a => a.Category), g => Assert.Equal(4, g.Count()));
            var forms = await db.WebsiteStarterForms.AsNoTracking().Where(f => f.BusinessType == StarterBusinessTypes.Generic).Select(f => f.Title).OrderBy(t => t).ToListAsync();
            Assert.Equal(new[] { "New Client Intake", "Request a Quote" }, forms);
        }
        await AssertPackAsync();

        // Production's tables were full before this pack existed: take the pack out and start up again.
        await db.WebsiteStarterArticles.Where(a => a.BusinessType == StarterBusinessTypes.Generic).ExecuteDeleteAsync();
        var genericFormIds = await db.WebsiteStarterForms.Where(f => f.BusinessType == StarterBusinessTypes.Generic).Select(f => f.Id).ToListAsync();
        db.WebsiteStarterForms.RemoveRange(await db.WebsiteStarterForms.Where(f => genericFormIds.Contains(f.Id)).ToListAsync());
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await WebsiteStarterArticleSeeder.SeedAsync(db);
        await WebsiteStarterFormSeeder.SeedAsync(db);
        await AssertPackAsync();
        await WebsiteStarterArticleSeeder.SeedAsync(db);
        await WebsiteStarterFormSeeder.SeedAsync(db);
        await AssertPackAsync();   // and never twice

        // What a Generic adviser's Resources section then holds: the two shared articles and the eight.
        var (agentId, website) = await SeedAgentWithWebsiteAsync(db, StarterBusinessTypes.Generic);
        await WebsiteStarterPagesHelper.EnsureStarterPagesAsync(db, website, agentId);
        await WebsiteStarterResourcesHelper.EnsureResourcesAsync(db, website, agentId);
        Assert.Equal(10, await db.Articles.CountAsync(a => a.AgentUserId == agentId));
    }

    [Fact]
    public void Every_generic_article_obeys_the_house_rules()
    {
        var articles = GenericStarterArticles.All;
        Assert.Equal(8, articles.Length);
        Assert.Equal(8, articles.Select(a => a.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var allowedTags = new[] { "p", "h3", "ul", "li", "strong" };
        foreach (var article in articles)
        {
            var tags = Regex.Matches(article.Content, @"</?([a-zA-Z0-9]+)").Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct();
            Assert.All(tags, tag => Assert.Contains(tag, allowedTags));
            Assert.DoesNotMatch(@"<[a-zA-Z0-9]+\s[^>]*>", article.Content);             // no attributes, so no styles, classes or links
            var words = Regex.Replace(article.Content, "<[^>]+>", " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            Assert.InRange(words, 300, 600);
            Assert.InRange(article.Summary.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length, 6, 25);

            // True for ANY professional-service business: no other edition's subject, no figures, no years.
            var everything = article.Title + " " + article.Summary + " " + article.Content;
            foreach (var word in OtherVerticalsWords) Assert.DoesNotContain(word, everything, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(@"[%$]|\b(19|20)\d\d\b", everything);
            Assert.DoesNotContain("guarantee", everything, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(@"src\IPRO.Web\Program.cs")]
    [InlineData(@"src\IPRO.Admin\Program.cs")]
    public void Both_apps_seed_the_generic_pages_at_start_up_after_the_shared_sets(string file)
    {
        var src = File.ReadAllText(FindRepoFile(file));
        var navV2 = src.IndexOf("WebsiteStarterContentSeeder.SeedNavV2AdditionsAsync(db", StringComparison.Ordinal);
        var generic = src.IndexOf("WebsiteStarterContentSeeder.SeedGenericEditionAsync(db", StringComparison.Ordinal);
        Assert.True(navV2 > 0 && generic > navV2, "the Generic pages are seeded after the shared sets, in both apps");
    }

    // ---- the home page's buttons ------------------------------------------------------------

    [Fact]
    public void The_starting_point_panel_has_buttons_that_carry_the_chosen_edition()
    {
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\Index.cshtml"));
        Assert.Contains("id=\"i2-market-start\"", view);
        Assert.Contains("id=\"i2-market-preview\"", view);
        Assert.Contains("Start with this edition", view);

        // The four tabs name the product's four business types, exactly -- the page is where this drifted.
        var types = Regex.Matches(view, @"\btype: '([^']+)'").Select(m => m.Groups[1].Value).OrderBy(t => t).ToArray();
        Assert.Equal(StarterBusinessTypes.Known.OrderBy(t => t).ToArray(), types);
        // And switching tabs re-points both buttons.
        Assert.Contains("'/Account/Register?businessType=' + ", view);
        Assert.Contains("'/Preview?businessType=' + ", view);
    }

    [Fact]
    public void The_preview_opens_with_the_edition_the_visitor_came_for()
    {
        var controller = new IPRO.Web.Controllers.PreviewController(null!);
        controller.Index(package: null, businessType: "Generic");
        Assert.Equal("Generic", (string?)controller.ViewBag.CarriedBusinessType);
        controller.Index(package: null, businessType: "Insurance / Financial");
        Assert.Equal("Insurance / Financial", (string?)controller.ViewBag.CarriedBusinessType);
        controller.Index(package: null, businessType: "Landscaping");
        Assert.Null((string?)controller.ViewBag.CarriedBusinessType);

        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Preview\Index.cshtml"));
        Assert.Contains("CarriedBusinessType", view);
        Assert.Contains("selected=\"@(", view);
    }

    // ---- harness ----------------------------------------------------------------------------

    private static async Task SeedEverythingAsync(IPRODbContext db)
    {
        await WebsiteTemplateSeeder.SeedAsync(db);
        await WebsiteStarterContentSeeder.SeedAsync(db);
        await WebsiteStarterContentSeeder.SeedNavV2AdditionsAsync(db);
        await WebsiteStarterContentSeeder.SeedGenericEditionAsync(db);
        await WebsiteStarterFormSeeder.SeedAsync(db);
        await WebsiteStarterArticleSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();
    }

    private static async Task<(int AgentId, AgentWebsite Website)> SeedAgentWithWebsiteAsync(IPRODbContext db, string businessType)
    {
        var rule = new BillingRule { PackageName = ($"T495-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t495-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Sam", LastName = "Lee", CompanyName = "Lee Consulting",
            DomainName = ($"t495-{Guid.NewGuid():N}")[..24],
            Country = "Canada", Province = "Ontario",
            PackageId = rule.Id, BusinessType = businessType
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var template = await db.WebsiteTemplates.FirstAsync(t => t.TemplateKey == WebsiteTemplateSeeder.DefaultTemplateKey);
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, SiteTitle = "Lee Consulting" };
        db.AgentWebsites.Add(website);
        await db.SaveChangesAsync();
        return (agent.Id, website);
    }

    private static async Task<string> HeroHeadingAsync(IPRODbContext db, int websiteId)
    {
        var home = await db.WebsitePages.AsNoTracking().Include(p => p.Blocks)
            .SingleAsync(p => p.AgentWebsiteId == websiteId && p.IsHomePage);
        return home.Blocks.OrderBy(b => b.SortOrder).First().Heading;
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "IPRO.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
