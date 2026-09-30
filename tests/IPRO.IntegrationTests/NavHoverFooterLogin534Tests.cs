using System;
using System.IO;
using Xunit;

namespace IPRO.IntegrationTests;

// 534 (2026-09-30): the owner, on the live adviser sites: "there is no way that we could do mouse over
// Resources menu and be able to click anything when opens". A menu with a third tier opens a panel as
// wide as the header, hung from the header's bottom edge; the menu row is centred in a taller header,
// so about 12px of header lies between "Resources" and its panel. Pure :hover dropped the panel the
// moment the pointer entered that strip. On a desktop a small script now keeps a submenu open for a
// moment after the pointer leaves its item (and closes it at once when another submenu opens); :hover
// stays as the fallback. And the owner's second ask: the agent-login arrow sits after "Powered by iPro".
public class NavHoverFooterLogin534Tests
{
    [Fact]
    public void The_desktop_menu_stays_open_while_the_pointer_crosses_into_its_panel()
    {
        var styles = Read(@"src\IPRO.Web\Views\PublicWebsite\_ManagedPageStyles.cshtml");
        var desktop = MediaBlock(styles, "@@media (min-width: 801px)");
        Assert.Contains(".public-site-nav__item--mega.is-hover .public-site-nav__mega { display: block; }", desktop);
        Assert.Contains(".public-site-nav__item.is-hover .public-site-nav__children { display: block; }", desktop);
        // The plain :hover rules stay: without the script the menu behaves as before.
        Assert.Contains(".public-site-nav__item--mega:hover .public-site-nav__mega { display: block; }", desktop);

        var nav = Read(@"src\IPRO.Web\Views\PublicWebsite\_PublicNavigation.cshtml");
        var script = nav[nav.IndexOf("<script", StringComparison.Ordinal)..];
        Assert.Contains("window.matchMedia('(min-width: 801px)')", script);
        Assert.Contains("addEventListener('mouseenter'", script);
        Assert.Contains("addEventListener('mouseleave'", script);
        Assert.Contains("classList.add('is-hover')", script);
        Assert.Contains("setTimeout(", script);
        // Only items that have a submenu take part.
        Assert.Contains(".public-site-nav__mega, .public-site-nav__children", script);
    }

    [Fact]
    public void The_agent_login_arrow_follows_Powered_by_iPro()
    {
        var footer = Read(@"src\IPRO.Web\Views\PublicWebsite\_PublicFooterContent.cshtml");
        var powered = footer.IndexOf("<div class=\"public-footer__powered\">", StringComparison.Ordinal);
        var login = footer.IndexOf("<a class=\"public-footer__agent-login\"", StringComparison.Ordinal);
        Assert.True(powered >= 0, "the Powered by line was not found");
        Assert.True(login > powered, "the agent-login arrow must come after the Powered by line");
        Assert.Equal(login, footer.LastIndexOf("<a class=\"public-footer__agent-login\"", StringComparison.Ordinal));
        Assert.True(login < footer.IndexOf("</div>", powered, StringComparison.Ordinal), "the arrow sits inside the Powered by line");
        // With the arrow gone from it, the social row is drawn only when there are links to show.
        Assert.Contains("@if (footer.SocialLinks.Any())", footer);

        foreach (var shell in new[] { "_ClassicFooter.cshtml", "_EditorialFooter.cshtml", "_ModernFooter.cshtml" })
        {
            var css = Read(Path.Combine(@"src\IPRO.Web\Views\PublicWebsite", shell));
            Assert.DoesNotContain("public-footer__agent-login { padding-left: 12px; border-left", css);
        }

        var guide = Read(@"DOCS\04_WEBSITE_BUILDER.md");
        Assert.Contains("appears in the footer, right after **Powered by iPro**", guide);
        Assert.DoesNotContain("beside the social media icons", guide);
    }

    private static string MediaBlock(string css, string query)
    {
        var i = css.IndexOf(query, StringComparison.Ordinal);
        Assert.True(i >= 0, $"no media block found for {query}");
        var open = css.IndexOf('{', i);
        var depth = 0;
        for (var j = open; j < css.Length; j++)
        {
            if (css[j] == '{') depth++;
            else if (css[j] == '}') { depth--; if (depth == 0) return css[open..j]; }
        }
        throw new InvalidOperationException($"unterminated media block for {query}");
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
