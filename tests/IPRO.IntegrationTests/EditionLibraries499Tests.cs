using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// 499 (2026-09-18). The Insurance / Financial and Mortgage editions shipped with two starter
// articles apiece while Accountants had a full library and Generic had eight, and the public pages
// say the articles "are written for your market". Each now has its own eight (EditionStarterArticles).
// These are regulated subjects, so the rules are pinned here: general information and never advice,
// no figures of any kind, no promise of an outcome, a pre-approval never described as a commitment
// to lend, legal questions pointed to a lawyer, plain markup only. The two older articles of each
// edition are filed under the new categories so a new site's Resources menu is not a loose pile.
public class EditionLibraries499Tests
{
    private const string InsuranceFinancial = "Insurance / Financial";
    private const string Mortgage = "Mortgage";

    public static IEnumerable<object[]> Libraries() => new[]
    {
        new object[] { InsuranceFinancial, new[] { "Protection", "Planning" } },
        new object[] { Mortgage, new[] { "Buying a Home", "Owning a Home" } }
    };

    private static GenericStarterArticles.Entry[] Library(string businessType) =>
        businessType == Mortgage ? EditionStarterArticles.Mortgage : EditionStarterArticles.InsuranceFinancial;

    [Theory]
    [MemberData(nameof(Libraries))]
    public void Each_edition_has_eight_articles_in_two_categories_of_four(string businessType, string[] categories)
    {
        var library = Library(businessType);
        Assert.Equal(8, library.Length);
        Assert.Equal(categories, library.Select(a => a.Category).Distinct().ToArray());
        Assert.All(categories, c => Assert.Equal(4, library.Count(a => a.Category == c)));
        Assert.Equal(8, library.Select(a => a.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // "Calculators" as an article category is what doubled the accountants' menu (497).
        Assert.DoesNotContain(library, a => string.Equals(a.Category, "Calculators", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(Libraries))]
    public void Every_article_keeps_the_rules_for_regulated_subjects(string businessType, string[] categories)
    {
        Assert.NotEmpty(categories);
        var library = Library(businessType);
        Assert.NotEmpty(library);
        Assert.All(library, article =>
        {
            var tags = Regex.Matches(article.Content, @"</?([a-zA-Z0-9]+)").Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.All(tags, t => Assert.Contains(t, new[] { "p", "h3", "ul", "li", "strong" }));
            Assert.DoesNotMatch(@"<[a-zA-Z0-9]+\s", article.Content);   // no attributes: no links, styles or scripts

            var plain = Regex.Replace(article.Content, "<[^>]+>", " ");
            foreach (var text in new[] { plain, article.Summary, article.Title })
            {
                // No figures: a rate, a limit, a year or a price is stale within months and differs by province.
                Assert.DoesNotMatch(@"\d", text);
                Assert.DoesNotContain("%", text);
                Assert.DoesNotContain("$", text);
                Assert.DoesNotContain("!", text);
                Assert.DoesNotContain("guarantee", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("risk-free", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("you should buy", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("we recommend", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("401(k)", text, StringComparison.OrdinalIgnoreCase);     // Canadian terms only
                Assert.DoesNotContain(" IRA", text, StringComparison.Ordinal);
                Assert.True(text.All(ch => ch < 128), $"non-ASCII character in [{article.Title}]");
            }
            Assert.InRange(plain.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length, 350, 650);
            Assert.True(article.Summary.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 25, article.Title);
            Assert.True(article.Title.Length <= 120, article.Title);
        });
    }

    [Fact]
    public void A_pre_approval_is_never_a_commitment_and_legal_questions_go_to_a_lawyer()
    {
        var mentioning = EditionStarterArticles.Mortgage.Where(a => a.Content.Contains("pre-approval", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(mentioning);
        Assert.All(mentioning, a => Assert.Matches(@"(isn't|is not) a commitment to lend", a.Content));

        var beneficiary = EditionStarterArticles.InsuranceFinancial.Single(a => a.Title.StartsWith("Naming a Beneficiary", StringComparison.Ordinal));
        Assert.Contains("general information, not legal advice", beneficiary.Content);
        Assert.Contains("estate lawyer", beneficiary.Content);
        // Investing is never described without its risk.
        var risk = EditionStarterArticles.InsuranceFinancial.Single(a => a.Title.StartsWith("Understanding Risk Tolerance", StringComparison.Ordinal));
        Assert.Contains("past performance doesn't tell you what will happen next", risk.Content);
    }

    [Fact]
    public async Task The_seeder_adds_both_libraries_to_a_database_seeded_long_ago_and_files_the_older_articles_once()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await WebsiteStarterArticleSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();

        // Production before this deploy: no libraries, the four older articles uncategorized -- one of
        // them filed by the owner himself in SuperAdmin.
        var newTitles = EditionStarterArticles.InsuranceFinancial.Concat(EditionStarterArticles.Mortgage).Select(a => a.Title).ToList();
        Assert.Equal(16, newTitles.Count);
        await db.WebsiteStarterArticles.Where(a => newTitles.Contains(a.Title)).ExecuteDeleteAsync();
        await db.WebsiteStarterArticles.Where(a => a.BusinessType == InsuranceFinancial || a.BusinessType == Mortgage)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.Category, (string?)null));
        await db.WebsiteStarterArticles.Where(a => a.Title == "RRSP or TFSA: Which Should You Prioritize?")
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.Category, "The owner's own"));
        Assert.Equal(2, await db.WebsiteStarterArticles.CountAsync(a => a.BusinessType == Mortgage));

        await WebsiteStarterArticleSeeder.SeedAsync(db);   // the next start-up
        db.ChangeTracker.Clear();

        var articles = await db.WebsiteStarterArticles.AsNoTracking().ToListAsync();
        Assert.Equal(10, articles.Count(a => a.BusinessType == InsuranceFinancial));
        Assert.Equal(10, articles.Count(a => a.BusinessType == Mortgage));
        Assert.Equal("Protection", articles.Single(a => a.Title == "Do You Actually Have Enough Life Insurance?").Category);
        Assert.Equal("The owner's own", articles.Single(a => a.Title == "RRSP or TFSA: Which Should You Prioritize?").Category);
        Assert.Equal("Buying a Home", articles.Single(a => a.Title == "Fixed or Variable: Choosing the Right Mortgage Rate").Category);
        Assert.Equal("Buying a Home", articles.Single(a => a.Title == "What First-Time Buyers Should Know About Pre-Approval").Category);
        // The older two read first inside their category.
        Assert.All(articles.Where(a => newTitles.Contains(a.Title)), a => Assert.True(a.SortOrder >= 10));

        // Filed ONCE, in the pass that brings the library: a category blanked afterwards stays blank.
        await db.WebsiteStarterArticles.Where(a => a.Title == "Do You Actually Have Enough Life Insurance?")
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.Category, (string?)null));
        await WebsiteStarterArticleSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();
        Assert.Null((await db.WebsiteStarterArticles.AsNoTracking().SingleAsync(a => a.Title == "Do You Actually Have Enough Life Insurance?")).Category);
        Assert.Equal(20, await db.WebsiteStarterArticles.CountAsync(a => a.BusinessType == InsuranceFinancial || a.BusinessType == Mortgage));
    }

    [Fact]
    public async Task A_new_mortgage_site_files_its_library_under_two_menu_entries()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var (agentId, website) = await SeedAgentWithWebsiteAsync(db, Mortgage);

        await WebsiteStarterPagesHelper.EnsureStarterPagesAsync(db, website, agentId);
        await WebsiteStarterResourcesHelper.EnsureResourcesAsync(db, website, agentId);
        db.ChangeTracker.Clear();

        var pages = await db.WebsitePages.AsNoTracking().Where(p => p.AgentWebsiteId == website.Id).ToListAsync();
        var resources = pages.Single(p => p.Slug == "resources");
        var menu = pages.Where(p => p.ParentPageId == resources.Id).OrderBy(p => p.SortOrder).Select(p => p.Title).ToList();
        Assert.Equal(new[]
        {
            "How to Get the Most Out of Your First Meeting",
            "Questions Worth Asking Before You Choose an Advisor",
            "Buying a Home", "Owning a Home", "Calculators"
        }, menu);

        var buying = pages.Single(p => p.ParentPageId == resources.Id && p.Title == "Buying a Home");
        var buyingChildren = pages.Where(p => p.ParentPageId == buying.Id).OrderBy(p => p.SortOrder).Select(p => p.Title).ToList();
        Assert.Equal(6, buyingChildren.Count);
        Assert.Equal("Fixed or Variable: Choosing the Right Mortgage Rate", buyingChildren[0]);
        Assert.Equal("How Much Home Can You Comfortably Afford?", buyingChildren[2]);
        var owning = pages.Single(p => p.ParentPageId == resources.Id && p.Title == "Owning a Home");
        Assert.Equal(4, pages.Count(p => p.ParentPageId == owning.Id));
        // Every article is a real, editable Article row of the adviser's own.
        Assert.Equal(12, await db.Articles.CountAsync(a => a.AgentUserId == agentId));
    }

    [Fact]
    public async Task The_insurance_preview_shows_the_same_two_entries()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);

