using System;
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

// 501 (2026-09-19), from the owner's own test morning, two days before launch.
//
// (1) He signed up a Generic account and asked of its Resources menu: "do we need calculators for
//     generic?" No. A consultant's, a designer's or a contractor's site with "Loan Amortization" and
//     "Savings Goal Timeline" on it reads as a financial template with the labels changed -- the very
//     thing the Generic pack (495) exists to avoid -- and the eight articles fill Resources on their
//     own. Any adviser can still put any of the fourteen calculators on any page with the Calculator
//     block. The home page's general sentence ("Your site launches with ... Canadian calculators")
//     is narrowed so it stays true.
// (2) His welcome email linked the new site as http://, and showed a Username above a sentence that
//     said "You sign in with your email address" -- both work. The plain-text alternative was still
//     the legacy letter ("CONGRATULATIONS! ... one of the most exciting and unique set of tools
//     available on the Internet"), with no phone number and a promise of a training "session".
public class SmallPolish501Tests
{
    [Fact]
    public void The_generic_edition_ships_no_starter_calculators_and_the_financial_editions_keep_theirs()
    {
        Assert.Empty(VerticalCalculatorCatalog.ForBusinessType("Generic"));
        Assert.NotEmpty(VerticalCalculatorCatalog.ForBusinessType("Mortgage"));
        Assert.NotEmpty(VerticalCalculatorCatalog.ForBusinessType("Insurance / Financial"));
        Assert.NotEmpty(VerticalCalculatorCatalog.ForBusinessType("Accountants"));
    }

    [Fact]
    public async Task A_new_generic_site_has_no_calculators_entry_and_its_resources_are_the_articles()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var (agentId, website) = await SeedAgentWithWebsiteAsync(db, StarterBusinessTypes.Generic);

        await WebsiteStarterPagesHelper.EnsureStarterPagesAsync(db, website, agentId);
        await WebsiteStarterResourcesHelper.EnsureResourcesAsync(db, website, agentId);
        db.ChangeTracker.Clear();

        var pages = await db.WebsitePages.AsNoTracking().Include(p => p.Blocks).Where(p => p.AgentWebsiteId == website.Id).ToListAsync();
        Assert.DoesNotContain(pages, p => p.Title == "Calculators");
        Assert.DoesNotContain(pages.SelectMany(p => p.Blocks), b => b.BlockType == WebsiteBlockTypes.Calculator);

        var resources = pages.Single(p => p.Slug == "resources");
        var menu = pages.Where(p => p.ParentPageId == resources.Id).OrderBy(p => p.SortOrder).Select(p => p.Title).ToList();
        Assert.Equal(new[]
        {
            "How to Get the Most Out of Your First Meeting",
            "Questions Worth Asking Before You Choose an Advisor",
            "Working Together", "Practical Guides"
        }, menu);
    }

    [Fact]
    public async Task The_generic_preview_shows_the_same_menu()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);

        var model = await ProspectWebsitePreviewBuilder.BuildAsync(db, new ProspectPreviewInput
        {
            FirstName = "Nora", LastName = "Field", CompanyName = "Field Studio", BusinessType = StarterBusinessTypes.Generic
        });

        Assert.NotNull(model);
        Assert.DoesNotContain(model!.Pages, p => p.Title == "Calculators");
        Assert.Contains(model.Pages, p => p.Title == "Working Together");
        Assert.Contains(model.Pages, p => p.Title == "Practical Guides");
    }

    [Fact]
    public void The_home_page_promises_calculators_only_where_a_new_site_has_them()
    {
        var home = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\Index.cshtml"));
        Assert.DoesNotContain("form and Canadian calculators.", home);
        Assert.Contains("form, plus Canadian calculators on the financial editions.", home);
    }

    [Fact]
    public void The_welcome_email_links_the_site_securely_and_says_how_to_sign_in_in_both_versions()
    {
        var model = RegistrationWelcomeTemplate.Sample();
        model.TemporaryPassword = string.Empty;   // a self-signup: the adviser chose the password
        var html = RegistrationWelcomeTemplate.BuildHtml(model);
        var text = RegistrationWelcomeTemplate.BuildText(model);

        foreach (var body in new[] { html, text })
        {
            Assert.Contains("https://FirstnameLastname.245Advisers.com", body);
            Assert.DoesNotContain("http://", body);
            // The box shows a username and the sign-in page asks for one; the email address works too.
            Assert.Contains("You sign in with your username or your email address", body);
            Assert.Contains(PlatformContact.SupportPhone, body);
            Assert.Contains("subscription is active", body);
        }

        // The plain-text alternative says what the HTML says, not the legacy letter.
        Assert.DoesNotContain("CONGRATULATIONS", text);
        Assert.DoesNotContain("most exciting", text);
        Assert.DoesNotContain("training session", text);
        Assert.Contains("Username: FirstnameLastname", text);
        Assert.Contains("training@iProAdvisers.com", text);   // 510: the capitals are iPro, not IPro

        // An account made for the adviser by SuperAdmin still gets its temporary password, in both.
        var made = RegistrationWelcomeTemplate.Sample();
        Assert.Contains("Lastname", RegistrationWelcomeTemplate.BuildText(made));
        Assert.Contains("change this temporary password", RegistrationWelcomeTemplate.BuildText(made));
        Assert.Contains("change this temporary password", RegistrationWelcomeTemplate.BuildHtml(made));
    }

    private static async Task SeedStarterContentAsync(IPRODbContext db)
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
        var rule = new BillingRule { PackageName = ($"T501-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t501-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Nora", LastName = "Field", CompanyName = "Field Studio",
            DomainName = ($"t501-{Guid.NewGuid():N}")[..24],
            Country = "Canada", Province = "Ontario",
            PackageId = rule.Id, BusinessType = businessType
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var template = await db.WebsiteTemplates.FirstAsync(t => t.TemplateKey == WebsiteTemplateSeeder.DefaultTemplateKey);
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, SiteTitle = "Field Studio" };
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
