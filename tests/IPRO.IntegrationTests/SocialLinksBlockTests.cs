using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 465 (2026-09-08). "Social media integration" turned out to be a misnomer for Social Posts;
// what the owner wanted was a website BLOCK. The agent's social profiles already live in one
// place -- My Website > Footer -- and render as icons in the footer. The Social links block shows
// the same links in the page body, larger, as icons or icons with names. One source of truth: no
// second place to type a URL.
public class SocialLinksBlockTests
{
    private const string Footer = """
        {"SocialLinks":[
          {"Platform":"linkedin","Url":"https://www.linkedin.com/in/example","SortOrder":2},
          {"Platform":"facebook","Url":"https://facebook.com/example","SortOrder":1},
          {"Platform":"twitter-x","Url":"https://x.com/example","SortOrder":3},
          {"Platform":"other","Url":"https://example.test","SortOrder":4},
          {"Platform":"youtube","Url":"javascript:alert(1)","SortOrder":5},
          {"Platform":"instagram","Url":"instagram.com/no-scheme","SortOrder":6}
        ]}
        """;

    [Fact]
    public void Links_come_from_the_footer_in_order_with_icons_and_names_and_only_safe_urls()
    {
        var data = SocialLinksBlock.Resolve(Footer, layoutVariant: null);

        Assert.Equal("icons", data.Style);
        Assert.Equal(new[] { "facebook", "linkedin", "twitter-x", "other" }, data.Links.Select(l => l.Platform).ToArray());
        Assert.Equal("fab fa-facebook", data.Links[0].IconClass);
        Assert.Equal("Facebook", data.Links[0].Label);
        Assert.Equal("fab fa-x-twitter", data.Links[2].IconClass);
        Assert.Equal("X", data.Links[2].Label);
        Assert.Equal("fas fa-link", data.Links[3].IconClass);
        Assert.Equal("Website", data.Links[3].Label);
        // javascript: and scheme-less entries are dropped, never rendered as hrefs.
        Assert.DoesNotContain(data.Links, l => l.Platform == "youtube" || l.Platform == "instagram");
    }

    [Fact]
    public void The_layout_variant_chooses_icons_or_names()
    {
        Assert.Equal("labels", SocialLinksBlock.Resolve(Footer, "labels").Style);
        Assert.Equal("icons", SocialLinksBlock.Resolve(Footer, "icons").Style);
        Assert.Equal("icons", SocialLinksBlock.Resolve(Footer, "banana").Style);
        Assert.Empty(SocialLinksBlock.Resolve(null, "labels").Links);
        Assert.Empty(SocialLinksBlock.Resolve("not json", "labels").Links);
    }

    [Fact]
    public void The_footer_and_the_block_share_one_icon_map()
    {
        Assert.Equal("fab fa-linkedin", SocialPlatformIcons.IconClass("linkedin"));
        Assert.Equal("fab fa-youtube", SocialPlatformIcons.IconClass("YouTube"));
        Assert.Equal("fas fa-link", SocialPlatformIcons.IconClass("other"));
        Assert.Equal("fas fa-link", SocialPlatformIcons.IconClass(""));
        Assert.Equal("LinkedIn", SocialPlatformIcons.Label("linkedin"));

        var footer = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\PublicWebsite\_PublicFooterContent.cshtml"));
        Assert.Contains("SocialPlatformIcons", footer);
        Assert.DoesNotContain("[\"facebook\"] = \"fa-facebook\"", footer);
    }

    [Fact]
    public void The_block_type_exists_every_template_renders_it_and_the_editor_offers_it()
    {
        Assert.Contains(WebsiteBlockTypes.SocialLinks, WebsiteBlockTypes.All);
        Assert.Equal("Social links", WebsiteBlockTypes.DisplayName(WebsiteBlockTypes.SocialLinks));

        foreach (var shell in new[] { "_ModernManagedPage.cshtml", "_ClassicManagedPage.cshtml", "_EditorialManagedPage.cshtml" })
        {
            var src = File.ReadAllText(FindRepoFile(Path.Combine(@"src\IPRO.Web\Views\PublicWebsite", shell)));
            Assert.Contains("WebsiteBlockTypes.SocialLinks", src);
            Assert.Contains("_PublicSocialLinks", src);
        }

        var partial = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\PublicWebsite\_PublicSocialLinks.cshtml"));
        Assert.Contains("rel=\"noopener noreferrer\"", partial);
        Assert.Contains("target=\"_blank\"", partial);
        Assert.Contains("aria-label", partial);
        Assert.Contains("My Website", partial); // the owner-preview nudge when the footer has no links

        var edit = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\WebsitePages\Edit.cshtml"));
        Assert.Contains("WebsiteBlockTypes.SocialLinks", edit);
        Assert.Contains("Icons with names", edit);
    }

    [Fact]
    public async Task Every_package_advertises_the_block()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        await PackageEntitlementSeeder.SeedAsync(db);

        var rows = await db.PackageFeatures.AsNoTracking().Where(f => f.FeatureCode == PackageFeatureCodes.SocialLinksBlock).ToListAsync();
        Assert.True(rows.Count >= 4, "the social links block must be a row on every seeded package");
        Assert.All(rows, r => Assert.True(r.IsIncluded));
        Assert.All(rows, r => Assert.Equal("Social links on your website", r.FeatureName));
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
