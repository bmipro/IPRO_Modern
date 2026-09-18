using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// 494 (2026-09-18). The home page sells four starting points -- Insurance / Financial, Accountants,
// Mortgage and GENERIC ("the flexible starting point for any professional-service business") -- and
// both landing-page footers link to it, but sign-up, the profile, the 30-second preview and the
// SuperAdmin agent editor each offered a hard-coded list of three. A visitor who chose Generic had
// nowhere to go; worse, the preview silently turned an unlisted type into Accountants.
//
// A neutral fallback was already in the product: an "All" set of starter pages, two "All" forms,
// an "All" meeting form and two "All" articles, which every vertical falls back to. 494 made Generic
// the business type that takes that set; 495 then gave it a pack written for it (its own six pages,
// eight articles and two forms -- see GenericStarterPackTests), which these tests now expect.
//
// What 494 itself added was the option, in one list the four forms share so they cannot drift from
// the marketing page again -- and a fix for the two places where a type without its own copy was
// shown another vertical's (the daily-assistant preview said "her life insurance policy review is
// 4 days overdue" to everyone; the fallback calculators led with a mortgage payment).
public class GenericEditionTests
{
    // ---- the one list -----------------------------------------------------------------------

    [Fact]
    public void Generic_is_a_business_type_the_product_offers_with_a_label_that_explains_itself()
    {
        Assert.Contains("Generic", StarterBusinessTypes.Known);

        var offered = StarterBusinessTypes.Offered;
        Assert.Equal(new[] { "Accountants", "Insurance / Financial", "Mortgage", "Generic" }, offered.Select(o => o.Value).ToArray());
        Assert.Equal(StarterBusinessTypes.Known, offered.Select(o => o.Value).ToArray());
        // The three verticals read as themselves; "Generic" alone would not tell a visitor what it is.
        Assert.All(offered.Where(o => o.Value != "Generic"), o => Assert.Equal(o.Value, o.Label));
        Assert.Equal("Generic (any other business)", offered.Single(o => o.Value == "Generic").Label);

        // The preview validates against the same list, not a copy of it.
        Assert.Equal(StarterBusinessTypes.Known, ProspectPreviewInput.ValidBusinessTypes);
    }

    [Theory]
    [InlineData(@"src\IPRO.Web\Views\Account\Register.cshtml")]
    [InlineData(@"src\IPRO.Web\Views\Account\Profile.cshtml")]
    [InlineData(@"src\IPRO.Web\Views\Preview\Index.cshtml")]
    [InlineData(@"src\IPRO.Admin\Views\Agents\Edit.cshtml")]
    public void Every_form_that_asks_for_a_business_type_renders_the_one_list(string file)
    {
        var view = File.ReadAllText(FindRepoFile(file));
        Assert.Contains("StarterBusinessTypes.Offered", view);
        // No hard-coded copy left behind to drift.
        Assert.DoesNotContain("<option>Mortgage</option>", view);
        Assert.DoesNotContain("<option>Accountants</option>", view);
    }

    [Fact]
    public void The_preview_keeps_a_generic_prospect_generic_and_an_unknown_type_falls_back_to_it()
    {
        Assert.Equal("Generic", new ProspectPreviewInput { BusinessType = "Generic" }.Normalized().BusinessType);
        // It used to become "Accountants": a stranger's business is not an accounting practice.
        Assert.Equal("Generic", new ProspectPreviewInput { BusinessType = "Landscaping" }.Normalized().BusinessType);
        Assert.Equal("Generic", new ProspectPreviewInput { BusinessType = "" }.Normalized().BusinessType);
        Assert.Equal("Mortgage", new ProspectPreviewInput { BusinessType = "Mortgage" }.Normalized().BusinessType);
    }

    // ---- nobody is shown another vertical's copy --------------------------------------------

