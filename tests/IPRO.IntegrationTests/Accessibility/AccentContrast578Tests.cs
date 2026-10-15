using System.Linq;
using IPRO.Entities;
using Xunit;

namespace IPRO.IntegrationTests.Accessibility;

// 578 (2026-10-15), audit finding 3 (WCAG 1.4.3): the four colours from one owner's choice. The page
// checks in PublicSiteAxe578Tests prove it on rendered pages; these pin the arithmetic.
public class AccentContrast578Tests
{
    private const string ModernTint = "#eef4ff";

    [Theory]
    [InlineData("#ffffff", "#000000", 21.0)]
    [InlineData("#0EA5E9", "#ffffff", 2.77)]
    [InlineData("#1457d9", "#ffffff", 6.19)]
    public void The_ratio_is_WCAGs(string a, string b, double expected) =>
        Assert.Equal(expected, AccentContrast.Ratio(a, b), 2);

    [Theory]
    [InlineData("#1457d9")]   // Ocean Blue, Professional Adviser
    [InlineData("#315b46")]   // Classic Sidebar
    [InlineData("#b42318")]   // Editorial Visual
    [InlineData("#1B3A5C")]   // Classic Navy
    public void A_colour_whose_white_text_reads_is_left_alone(string accent)
    {
        var colours = AccentContrast.For(accent, ModernTint);
        Assert.Equal(accent.ToLowerInvariant(), colours.Fill);
        Assert.Equal(AccentContrast.White, colours.OnFill);
        Assert.Equal(accent.ToLowerInvariant(), colours.Band);
        Assert.Equal(accent.ToLowerInvariant(), colours.Ink);
    }

    [Theory]
    [InlineData("#0EA5E9")]   // Modern Minimal, www.4ipro.com
    [InlineData("#b8892b")]   // Amber Gold
    [InlineData("#d9541f")]   // Sunset Orange
    [InlineData("#0f8c8c")]   // Teal
    [InlineData("#ffe600")]   // anything the free picker allows
    [InlineData("#f0f0f0")]
    [InlineData("#2563eb")]   // reads on the tint (4.7:1) but not on its cards until deepened
    [InlineData("#c2410c")]
    public void Every_role_reads_whatever_the_colour(string accent)
    {
        foreach (var keepLight in new[] { true, false })
        {
            var colours = AccentContrast.For(accent, ModernTint, darkTextOnLight: keepLight);
            Assert.True(AccentContrast.Ratio(colours.Fill, colours.OnFill) >= 4.5, $"{accent} fill {colours.Fill} / {colours.OnFill}");
            Assert.True(AccentContrast.Ratio(colours.Band, AccentContrast.White) >= 4.5, $"{accent} band {colours.Band}");
            Assert.True(AccentContrast.Ratio(colours.Ink, ModernTint) >= 4.5, $"{accent} ink {colours.Ink} on the tint");
            Assert.True(AccentContrast.Ratio(colours.Ink, AccentContrast.White) >= 4.5, $"{accent} ink {colours.Ink} on white");
            // The Did You Know grid lays 3% grey cards on the tint (#eef4ff becomes #e7edf7).
            Assert.True(AccentContrast.Ratio(colours.Ink, "#e7edf7") >= 4.5, $"{accent} ink {colours.Ink} on a card");
        }
    }

    [Fact]
    public void A_light_colour_keeps_itself_with_dark_text_or_deepens_for_white_text()
    {
        var keep = AccentContrast.For("#0EA5E9", ModernTint, darkTextOnLight: true);
        Assert.Equal("#0ea5e9", keep.Fill);
        Assert.Equal(AccentContrast.Dark, keep.OnFill);

        var deepen = AccentContrast.For("#0EA5E9", ModernTint, darkTextOnLight: false);
        Assert.NotEqual("#0ea5e9", deepen.Fill);
        Assert.Equal(AccentContrast.White, deepen.OnFill);

        // A middle colour (neither text reads on it) is deepened only as far as white text needs.
        var teal = AccentContrast.For("#0f8c8c", ModernTint, darkTextOnLight: true);
        Assert.Equal(AccentContrast.White, teal.OnFill);
        Assert.True(AccentContrast.Ratio(teal.Fill, "#0f8c8c") < 1.2, $"teal moved too far: {teal.Fill}");
    }

    [Fact]
    public void Coloured_text_reads_on_a_light_page_background_too()
    {
        // The Classic Sidebar's cream page (#f5f3ed) is darker than its tint; sky blue's ink must read on both.
        var colours = AccentContrast.For("#0EA5E9", "#f2f6fc", "#f5f3ed");
        Assert.True(AccentContrast.Ratio(colours.Ink, "#f5f3ed") >= 4.5, colours.Ink);
        // A dark background chosen by the owner is not a background for coloured text: it is ignored.
        Assert.Equal(AccentContrast.For("#0EA5E9", "#f2f6fc").Ink, AccentContrast.For("#0EA5E9", "#f2f6fc", "#101010").Ink);
    }

    // The owner's choice (2026-10-15, option 1): light colours keep themselves with dark text, and My
    // Website says so under the colour picker, in his words.
    [Fact]
    public void The_owners_choice_is_kept_and_My_Website_says_so()
    {
        Assert.True(AccentContrast.KeepLightColours);
        var view = System.IO.File.ReadAllText(System.IO.Path.Combine(PublicSitePages.RepoRoot(), "src", "IPRO.Web", "Views", "Website", "Index.cshtml"));
        Assert.Contains("We adjust text on your colour so it stays readable.", view);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("red")]
    [InlineData("#12345")]
    public void A_colour_that_is_not_a_hex_colour_falls_back_to_the_default_blue(string? accent) =>
        Assert.Equal("#1457d9", AccentContrast.For(accent!, ModernTint).Fill);
}
