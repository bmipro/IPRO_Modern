using System;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// 497 (2026-09-18), found by the truth sweep and confirmed on the live site: every accountant's
// Resources menu showed "Calculators" TWICE. The Accountants library files one article ("Which
// Calculator Do You Actually Need?") under a category called Calculators, and the provisioning code
// then built its own Calculators section for the real calculators beside it, at /calculators-2. The
// /accountants landing page frames that very menu. The calculators now go under the category that is
// already there; an edition with no such article category gets the section exactly as before. The
// real provisioning and the preview's zero-write mirror are fixed together, as they must be.
public class ResourcesMenu497Tests
{
    [Fact]
    public async Task An_accountants_site_has_one_calculators_entry_holding_the_guide_and_the_calculators()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var (agentId, website) = await SeedAgentWithWebsiteAsync(db, "Accountants");

        await WebsiteStarterPagesHelper.EnsureStarterPagesAsync(db, website, agentId);
        await WebsiteStarterResourcesHelper.EnsureResourcesAsync(db, website, agentId);
        db.ChangeTracker.Clear();

        var pages = await db.WebsitePages.AsNoTracking().Where(p => p.AgentWebsiteId == website.Id).ToListAsync();
        var resources = pages.Single(p => p.Slug == "resources");
        var calculators = pages.Where(p => p.ParentPageId == resources.Id && p.Title == "Calculators").ToList();
        Assert.Single(calculators);
        Assert.DoesNotContain(pages, p => p.Slug == "calculators-2");

        var children = pages.Where(p => p.ParentPageId == calculators[0].Id).OrderBy(p => p.SortOrder).ToList();
        var expected = VerticalCalculatorCatalog.ForBusinessType("Accountants").Count + 1;   // the guide article first
        Assert.Equal(expected, children.Count);
        Assert.Equal("Which Calculator Do You Actually Need?", children[0].Title);
        Assert.Equal(children.Count, children.Select(c => c.SortOrder).Distinct().Count());
    }

    [Fact]
    public async Task The_preview_shows_the_same_single_calculators_entry()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);

        var model = await ProspectWebsitePreviewBuilder.BuildAsync(db, new ProspectPreviewInput
        {
            FirstName = "Nora", LastName = "Field", CompanyName = "Field Accounting", BusinessType = "Accountants"
        });

        Assert.NotNull(model);
        var calculators = model!.Pages.Where(p => p.Title == "Calculators").ToList();
        Assert.Single(calculators);
        Assert.DoesNotContain(model.Pages, p => p.Slug == "calculators-2");
        var children = model.Pages.Where(p => p.ParentPageId == calculators[0].Id).ToList();
        Assert.Equal(VerticalCalculatorCatalog.ForBusinessType("Accountants").Count + 1, children.Count);
    }

    [Fact]
    public async Task An_edition_without_such_an_article_category_gets_its_calculators_section_as_before()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var (agentId, website) = await SeedAgentWithWebsiteAsync(db, "Mortgage");

        await WebsiteStarterPagesHelper.EnsureStarterPagesAsync(db, website, agentId);
        await WebsiteStarterResourcesHelper.EnsureResourcesAsync(db, website, agentId);
        db.ChangeTracker.Clear();

        var pages = await db.WebsitePages.AsNoTracking().Where(p => p.AgentWebsiteId == website.Id).ToListAsync();
        var calculators = Assert.Single(pages, p => p.Title == "Calculators");
        Assert.Equal("calculators", calculators.Slug);
        Assert.Equal(VerticalCalculatorCatalog.ForBusinessType("Mortgage").Count, pages.Count(p => p.ParentPageId == calculators.Id));
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
        var rule = new BillingRule { PackageName = ($"T497-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t497-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Nora", LastName = "Field", CompanyName = "Field Accounting",
            DomainName = ($"t497-{Guid.NewGuid():N}")[..24],
            Country = "Canada", Province = "Ontario",
            PackageId = rule.Id, BusinessType = businessType
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var template = await db.WebsiteTemplates.FirstAsync(t => t.TemplateKey == WebsiteTemplateSeeder.DefaultTemplateKey);
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, SiteTitle = "Field Accounting" };
        db.AgentWebsites.Add(website);
        await db.SaveChangesAsync();
        return (agent.Id, website);
    }
}
