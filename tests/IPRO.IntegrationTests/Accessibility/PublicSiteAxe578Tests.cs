using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace IPRO.IntegrationTests.Accessibility;

// 578 (2026-10-15): the accessibility audit of 2026-10-14 (DOCS/ACCESSIBILITY_AUDIT_2026-10-14.md)
// found the public websites close to WCAG 2.1 AA but held back by template problems. This is the
// guard that keeps them fixed: every design's pages, rendered by the real views, are opened in
// headless Edge and checked by axe-core 4.10.2 against the WCAG 2.0/2.1 A and AA rules (and the 2.2 AA
// ones), with no network. A finding names the rule and the element.
public class PublicSiteAxe578Tests : IClassFixture<EdgeFixture>
{
    private readonly EdgeFixture _edge;
    public PublicSiteAxe578Tests(EdgeFixture edge) => _edge = edge;

    public static IEnumerable<object[]> Designs() => PublicSitePages.Designs.Select(d => new object[] { d });

    [Theory]
    [MemberData(nameof(Designs))]
    public async Task Every_page_of_every_design_passes_axe_on_a_desktop(string design)
    {
        var site = PublicSitePages.Build(design);
        var findings = new List<string>();
        foreach (var slug in PublicSitePages.Slugs.Append("no-such-page"))
        {
            var html = await PublicSitePages.RenderAsync(site.Model(slug));
            findings.AddRange((await _edge.AxeAsync(html, $"{design}-{slug}")).Select(f => $"{slug}: {f}"));
            // Not a WCAG rule, but the audit's finding 14: each page opens with a level-one heading.
            findings.AddRange((await _edge.AxeOpenPageAsync("page-has-heading-one")).Select(f => $"{slug}: {f}"));
        }
        Assert.True(findings.Count == 0, $"{design}: {findings.Count} findings\n" + string.Join("\n", findings));
    }

    [Theory]
    [MemberData(nameof(Designs))]
    public async Task Every_page_of_every_design_passes_axe_on_a_phone(string design)
    {
        var site = PublicSitePages.Build(design);
        var findings = new List<string>();
        foreach (var slug in PublicSitePages.Slugs)
        {
            var html = await PublicSitePages.RenderAsync(site.Model(slug));
            findings.AddRange((await _edge.AxeAsync(html, $"{design}-{slug}-phone", 375)).Select(f => $"{slug}: {f}"));
        }
        Assert.True(findings.Count == 0, $"{design}: {findings.Count} findings\n" + string.Join("\n", findings));
    }

    // Audit finding 3: four of the colours iPro offers made white button text and small coloured words
    // unreadable. Whatever colour a site has, its text must read (WCAG 1.4.3). Every page, for those four
    // and the default blue; AccentContrast578Tests pins that the other offered colours are left alone.
    [Theory]
    [MemberData(nameof(Designs))]
    public async Task Every_offered_colour_gives_readable_text(string design)
    {
        var findings = new List<string>();
        foreach (var (name, hex) in PublicSitePages.OfferedColours.Where(c => c.Name is "Modern Minimal" or "Amber Gold" or "Sunset Orange" or "Teal" or "Ocean Blue"))
        {
            var site = PublicSitePages.Build(design, hex);
            // The "page not found" screen sits on the page background itself (Classic Sidebar: cream).
            foreach (var slug in PublicSitePages.Slugs.Append("no-such-page"))
            {
                var html = await PublicSitePages.RenderAsync(site.Model(slug));
                findings.AddRange((await _edge.AxeAsync(html, $"{design}-{slug}-{hex.TrimStart('#')}", 1280, "color-contrast"))
                    .Select(f => $"{name} {hex} {slug}: {f}"));
            }
        }
        Assert.True(findings.Count == 0, $"{design}: {findings.Count} findings\n" + string.Join("\n", findings));
    }
}
