using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// 500 (2026-09-18), found on the live preview minutes after 499 deployed. Production's Mortgage
// edition already held starter articles the OWNER had added in SuperAdmin -> Starter Content, which
// no code or test knew about -- among them "Buying a home?". 499's category "Buying a Home" therefore
// arrived as a second menu entry of almost the same name, and because an uncategorized article takes
// its slug first, the CATEGORY page of every new mortgage site was /buying-a-home-2: the Calculators
// wart of 497 again, made the same day it was fixed. The categories are renamed so they collide with
// nothing ("Home Buying Basics", "Managing Your Mortgage"), and the rows 499 had already written to
// production are renamed once, together with anything the owner filed beside them.
public class MortgageCategories500Tests
{
    private const string Mortgage = "Mortgage";

    [Fact]
    public async Task A_new_mortgage_site_has_no_numbered_slug_even_beside_the_owners_own_article()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        // What production holds and the code does not: the owner's own, uncategorized.
        db.WebsiteStarterArticles.AddRange(
            new WebsiteStarterArticle { BusinessType = Mortgage, Title = "Buying a home?", Summary = "The owner's own.", Content = "<p>For most people, a home is the largest purchase they will ever make.</p>", IsActive = true, SortOrder = 5 },
            new WebsiteStarterArticle { BusinessType = Mortgage, Title = "Using a Mortgage Professional", Summary = "The owner's own.", Content = "<p>Buyers have two basic choices.</p>", IsActive = true, SortOrder = 6 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var (agentId, website) = await SeedAgentWithWebsiteAsync(db);

        await WebsiteStarterPagesHelper.EnsureStarterPagesAsync(db, website, agentId);
        await WebsiteStarterResourcesHelper.EnsureResourcesAsync(db, website, agentId);
        db.ChangeTracker.Clear();

        var pages = await db.WebsitePages.AsNoTracking().Where(p => p.AgentWebsiteId == website.Id).ToListAsync();
        var numbered = pages.Where(p => Regex.IsMatch(p.Slug, @"-\d+$")).Select(p => $"{p.Title} -> /{p.Slug}").ToList();
        Assert.True(numbered.Count == 0, "a page of a brand-new site was given a numbered slug: " + string.Join("; ", numbered));

        // And no two entries of the Resources menu read as the same thing.
        var resources = pages.Single(p => p.Slug == "resources");
        static string Key(string title) => Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]", "");
        var menu = pages.Where(p => p.ParentPageId == resources.Id).Select(p => p.Title).ToList();
        Assert.Equal(menu.Count, menu.Select(Key).Distinct().Count());
        Assert.Contains("Home Buying Basics", menu);
        Assert.Contains("Managing Your Mortgage", menu);
        Assert.Contains("Buying a home?", menu);
    }

    [Fact]
    public async Task The_rows_499_wrote_to_production_are_renamed_once_with_whatever_was_filed_beside_them()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await WebsiteStarterArticleSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();

        // Production as 499 left it this evening: the library and the two older articles under the old
        // names -- plus one article the owner files under "Buying a Home" himself before this deploy.
        await db.WebsiteStarterArticles.Where(a => a.BusinessType == Mortgage && a.Category == "Home Buying Basics")
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.Category, "Buying a Home"));
        await db.WebsiteStarterArticles.Where(a => a.BusinessType == Mortgage && a.Category == "Managing Your Mortgage")
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.Category, "Owning a Home"));
        db.WebsiteStarterArticles.Add(new WebsiteStarterArticle { BusinessType = Mortgage, Title = "Mortgage Process Table", Category = "Buying a Home", Summary = "The owner's own.", Content = "<p>Step by step.</p>", IsActive = true });
        // Another edition using the same words is none of this repair's business.
        db.WebsiteStarterArticles.Add(new WebsiteStarterArticle { BusinessType = "Generic", Title = "Owning a Home Office", Category = "Owning a Home", Summary = "Not ours.", Content = "<p>x</p>", IsActive = true });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(7, await db.WebsiteStarterArticles.CountAsync(a => a.BusinessType == Mortgage && a.Category == "Buying a Home"));

        await WebsiteStarterArticleSeeder.SeedAsync(db);   // the next start-up
        db.ChangeTracker.Clear();

        var mortgage = await db.WebsiteStarterArticles.AsNoTracking().Where(a => a.BusinessType == Mortgage).ToListAsync();
        Assert.DoesNotContain(mortgage, a => a.Category == "Buying a Home" || a.Category == "Owning a Home");
        Assert.Equal(7, mortgage.Count(a => a.Category == "Home Buying Basics"));      // six of ours and the owner's one
        Assert.Equal(4, mortgage.Count(a => a.Category == "Managing Your Mortgage"));
        Assert.Equal(11, mortgage.Count);                                                // nothing added twice
        Assert.Equal("Owning a Home", (await db.WebsiteStarterArticles.AsNoTracking().SingleAsync(a => a.Title == "Owning a Home Office")).Category);
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

    private static async Task<(int AgentId, AgentWebsite Website)> SeedAgentWithWebsiteAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T500-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t500-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Nora", LastName = "Field", CompanyName = "Field Mortgages",
            DomainName = ($"t500-{Guid.NewGuid():N}")[..24],
            Country = "Canada", Province = "Ontario",
            PackageId = rule.Id, BusinessType = Mortgage
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
