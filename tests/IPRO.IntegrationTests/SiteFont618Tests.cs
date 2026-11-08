using System;
using System.IO;
using IPRO.Entities;
using Xunit;

namespace IPRO.IntegrationTests;

// 618 (found 2026-10-29 in 614's side-by-side). The shells wrote the font list into their <style> through
// Razor, which encoded the quote marks; the browser dropped the declaration, so every font list naming a
// family with a space never reached an adviser's site (Classic's and Editorial's Georgia never showed).
// The list is now written as it is, and only a font list gets through.
public class SiteFont618Tests
{
    [Theory]
    [InlineData("Georgia, 'Times New Roman', serif", "Georgia, 'Times New Roman', serif")]
    // Modern UI: Apple's interface font in front, so Apple devices keep it (the owner, 2026-11-08); Windows
    // has neither name and draws Segoe UI as before.
    [InlineData("'Segoe UI', Arial, Helvetica, sans-serif", "-apple-system, BlinkMacSystemFont, 'Segoe UI', Arial, Helvetica, sans-serif")]
    [InlineData("\"Segoe UI\",sans-serif", "-apple-system, BlinkMacSystemFont, \"Segoe UI\", sans-serif")]
    [InlineData("-apple-system, 'Segoe UI', Arial", "-apple-system, 'Segoe UI', Arial")]          // already there: once
    [InlineData("BlinkMacSystemFont, 'Segoe UI', Arial", "BlinkMacSystemFont, 'Segoe UI', Arial")]
    [InlineData("Arial, 'Segoe UI', sans-serif", "Arial, 'Segoe UI', sans-serif")]                // only a list that starts with it
    [InlineData("'Segoe UI Light', sans-serif", "'Segoe UI Light', sans-serif")]
    [InlineData("'Times New Roman', Times, serif", "'Times New Roman', Times, serif")]
    [InlineData("Arial, Helvetica, sans-serif", "Arial, Helvetica, sans-serif")]
    [InlineData("\"Open Sans\",Arial ,  sans-serif", "\"Open Sans\", Arial, sans-serif")]
    [InlineData("Times New Roman, serif", "Times New Roman, serif")]
    [InlineData("Noto Sans-JP", "Noto Sans-JP")]
    public void A_font_list_is_written_as_it_is(string given, string written) =>
        Assert.Equal(written, CssFontList.Safe(given));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Arial; } body { display:none")]
    [InlineData("Arial</style><script>alert(1)</script>")]
    [InlineData("'Times New Roman, serif")]                 // a quote never closed
    [InlineData("'Times' New Roman', serif")]               // a quote inside a quoted name
    [InlineData("\"Open Sans', serif")]                     // quote marks that do not pair
    [InlineData("Arial, , serif")]                          // an empty family
    [InlineData("Arial,")]
    [InlineData("url(x), serif")]
    [InlineData("Arial\\, serif")]
    [InlineData("Arial\n}")]
    [InlineData("-5px, serif")]                             // a bare name starts with a letter, or a hyphen and a letter
    [InlineData("--x, serif")]
    [InlineData("- apple, serif")]
    [InlineData("Ari&#x27;al")]
    public void Anything_else_is_drawn_with_the_fallback(string? given) =>
        Assert.Equal(CssFontList.Fallback, CssFontList.Safe(given));

    [Fact]
    public void A_list_longer_than_200_letters_is_not_a_font_choice()
    {
        Assert.Equal(CssFontList.Fallback, CssFontList.Safe(string.Concat(System.Linq.Enumerable.Repeat("Arial, ", 39)) + "serif"));   // 278 letters, each family good
        Assert.Equal("Arial, serif", CssFontList.Safe("Arial, serif"));
    }

    [Theory]
    [InlineData(@"src\IPRO.Web\Views\PublicWebsite\_ClassicSidebar.cshtml")]
    [InlineData(@"src\IPRO.Web\Views\PublicWebsite\_ModernProfessional.cshtml")]
    public void Classic_and_Modern_write_the_held_list_unencoded(string shell)
    {
        var source = Read(shell);
        Assert.Contains("    var fontFamily = IPRO.Entities.CssFontList.Safe(design.FontFamily);\n", source);
        Assert.Contains("      font-family: @Html.Raw(fontFamily);\n", source);
        Assert.DoesNotContain("font-family: @fontFamily;", source);
    }

    [Fact]
    public void Editorial_writes_the_held_list_unencoded()
    {
        var source = Read(@"src\IPRO.Web\Views\PublicWebsite\_EditorialVisual.cshtml");
        Assert.Contains("font-family: @Html.Raw(IPRO.Entities.CssFontList.Safe(design.FontFamily)); }", source);
        Assert.DoesNotContain("font-family: @design.FontFamily;", source);
    }

    [Fact]
    public void No_shell_writes_a_font_or_anything_else_raw_without_the_hold()
    {
        foreach (var shell in new[] { "_ClassicSidebar", "_ModernProfessional", "_EditorialVisual" })
        {
            var source = Read($@"src\IPRO.Web\Views\PublicWebsite\{shell}.cshtml");
            foreach (System.Text.RegularExpressions.Match raw in System.Text.RegularExpressions.Regex.Matches(source, @"font-family:\s*@Html\.Raw\(([^;]*)\);"))
                Assert.True(raw.Groups[1].Value is "fontFamily" or "IPRO.Entities.CssFontList.Safe(design.FontFamily)", $"{shell}: {raw.Value}");
        }
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative)).Replace("\r\n", "\n");
    }
}