        var model = await ProspectWebsitePreviewBuilder.BuildAsync(db, new ProspectPreviewInput
        {
            FirstName = "Nora", LastName = "Field", CompanyName = "Field Financial", BusinessType = InsuranceFinancial
        });

        Assert.NotNull(model);
        var protection = Assert.Single(model!.Pages, p => p.Title == "Protection");
        var planning = Assert.Single(model.Pages, p => p.Title == "Planning");
        Assert.Equal(5, model.Pages.Count(p => p.ParentPageId == protection.Id));
        Assert.Equal(5, model.Pages.Count(p => p.ParentPageId == planning.Id));
        Assert.Single(model.Pages, p => p.Title == "Calculators");
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
        var rule = new BillingRule { PackageName = ($"T499-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t499-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Nora", LastName = "Field", CompanyName = "Field Mortgages",
            DomainName = ($"t499-{Guid.NewGuid():N}")[..24],
            Country = "Canada", Province = "Ontario",
            PackageId = rule.Id, BusinessType = businessType
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var template = await db.WebsiteTemplates.FirstAsync(t => t.TemplateKey == WebsiteTemplateSeeder.DefaultTemplateKey);
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, SiteTitle = "Field Mortgages" };
        db.AgentWebsites.Add(website);
        await db.SaveChangesAsync();
        return (agent.Id, website);
    }
}
