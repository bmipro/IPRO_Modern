using System;
using System.Linq;
using IPRO.Business.Services;
using Xunit;

namespace IPRO.IntegrationTests;

// M1 (launch runway Phase 2, Wave C, 2026-08-27). The sanitizer's CSS handling was a deny-list of
// eight properties, and the register's own reproduction proved it useless: transform + negative
// margin + viewport sizing rebuilt the same full-viewport phishing cover from properties still
// allowed. The fix inverts it -- an ALLOW-list of formatting properties, plus two value guards
// (negative margins/text-indent, viewport units) for the residue the list alone cannot express.
// This class closes the suite's one skipped test (which is un-skipped in MediumSweepTests).
public class OverlayAllowListTests
{
    // ------------------------------------------------------------- the exploit, every flavour --

    [Theory]
    [InlineData("transform:translateY(-2000px);width:100vw;height:100vh", "the register's original reproduction")]
    [InlineData("transform: translate(-50%, -50%)", "bare transform")]
    [InlineData("margin-top:-2000px;width:2000px;height:2000px;background:#fff", "negative margin drag")]
    [InlineData("margin: 0 0 0 -1500px", "negative component inside shorthand margin")]
    [InlineData("text-indent:-9999px", "negative text-indent")]
    [InlineData("width:100vw;height:100vh;background:#fff", "viewport sizing")]
    [InlineData("height:100dvh", "dynamic viewport unit")]
    [InlineData("position:fixed;inset:0;z-index:9999", "the classic, still dead")]
    [InlineData("rotate: 45deg; translate: -50% -50%", "the standalone transform properties")]
    [InlineData("clip-path: inset(0)", "clip-path")]
    public void M1_no_overlay_mechanism_survives(string css, string because)
    {
        var output = HtmlContentSanitizer.Sanitize($"<div style=\"{css}\">OVERLAY</div>");
        var flat = output.Replace(" ", "").ToLowerInvariant();

        Assert.False(
            flat.Contains("transform") || flat.Contains("translate") || flat.Contains("rotate:") ||
            flat.Contains("position:") || flat.Contains("z-index") || flat.Contains("inset") ||
            flat.Contains("clip-path") ||
            flat.Contains("vw") || flat.Contains("vh") ||
            System.Text.RegularExpressions.Regex.IsMatch(flat, @"(margin[^:]*|text-indent):[^;""]*-\d"),
            $"an overlay mechanism survived ({because}): {output}");
        // The words themselves always survive -- H1's rule: never destroy agent prose.
        Assert.Contains("OVERLAY", output);
    }

    // -------------------------------------------------- the promise: formatting is untouched --

    [Fact]
    public void M1_a_representative_pasted_newsletter_keeps_every_declaration()
    {
        // The register's warning was "chosen carefully so existing newsletter formatting does not
        // break". This block is the marketing-HTML shape agents paste through the editor's source
        // toggle: fonts, colors, backgrounds, spacing, borders, a styled table, a full-width
        // wrapper. EVERY declaration here must survive verbatim (modulo whitespace).
        const string html =
            "<div style=\"max-width: 600px; margin: 0 auto; background-color: #f7f9fc; padding: 24px; " +
            "border: 1px solid #dbe1ed; border-radius: 8px; font-family: Georgia, serif\">" +
            "<h2 style=\"color: #193f82; font-size: 22px; letter-spacing: 0.02em; text-align: center; " +
            "text-transform: uppercase\">Monthly update</h2>" +
            "<p style=\"font-size: 15px; line-height: 1.6; color: #333333; text-align: justify; " +
            "margin-top: 12px; text-indent: 2em\">Body copy with <span style=\"font-weight: bold; " +
            "text-decoration: underline; background-color: #fff3cd\">highlights</span>.</p>" +
            "<table style=\"width: 100%; border-collapse: collapse; table-layout: fixed\">" +
            "<tr><td style=\"border-bottom: 1px solid #ccc; padding: 8px; vertical-align: top; " +
            "white-space: nowrap\">Cell</td></tr></table>" +
            "<img src=\"https://example.test/x.png\" style=\"width: 100%; height: auto; display: block; " +
            "border-radius: 6px; object-fit: cover\">" +
            "</div>";

        var output = HtmlContentSanitizer.Sanitize(html);
        var flat = Flatten(output);

        foreach (var declaration in new[]
        {
            "max-width:600px", "margin:0auto", "background-color:#f7f9fc", "padding:24px",
            "border:1pxsolid#dbe1ed", "border-radius:8px", "font-family:georgia,serif",
            "color:#193f82", "font-size:22px", "letter-spacing:0.02em", "text-align:center",
            "text-transform:uppercase", "font-size:15px", "line-height:1.6",
            "text-align:justify", "margin-top:12px", "text-indent:2em", "font-weight:bold",
            "text-decoration:underline", "background-color:#fff3cd", "width:100%",
            "border-collapse:collapse", "table-layout:fixed", "border-bottom:1pxsolid#ccc",
            "padding:8px", "vertical-align:top", "white-space:nowrap", "height:auto",
            "display:block", "border-radius:6px", "object-fit:cover"
        })
        {
            AssertDeclarationSurvived(flat, declaration, output);
        }
    }

