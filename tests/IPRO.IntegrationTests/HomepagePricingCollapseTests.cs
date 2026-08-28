using System;
using System.IO;
using Xunit;

namespace IPRO.IntegrationTests;

// Owner request 2026-08-28: "make the pricing collapsible -- it is too long of a page and it gets
// visitors bored." The comparison table is the longest thing on the homepage (48 feature codes are
// defined, so it runs ~49 rows). Collapsed it shows the package headers -- which carry the actual
// PRICES, so pricing itself is never hidden -- the first six feature rows, and the CTA row.
//
// Source-walk pin. The behaviour itself was verified in a browser harness before shipping
// (collapsed: 7 of 13 rows with the CTA visible; expanded: 13 of 13; label and aria-expanded flip
// both ways). What this test protects is the part a future edit can silently break: the CTA
// exemption and the toggle wiring.
public class HomepagePricingCollapseTests
{
    [Fact]
    public void Pricing_table_collapses_but_never_hides_the_get_started_row()
    {
        var razor = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\Index.cshtml"));

        // THE rule. `nth-child(n+7)` hides from the seventh row on; `:not(:last-child)` exempts
        // the CTA row, which is the last row of the tbody. Drop that exemption and "Get started"
        // disappears the moment the table is collapsed -- the one outcome that would cost signups.
        Assert.Contains(
            ".i2-plan-table.i2-collapsed tbody tr:nth-child(n+7):not(:last-child) { display: none; }",
            razor);

        // Wiring: the table carries the id the toggle controls, and only collapses when there is
        // actually something to hide.
        Assert.Contains("id=\"i2-plan-table\"", razor);
        Assert.Contains("allFeatures.Count > 6 ? \"i2-collapsed\" : \"\"", razor);

        // The toggle: rendered only when it has a job, and accessible.
        Assert.Contains("id=\"i2-pricing-toggle\"", razor);
        Assert.Contains("aria-controls=\"i2-plan-table\"", razor);
        Assert.Contains("aria-expanded=\"false\"", razor);

        // The script flips class, label and aria together -- a label that lies about the state is
        // the classic half-fix here.
        Assert.Contains("planTable.classList.toggle('i2-collapsed')", razor);
        Assert.Contains("planToggle.setAttribute('aria-expanded', String(!collapsed))", razor);
        Assert.Contains("planToggle.getAttribute('data-more') : 'Show less'", razor);
    }

    [Fact]
    public void The_prices_themselves_are_never_inside_the_collapsed_region()
    {
        // Collapsing PRICING would defeat the purpose. Prices live in <thead>; the rule only ever
        // touches tbody rows, so the monthly price, annual price and setup fee stay on screen.
        var razor = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\Index.cshtml"));
        var rule = ".i2-plan-table.i2-collapsed tbody tr";
        Assert.Contains(rule, razor);
        Assert.DoesNotContain(".i2-plan-table.i2-collapsed thead", razor);

        // Anchor on the PRICING table -- the page has other tables, and the first <thead> in the
        // file belongs to one of them.
        var table = razor.IndexOf("id=\"i2-plan-table\"", StringComparison.Ordinal);
        Assert.True(table >= 0, "the pricing table lost its id");
        var thead = razor.IndexOf("<thead>", table, StringComparison.Ordinal);
        var tbody = razor.IndexOf("<tbody>", table, StringComparison.Ordinal);
        Assert.True(thead >= 0 && tbody > thead, "the pricing table lost its thead/tbody structure");
        var headerBlock = razor[thead..tbody];
        Assert.Contains("i2-plan-price", headerBlock);    // monthly price
        Assert.Contains("i2-plan-annual", headerBlock);   // annual + setup fee
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
