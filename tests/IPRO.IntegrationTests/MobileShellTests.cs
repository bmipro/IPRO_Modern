using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace IPRO.IntegrationTests;

// Mobile-readiness sweep, 2026-08-30 (4 agents + live measurement at 375px). The portal IS
// responsive -- the sidebar goes off-canvas at 991.98px with a working hamburger -- but two shell
// defects blocked ordinary tasks on every page, so they are fixed first:
//
//   1. The drawer could not be closed. .agent-sidebar is z-index 1040 and .agent-topbar 1030, so
//      the open panel painted OVER the hamburger; tapping where it visibly sits hit the brand
//      logo's <a href="/portal/Dashboard"> underneath, which navigated away and discarded the page
//      -- including an unsaved form. There was no backdrop, no outside-click handler and no ESC
//      handler; the entire sidebar JS was a five-line classList.toggle. For an access-gated agent
//      every nav link is preventDefault'd into the upgrade modal, so even that accidental escape
//      never fired and the drawer became a genuine trap.
//   2. Logout was unreachable. height:100vh on a position:fixed sidebar puts the footer at the
//      bottom of the LARGE viewport, so with mobile Safari's toolbar showing, Logout sat ~70px and
//      Change Password ~29px below the visible edge, outside the only scrolling child.
//
// These are source-walk pins (CSS and inline script in a Razor layout). They were observed RED
// against the pre-fix layout, and the behaviour was verified live at 375px after deploy.
public class MobileShellTests
{
    private static string Layout() => File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Shared\_Layout.cshtml"));

    // ---- 1. the drawer can be closed --------------------------------------------------------

    [Fact]
    public void The_open_drawer_has_a_backdrop_that_closes_it()
    {
        var layout = Layout();

        // An element to tap outside the drawer...
        Assert.Contains("id=\"sidebarBackdrop\"", layout);
        // ...that is styled to cover the page beneath the sidebar but above the content.
        var css = SidebarBackdropCss(layout);
        Assert.Contains("position:fixed", css);
        Assert.Contains("z-index:1035", css);   // between .agent-content and .agent-sidebar (1040)

        // ...and actually closes the drawer when tapped.
        Assert.Matches(new Regex(@"sidebarBackdrop.*addEventListener\('click'", RegexOptions.Singleline), layout);
    }

    [Fact]
    public void Escape_closes_the_drawer()
    {
        var layout = Layout();
        Assert.Matches(new Regex(@"keydown.*Escape", RegexOptions.Singleline), layout);
    }

    [Fact]
    public void Closing_is_a_real_close_not_another_toggle()
    {
        // A toggle on the backdrop would REOPEN a closed drawer if the classes ever desynced.
        // Closing must be explicit.
        var layout = Layout();
        Assert.Contains("classList.remove('show')", layout);
    }

    [Fact]
    public void The_hamburger_is_not_painted_over_by_the_open_drawer()
    {
        // The defect: .agent-topbar z-index 1030 < .agent-sidebar 1040, so the toggle sat UNDER
        // the open panel and the tap landed on the brand link. The toggle must out-rank the
        // sidebar, or the drawer must carry its own close control.
        var layout = Layout();

        // The close control must live INSIDE the drawer. Raising the toggle's z-index does NOT
        // work and this test used to accept it: .agent-topbar is position:sticky with a z-index,
        // which creates a stacking context, so a child of it is resolved WITHIN that context and
        // can never out-rank the sidebar's 1040 however large its own z-index. Verified live at
        // 375px -- with z-index:1045 on the toggle, elementFromPoint at the button's centre still
        // returned the brand logo <img> inside <a href="/portal/Dashboard">. Only a control that
        // is a child of the drawer escapes the trap.
        Assert.Contains("id=\"sidebarClose\"", layout);
        Assert.Contains("sidebarClose", layout[layout.IndexOf("id=\"agentSidebar\"", StringComparison.Ordinal)..]);
        Assert.Matches(new Regex(@"sidebarClose.*addEventListener\('click'", RegexOptions.Singleline), layout);
        // ...and it is only shown where the drawer is actually off-canvas.
        Assert.Contains(".sidebar-close", MobileMediaBlock(layout));
    }

    [Fact]
    public void A_gated_agent_is_not_trapped_because_the_close_paths_do_not_depend_on_navigation()
    {
        // For an access-gated agent every nav link is preventDefault'd into the upgrade modal, so
        // "navigate away" is not an escape. Both close paths must be page-load independent.
        var layout = Layout();
        Assert.Contains("sidebarBackdrop", layout);
        Assert.Matches(new Regex(@"keydown.*Escape", RegexOptions.Singleline), layout);
    }

    // ---- 2. the footer is reachable ---------------------------------------------------------

    [Fact]
    public void The_sidebar_is_sized_to_the_visible_viewport_not_the_large_one()
    {
        var css = RuleFor(Layout(), ".agent-sidebar");
        // 100vh stays FIRST as the fallback for browsers without dvh; 100dvh must follow it.
        Assert.Contains("height:100vh", css);
        Assert.Contains("height:100dvh", css);
        Assert.True(css.IndexOf("height:100vh", StringComparison.Ordinal) < css.IndexOf("height:100dvh", StringComparison.Ordinal),
            "100dvh must come after 100vh or the fallback wins");
    }

    [Fact]
    public void The_footer_clears_the_phones_home_indicator()
    {
        var css = RuleFor(Layout(), ".agent-sidebar-footer");
        Assert.Contains("env(safe-area-inset-bottom)", css);
    }

    // ---- helpers -----------------------------------------------------------------------------

    // The first CSS rule body for a selector in the layout's inline <style> block.
    private static string RuleFor(string layout, string selector)
    {
        var i = layout.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(i > 0, $"no CSS rule found for {selector}");
        var open = layout.IndexOf('{', i);
        var close = layout.IndexOf('}', open);
        return layout[open..close];
    }

    private static string SidebarBackdropCss(string layout) => RuleFor(layout, ".sidebar-backdrop");

    // The @media (max-width: 991.98px) block -- where the drawer is off-canvas.
    private static string MobileMediaBlock(string layout)
    {
        var i = layout.IndexOf("media (max-width: 991.98px)", StringComparison.Ordinal);
        Assert.True(i > 0, "the mobile media query moved; this pin needs updating");
        var end = layout.IndexOf("\n        }", i, StringComparison.Ordinal);
        return layout[i..end];
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