    [Fact]
    public void M1_value_guards_drop_only_the_offending_declaration()
    {
        // Surgical, not wholesale: the poisoned declaration dies, its honest neighbours live.
        var output = HtmlContentSanitizer.Sanitize(
            "<p style=\"color: #333; margin-top: -400px; font-size: 14px\">text</p>");
        var flat = Flatten(output);
        AssertDeclarationSurvived(flat, "color:#333", output);
        Assert.Contains("font-size:14px", flat);
        Assert.DoesNotContain("margin-top", flat);

        var viewport = HtmlContentSanitizer.Sanitize(
            "<p style=\"width: 100vw; padding: 10px\">text</p>");
        var flatV = Flatten(viewport);
        Assert.Contains("padding:10px", flatV);
        Assert.DoesNotContain("100vw", flatV);
    }

    [Fact]
    public void M1_positive_margins_and_percent_widths_are_untouched()
    {
        // The guards' other direction: ordinary spacing and percentage sizing -- the legitimate
        // spellings of what the exploit abuses -- pass through unchanged.
        var output = HtmlContentSanitizer.Sanitize(
            "<div style=\"margin: 16px 8px; text-indent: 1.5em; width: 80%\">text</div>");
        var flat = Flatten(output);
        Assert.Contains("margin:16px8px", flat);
        Assert.Contains("text-indent:1.5em", flat);
        Assert.Contains("width:80%", flat);
    }

    [Fact]
    public void M1_font_shorthand_with_a_hyphenated_family_survives_the_negative_guard()
    {
        // A hyphen inside a font name or color must never be mistaken for a negative number.
        var output = HtmlContentSanitizer.Sanitize(
            "<p style=\"font-family: sans-serif; margin-left: 4px; color: #a52218\">text</p>");
        var flat = Flatten(output);
        Assert.Contains("font-family:sans-serif", flat);
        Assert.Contains("margin-left:4px", flat);
    }

    private static string Flatten(string html) => html.Replace(" ", "").Replace("\"", "").ToLowerInvariant();

    // The sanitizer's CSS engine normalizes colors (#333 -> rgba(51, 51, 51, 1)), so a hex
    // expectation is satisfied by either spelling. Survival is the property under test, not the
    // serialization.
    private static void AssertDeclarationSurvived(string flat, string declaration, string output)
    {
        if (flat.Contains(declaration)) return;
        var hex = System.Text.RegularExpressions.Regex.Match(declaration, "#([0-9a-f]{6}|[0-9a-f]{3})");
        if (hex.Success)
        {
            var h = hex.Groups[1].Value;
            if (h.Length == 3) h = string.Concat(h.Select(c => new string(c, 2)));
            var rgba = $"rgba({Convert.ToInt32(h[..2], 16)},{Convert.ToInt32(h[2..4], 16)},{Convert.ToInt32(h[4..], 16)},1)";
            if (flat.Contains(declaration.Replace(hex.Value, rgba))) return;
        }
        Assert.Fail($"legitimate formatting was destroyed: `{declaration}` missing from {output}");
    }
}
