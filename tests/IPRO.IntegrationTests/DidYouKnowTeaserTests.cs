using System;
using System.IO;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 479 (2026-09-11). Checking /accountants, the owner asked for the Did You Know block as 3 x 2
// on both landing pages. The layout exists per block (Grid, 2 rows x 3 columns); what stopped it
// showing was the stylesheet: the stack rule sat in the 800 px phone block, and the landing pages'
// preview frame is about 620 px wide on a desktop, so the grid collapsed to one column inside it.
// Seen in the same frame: a teaser reading a literal "&mdash;" -- the excerpt stripped the tags from
// the article body but left its HTML entities in, and the page encodes the excerpt again.
public class DidYouKnowTeaserTests
{
    [Fact]
    public void The_teaser_excerpt_reads_entities_as_the_characters_they_stand_for()
    {
        var block = new WebsiteContentBlock
        {
            Id = 1,
            BlockType = WebsiteBlockTypes.DidYouKnow,
            IsVisible = true,
            SettingsJson = new WebsiteDidYouKnowSettings { ArticleIds = { 7 }, LayoutStyle = "grid-2x3" }.ToJson()
        };
        var article = new Article
        {
            Id = 7,
            Title = "Personal Tax Preparation",
            IsPublished = true,
            Content = "<p>We make sure you&rsquo;re claiming everything you&rsquo;re entitled to &mdash; not just the obvious &amp; easy credits.</p>"
        };

        var data = DidYouKnowBuilder.Build(new[] { block }, new[] { article });

        var teaser = Assert.Single(data[1].Teasers);
        Assert.Equal("We make sure you’re claiming everything you’re entitled to — not just the obvious & easy credits.", teaser.Excerpt);
        Assert.Equal("grid-2x3", data[1].LayoutStyle);
    }

    [Fact]
    public void The_grid_keeps_its_three_columns_inside_the_landing_pages_preview_frame()
    {
        var styles = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\PublicWebsite\_ManagedPageStyles.cshtml"));
        const string stack = ".dyk-teasers.dyk-grid-2x3 { grid-template-columns: 1fr; }";

        // The stack rule exists once, on its own phone-only query, and is gone from the 800 px block.
        Assert.Equal(1, styles.Split(stack).Length - 1);
        Assert.Contains("@@media (max-width: 559px) { " + stack + " }", styles);
        var phoneBlock = styles.IndexOf("@@media (max-width: 800px)", StringComparison.Ordinal);
        Assert.True(phoneBlock >= 0, "the 800 px block is where the other phone rules live");
        Assert.DoesNotContain(stack, styles[phoneBlock..]);
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
