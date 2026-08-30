using System;
using System.IO;
using System.Linq;
using Xunit;

namespace IPRO.IntegrationTests;

// Phase 3, mkt-shots (2026-08-30). The homepage hero carried an interactive portal mock whose
// panels were HAND-BUILT HTML impressions of the product -- fictional clients, invented stats
// ("Showing 5 of 128"), a fake custom domain. The strategy doc calls that a credibility risk:
// the product is real now, so the panels must be real. The tabbed frame STAYS (it is good
// marketing); only the fake innards were swapped for screenshots of the live demo agent
// (Michael Tran, michaeltran.247advisers.com), captured 2026-08-29/30.
public class MarketingScreenshotTests
{
    private static readonly string[] Shots =
    {
        "01-dashboard.webp", "02-clients.webp", "03-followups.webp", "04-calendar.webp",
        "05-marketing.webp", "06-website.webp", "07-leads.webp"
    };

    [Fact]
    public void Every_hero_panel_renders_a_real_screenshot()
    {
        foreach (var shot in Shots)
        {
            Assert.True(File.Exists(FindRepoFile($@"src\IPRO.Web\wwwroot\images\portal-shots\{shot}")),
                $"{shot} is missing from wwwroot/images/portal-shots");
        }

        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\Index.cshtml"));
        foreach (var shot in Shots)
        {
            Assert.Contains($"/images/portal-shots/{shot}", view);
        }
    }

    [Fact]
    public void The_fabricated_panel_ui_is_gone_but_the_tabs_survive()
    {
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\Index.cshtml"));

        // The tells of the hand-built mock: invented stats, a fake client count, a custom
        // domain no signup ever issued (the hero itself was fixed for exactly that in #408).
        Assert.DoesNotContain("i2-command-stats", view);
        Assert.DoesNotContain("Showing 5 of 128", view);
        Assert.DoesNotContain("tranfinancial.ca", view);

        // The interaction is the KEEP: seven tabs, ARIA-wired, switching seven panels.
        Assert.Contains("i2-command-tab", view);
        Assert.Contains("role=\"tab\"", view);
        Assert.Equal(7, Shots.Length);
        Assert.Equal(7, CountOccurrences(view, "role=\"tabpanel\""));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0; var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
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
