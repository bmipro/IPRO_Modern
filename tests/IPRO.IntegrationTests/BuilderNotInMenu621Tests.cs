using System.Threading.Tasks;
using IPRO.Business.SiteImport;
using IPRO.Entities;
using Xunit;

namespace IPRO.IntegrationTests;

// 621: the owner's test of 614 found Photo Gallery at the end of the builder's menu, and not on his site or in
// Preview. The hand-over sends every page and "show in menu" stays on iPro's side, so the builder put them all in
// its menu. iPro now names the pages its own menu leaves out when a session opens.
public class BuilderNotInMenu621Tests
{
    [Fact]
    public async Task The_builder_is_told_the_pages_the_sites_own_menu_leaves_out()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await BuilderImport571Tests.SeedSiteAsync(db);
        var before = await BuilderBeta.NotInMenuAsync(db, site.Id);

        db.WebsitePages.AddRange(
            new WebsitePage { AgentWebsiteId = site.Id, Title = "In the menu", Slug = "t621-shown", SortOrder = 90 },
            new WebsitePage { AgentWebsiteId = site.Id, Title = "Out of the menu", Slug = "t621-hidden", SortOrder = 91, ShowInNavigation = false },
            new WebsitePage { AgentWebsiteId = site.Id, Title = "Not published", Slug = "t621-unpublished", SortOrder = 92, IsPublished = false });
        await db.SaveChangesAsync();

        var after = await BuilderBeta.NotInMenuAsync(db, site.Id);
        Assert.Contains("t621-hidden", after);
        Assert.Contains("t621-unpublished", after);
        Assert.DoesNotContain("t621-shown", after);
        Assert.Equal(before.Count + 2, after.Count);
    }

    // The rule above is the live menu's: shown in the menu, of the pages published.
    [Fact]
    public void It_is_the_rule_of_the_live_menu_and_the_session_carries_it()
    {
        Assert.Contains("var navPages = Model.Pages.Where(p => p.ShowInNavigation).ToList();",
            Lock583.Read(@"src\IPRO.Web\Views\PublicWebsite\_PublicNavigation.cshtml"));
        Assert.Contains(".Where(p => p.AgentWebsiteId == website.Id && p.IsPublished)",
            Lock583.Read(@"src\IPRO.Web\Controllers\PublicWebsiteController.cs"));
        Assert.Contains("notInMenu = await BuilderBeta.NotInMenuAsync(_db, granted.Site.Id),",
            Lock583.Read(@"src\IPRO.Web\Controllers\BuilderApiController.cs"));
    }
}
