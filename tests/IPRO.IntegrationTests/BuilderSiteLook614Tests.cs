using System;
using System.IO;
using IPRO.Business.SiteImport;
using IPRO.Entities;
using Xunit;

namespace IPRO.IntegrationTests;

// 614: the builder draws an adviser's page in the site's own design from what iPro tells it when a
// session opens (BuilderBeta.Look). The values each shell sets in its own :root are copied there, so
// these tests hold the copy to the shells' lines: a shell that changes a tint, its deep colour, its own
// text colours or how spacing and button corners are worked out fails here until the copy follows.
public class BuilderSiteLook614Tests
{
    public static readonly TheoryData<string, string> Shells = new()
    {
        { "modern-professional", @"src\IPRO.Web\Views\PublicWebsite\_ModernProfessional.cshtml" },
        { "classic-sidebar", @"src\IPRO.Web\Views\PublicWebsite\_ClassicSidebar.cshtml" },
        { "editorial-visual", @"src\IPRO.Web\Views\PublicWebsite\_EditorialVisual.cshtml" }
    };

    [Theory]
    [MemberData(nameof(Shells))]
    public void The_builders_copy_of_each_shells_values_matches_the_shell(string renderer, string file)
    {
        var shell = File.ReadAllText(FindRepoFile(file));
        var copy = BuilderBeta.ShellFor(renderer);

        Assert.Equal(renderer, copy.Name);
        Assert.Contains($"var tint = \"{copy.Tint}\";", shell);
        Assert.Contains($"--site-theme-deep: {copy.Deep};", shell);
        foreach (var (name, value) in copy.Extra) Assert.Contains($"{name}: {value};", shell);
        Assert.Contains("var sectionSpace = design.SectionSpacing == \"compact\" ? \"44px\" : design.SectionSpacing == \"comfortable\" ? \"62px\" : \"82px\";", shell);
        Assert.Contains("var buttonRadius = design.ButtonStyle == \"square\" ? \"2px\" : design.ButtonStyle == \"pill\" ? \"999px\" : \"7px\";", shell);
        Assert.Contains("<body class=\"header-@design.HeaderStyle hero-@design.HeroLayout hero-style-@design.HeroStyle buttons-@design.ButtonStyle\">", shell);
    }

    [Fact]
    public void The_public_site_picks_its_shell_by_the_same_names()
    {
        var index = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\PublicWebsite\Index.cshtml"));
        Assert.Contains("design.Renderer == \"classic-sidebar\" ? \"_ClassicSidebar\"", index);
        Assert.Contains("design.Renderer == \"editorial-visual\" ? \"_EditorialVisual\" : \"_ModernProfessional\"", index);
        Assert.Equal("modern-professional", BuilderBeta.ShellFor("something-else").Name);
    }

    [Fact]
    public void The_hero_letter_is_the_one_the_managed_pages_draw()
    {
        foreach (var page in new[] { "_ModernManagedPage", "_ClassicManagedPage", "_EditorialManagedPage" })
        {
            var view = File.ReadAllText(FindRepoFile($@"src\IPRO.Web\Views\PublicWebsite\{page}.cshtml"));
            Assert.Contains("if (!string.IsNullOrWhiteSpace(agent.FirstName)) return agent.FirstName.Substring(0, 1).ToUpperInvariant();", view);
            Assert.Contains("if (!string.IsNullOrWhiteSpace(agent.CompanyName)) return agent.CompanyName.Substring(0, 1).ToUpperInvariant();", view);
        }
        Assert.Equal("R", BuilderBeta.Monogram(new AgentUser { FirstName = "raniah", CompanyName = "Motamed Wealth" }));
        Assert.Equal("M", BuilderBeta.Monogram(new AgentUser { FirstName = " ", CompanyName = "motamed Wealth" }));
        Assert.Equal(((char)0x2022).ToString(), BuilderBeta.Monogram(new AgentUser()));
    }

    [Fact]
    public void A_site_with_its_own_choices_sends_them()
    {
        var site = new AgentWebsite
        {
            Template = new WebsiteTemplate { TemplateKey = "classic-sidebar", Name = "Classic Sidebar" },
            SiteTitle = "Raniah's site",
            SectionSpacingOverride = "compact",
            ButtonStyleOverride = "pill",
            HeaderSettingsJson = "{\"Style\":\"compact\",\"LogoPosition\":\"center\",\"LogoSize\":\"large\",\"Sticky\":true,\"ShowPhone\":true,\"ShowEmail\":true,\"ButtonText\":\"Book a call\"}",
            FooterSettingsJson = "{\"CopyrightText\":\"Motamed Wealth Inc.\"}"
        };
        var agent = new AgentUser { FirstName = "Raniah", LastName = "Motamed", CompanyName = "Motamed Wealth", Phone = "416 555 0100", Email = "raniah@example.test" };

        var look = BuilderBeta.Look(site, agent);
        var vars = Assert.IsType<System.Collections.Generic.Dictionary<string, string>>(look["vars"]);

        Assert.Equal("44px", vars["--section-space"]);
        Assert.Equal("999px", vars["--button-radius"]);
        Assert.Equal("#d8e1ee", vars["--line"]);
        Assert.Equal("style-compact logo-center logo-size-large is-sticky", look["headerClass"]);
        Assert.Equal("Motamed Wealth Inc.", look["copyright"]);
        Assert.Equal("R", look["monogram"]);
        var contact = Assert.IsType<System.Collections.Generic.Dictionary<string, string>>(look["contact"]);
        Assert.Equal("Raniah Motamed", contact["name"]);
        Assert.Equal("416 555 0100", contact["phone"]);
        var actions = Assert.IsType<System.Collections.Generic.Dictionary<string, object>>(look["headerActions"]);
        Assert.Equal("416 555 0100", actions["phone"]);
        Assert.Equal(true, actions["email"]);
        Assert.Equal("Book a call", actions["button"]);
        // The font as the live site draws it: a list with quote marks never reaches the page (TODO 618).
        site.FontFamilyOverride = "Georgia, 'Times New Roman', serif";
        Assert.Equal(string.Empty, BuilderBeta.Look(site, agent)["siteFont"]);
        site.FontFamilyOverride = "Arial, Helvetica, sans-serif";
        Assert.Equal("Arial, Helvetica, sans-serif", BuilderBeta.Look(site, agent)["siteFont"]);
        // Without the adviser (an older caller), the design alone.
        Assert.False(BuilderBeta.Look(site).ContainsKey("company"));
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