    [Fact]
    public void The_daily_assistant_preview_speaks_each_business_type_s_own_language()
    {
        var kinds = new[] { "OverdueFollowUp", "StaleLead", "NoFollowUp" };
        string Text(string type) => string.Join(" ", kinds.Select(k => MockDailyInsightCatalog.Get(type, k)).Select(e => e.ActionText + " " + e.ActionReason));

        var generic = Text("Generic");
        foreach (var word in new[] { "insurance", "policy", "mortgage", "filing", "tax", "renewal", "coverage" })
            Assert.DoesNotContain(word, generic, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("insurance", Text("Accountants"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("insurance", Text("Mortgage"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("insurance", Text("Insurance / Financial"), StringComparison.OrdinalIgnoreCase);

        // Four types, four different openings; a type nobody wrote copy for gets the neutral one.
        var openings = StarterBusinessTypes.Known.Select(t => MockDailyInsightCatalog.Get(t).ActionText).ToList();
        Assert.Equal(4, openings.Distinct().Count());
        Assert.Equal(MockDailyInsightCatalog.Get("Generic").ActionText, MockDailyInsightCatalog.Get("Landscaping").ActionText);
    }

    [Fact]
    public void The_generic_resources_carry_neutral_calculators()
    {
        var kinds = VerticalCalculatorCatalog.ForBusinessType("Generic").Select(c => c.Kind).ToArray();
        Assert.Equal(new[] { CalculatorKinds.LoanAmortization, CalculatorKinds.SavingsGrowth, CalculatorKinds.SavingsGoal }, kinds);
    }

    // ---- what a Generic adviser actually gets -----------------------------------------------

    [Fact]
    public async Task A_generic_adviser_is_provisioned_a_neutral_website_from_the_shared_starter_content()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedProductStarterContentAsync(db);
        var (agentId, website) = await SeedGenericAgentWithWebsiteAsync(db);

        await WebsiteStarterPagesHelper.EnsureStarterPagesAsync(db, website, agentId);
        await WebsiteStarterResourcesHelper.EnsureResourcesAsync(db, website, agentId);
        db.ChangeTracker.Clear();

        var pages = await db.WebsitePages.AsNoTracking().Include(p => p.Blocks)
            .Where(p => p.AgentWebsiteId == website.Id).ToListAsync();
        var topLevel = pages.Where(p => p.ParentPageId == null).Select(p => p.Slug).ToList();
        foreach (var slug in new[] { "home", "about", "contact", "testimonials", "free-newsletter", "request-meeting", "resources" })
            Assert.Contains(slug, topLevel);

        var hero = pages.Single(p => p.IsHomePage).Blocks.OrderBy(b => b.SortOrder).First();
        Assert.Equal("Good work starts with a conversation.", hero.Heading);   // 495: the Generic pack's own home page

        // Nothing on the starter pages speaks another vertical's language.
        var starterText = string.Join(" ", pages
            .Where(p => p.ParentPageId == null && p.Slug != "resources")
            .SelectMany(p => p.Blocks)
            .SelectMany(b => new[] { b.Heading, b.Subheading, b.Body }));
        foreach (var word in new[] { "insurance", "mortgage", "accounting", "bookkeeping", "retirement", "refinanc" })
            Assert.DoesNotContain(word, starterText, StringComparison.OrdinalIgnoreCase);

        // The Request Meeting page carries the shared meeting form, copied to the adviser.
        Assert.True(await db.WebsiteForms.AnyAsync(f => f.AgentUserId == agentId));
        // Resources: the two shared articles, the Generic library's eight (495), and the three neutral calculators.
        var calculatorKinds = pages.SelectMany(p => p.Blocks)
            .Where(b => b.BlockType == WebsiteBlockTypes.Calculator)
            .Select(b => WebsiteCalculatorSettings.FromJson(b.SettingsJson).CalculatorKind).OrderBy(k => k).ToArray();
        Assert.Equal(new[] { CalculatorKinds.LoanAmortization, CalculatorKinds.SavingsGoal, CalculatorKinds.SavingsGrowth }, calculatorKinds);
        Assert.Equal(10, await db.Articles.CountAsync(a => a.AgentUserId == agentId));
    }

    [Fact]
    public async Task The_thirty_second_preview_builds_a_generic_site()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedProductStarterContentAsync(db);

        var model = await ProspectWebsitePreviewBuilder.BuildAsync(db, new ProspectPreviewInput
        {
            FirstName = "Sam", LastName = "Lee", CompanyName = "Lee Consulting", BusinessType = "Generic"
        });

        Assert.NotNull(model);
        Assert.Equal("Generic", model!.Website.AgentUser.BusinessType);
        Assert.Equal(WebsiteTemplateSeeder.DefaultTemplateKey, model.Website.Template.TemplateKey);
        var hero = model.Pages.Single(p => p.IsHomePage).Blocks.OrderBy(b => b.SortOrder).First();
        Assert.Equal("Good work starts with a conversation.", hero.Heading);   // 495: the Generic pack's own home page
    }

    // ---- harness ----------------------------------------------------------------------------

    // The REAL seeders, the same ones both apps run at start-up: the point of these tests is that the
    // content production already holds is enough for a Generic adviser.
    private static async Task SeedProductStarterContentAsync(IPRODbContext db)
    {
        await WebsiteTemplateSeeder.SeedAsync(db);
        await WebsiteStarterContentSeeder.SeedAsync(db);
        await WebsiteStarterContentSeeder.SeedNavV2AdditionsAsync(db);
        await WebsiteStarterContentSeeder.SeedGenericEditionAsync(db);   // 495
        await WebsiteStarterFormSeeder.SeedAsync(db);
        await WebsiteStarterArticleSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();
    }

    private static async Task<(int AgentId, AgentWebsite Website)> SeedGenericAgentWithWebsiteAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T494-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t494-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Sam", LastName = "Lee", CompanyName = "Lee Consulting",
            DomainName = ($"t494-{Guid.NewGuid():N}")[..24],
            Country = "Canada", Province = "Ontario",
            PackageId = rule.Id, BusinessType = "Generic"
        };
        db.Add(agent);
        await db.SaveChangesAsync();

        var template = await db.WebsiteTemplates.FirstAsync(t => t.TemplateKey == WebsiteTemplateSeeder.DefaultTemplateKey);
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, SiteTitle = "Lee Consulting" };
        db.AgentWebsites.Add(website);
        await db.SaveChangesAsync();
        return (agent.Id, website);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "IPRO.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
