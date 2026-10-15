using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using Xunit;

namespace IPRO.IntegrationTests.Accessibility;

// 578 (2026-10-15): what the audit of 2026-10-14 found a keyboard could not do on the public websites,
// done by a keyboard in headless Edge on pages the real views render: real Tab, Enter and Escape key
// presses, and what has focus afterwards.
public class PublicSiteKeyboard578Tests : IClassFixture<EdgeFixture>
{
    private readonly EdgeFixture _edge;
    public PublicSiteKeyboard578Tests(EdgeFixture edge) => _edge = edge;

    public static IEnumerable<object[]> Designs() => PublicSitePages.Designs.Select(d => new object[] { d });

    // The menus once more for the Classic Sidebar design with its menu across the top, not in the side rail.
    public static IEnumerable<object[]> Menus() => PublicSitePages.Designs.Select(d => new object[] { d, false })
        .Append(new object[] { PublicSitePages.Classic, true });

    private const string ActiveDescription =
        "(function(e){return e?(e.tagName+(e.id?'#'+e.id:'')+'.'+(e.className||'')+' '+(e.getAttribute('aria-label')||e.textContent||'').trim().slice(0,40)):'none';})(document.activeElement)";

    private async Task<bool> TabUntilAsync(string condition, int maxPresses = 60)
    {
        for (var i = 0; i < maxPresses; i++)
        {
            await _edge.Edge.PressAsync("Tab");
            if (await _edge.Edge.EvaluateAsync<bool>($"(function(e){{return !!e && ({condition});}})(document.activeElement)")) return true;
        }
        return false;
    }

    private Task<bool> VisibleAsync(string selector) =>
        _edge.Edge.EvaluateAsync<bool>($"(function(el){{return !!el && el.getClientRects().length>0 && getComputedStyle(el).visibility!=='hidden';}})(document.querySelector('{selector}'))");

    // Audit finding 1 (WCAG 2.1.1): on a desktop the drop-down menus opened only under the mouse, so
    // the pages under Contact (and the ten under Resources on www.4ipro.com) never got keyboard focus.
    [Theory]
    [MemberData(nameof(Menus))]
    public async Task A_keyboard_opens_a_desktop_submenu_reaches_its_pages_and_Escape_closes_it(string design, bool topMenu)
    {
        var site = PublicSitePages.Build(design, tweak: topMenu ? w =>
        {
            Assert.Contains("\"sidebar\"", w.Template.LayoutJson);
            w.Template.LayoutJson = w.Template.LayoutJson.Replace("\"sidebar\"", "\"light\"");
        } : null);
        await _edge.OpenAsync(await PublicSitePages.RenderAsync(site.Model("home")), $"{design}-menu", 1280);
        const string toggle = "button[aria-controls=\"public-site-sub-2\"]";
        const string submenu = "#public-site-sub-2";

        Assert.False(await VisibleAsync(submenu), "the Contact submenu should start closed");
        Assert.True(await TabUntilAsync($"e.matches('{toggle.Replace("\"", "\\\"")}')"),
            $"{design}: Tab never reached the Contact submenu button; focus ended on {await _edge.Edge.EvaluateAsync<string>(ActiveDescription)}");
        Assert.Equal("false", await _edge.Edge.EvaluateAsync<string>($"document.querySelector('{toggle}').getAttribute('aria-expanded')"));

        await _edge.Edge.PressAsync("Enter");
        Assert.True(await VisibleAsync(submenu), $"{design}: Enter on the submenu button did not open the submenu");
        Assert.Equal("true", await _edge.Edge.EvaluateAsync<string>($"document.querySelector('{toggle}').getAttribute('aria-expanded')"));

        await _edge.Edge.PressAsync("Tab");
        Assert.True(await _edge.Edge.EvaluateAsync<bool>($"document.querySelector('{submenu}').contains(document.activeElement)"),
            $"{design}: the next Tab did not go into the open submenu; focus is on {await _edge.Edge.EvaluateAsync<string>(ActiveDescription)}");
        Assert.Equal("/directions", await _edge.Edge.EvaluateAsync<string>("document.activeElement.getAttribute('href')"));

        await _edge.Edge.PressAsync("Escape");
        Assert.False(await VisibleAsync(submenu), $"{design}: Escape did not close the submenu");
        Assert.True(await _edge.Edge.EvaluateAsync<bool>($"document.activeElement === document.querySelector('{toggle}')"),
            $"{design}: Escape did not return focus to the submenu button");
        Assert.Equal("false", await _edge.Edge.EvaluateAsync<string>($"document.querySelector('{toggle}').getAttribute('aria-expanded')"));

        // Opened again and left by Tab: it closes behind the keyboard.
        await _edge.Edge.PressAsync("Enter");
        Assert.True(await VisibleAsync(submenu));
        await _edge.Edge.PressAsync("Tab");
        await _edge.Edge.PressAsync("Tab");
        Assert.False(await VisibleAsync(submenu), $"{design}: the submenu stayed open after focus left it");
    }

    // WCAG 1.4.13: content that appears on hover can be dismissed. With the mouse resting on Contact, its
    // drop-down shows; Escape hides it until the pointer leaves.
    [Theory]
    [InlineData(PublicSitePages.Modern)]
    [InlineData(PublicSitePages.Editorial)]
    public async Task Escape_closes_a_submenu_the_mouse_rests_on(string design)
    {
        var site = PublicSitePages.Build(design);
        await _edge.OpenAsync(await PublicSitePages.RenderAsync(site.Model("home")), $"{design}-hover", 1280);
        var point = await _edge.Edge.EvaluateAsync<double[]>(
            "(function(r){return [r.left + r.width / 2, r.top + r.height / 2];})(document.querySelector('a[href=\"/contact\"]').getBoundingClientRect())");
        var (x, y) = (point![0], point[1]);
        await _edge.Edge.MoveMouseAsync(x, y);
        Assert.True(await VisibleAsync("#public-site-sub-2"), "the mouse did not open the Contact submenu");
        await _edge.Edge.EvaluateAsync<bool>("(document.querySelector('a[href=\"/contact\"]').focus(), true)");
        await _edge.Edge.PressAsync("Escape");
        Assert.False(await VisibleAsync("#public-site-sub-2"), $"{design}: Escape left the submenu open under the mouse");
        await _edge.Edge.MoveMouseAsync(5, 800);
        await _edge.Edge.MoveMouseAsync(x, y);
        Assert.True(await VisibleAsync("#public-site-sub-2"), "the submenu did not come back for the mouse");
    }

    // Three levels: the Resources panel opens the same way where it is a drop-down panel.
    [Theory]
    [InlineData(PublicSitePages.Modern)]
    [InlineData(PublicSitePages.Editorial)]
    public async Task A_keyboard_opens_the_three_level_panel(string design)
    {
        var site = PublicSitePages.Build(design);
        await _edge.OpenAsync(await PublicSitePages.RenderAsync(site.Model("home")), $"{design}-mega", 1280);
        Assert.True(await TabUntilAsync("e.matches('button[aria-controls=\"public-site-sub-4\"]')"),
            $"{design}: Tab never reached the Resources submenu button");
        await _edge.Edge.PressAsync("Enter");
        Assert.True(await VisibleAsync("#public-site-sub-4"));
        await _edge.Edge.PressAsync("Tab");
        Assert.True(await _edge.Edge.EvaluateAsync<bool>("document.querySelector('#public-site-sub-4').contains(document.activeElement)"));
    }

    // Audit finding 7 (WCAG 2.4.3): Read More hid itself while it had focus, so focus fell to the top
    // of the page. It now goes to the form that appears, and Back to articles brings it back.
    [Theory]
    [MemberData(nameof(Designs))]
    public async Task Read_More_moves_focus_to_the_form_and_Back_returns_it(string design)
    {
        var site = PublicSitePages.Build(design);
        await _edge.OpenAsync(await PublicSitePages.RenderAsync(site.Model("resources")), $"{design}-dyk", 1280);
        await _edge.Edge.EvaluateAsync<bool>("(document.querySelectorAll('.dyk-readmore')[1].focus(), true)");
        await _edge.Edge.PressAsync("Enter");
        Assert.True(await _edge.Edge.EvaluateAsync<bool>("document.activeElement.classList.contains('dyk-form-intro')"),
            $"{design}: after Read More, focus is on {await _edge.Edge.EvaluateAsync<string>(ActiveDescription)}");
        await _edge.Edge.EvaluateAsync<bool>("(document.querySelector('.dyk-back-link').focus(), true)");
        await _edge.Edge.PressAsync("Enter");
        Assert.True(await _edge.Edge.EvaluateAsync<bool>("document.activeElement === document.querySelectorAll('.dyk-readmore')[1]"),
            $"{design}: after Back to articles, focus is on {await _edge.Edge.EvaluateAsync<string>(ActiveDescription)}");
    }

    // Audit finding 12 (WCAG 2.4.3): two Tabs after opening a photo, focus was behind the dark overlay.
    [Theory]
    [MemberData(nameof(Designs))]
    public async Task Tab_stays_inside_the_open_photo_viewer(string design)
    {
        var site = PublicSitePages.Build(design);
        await _edge.OpenAsync(await PublicSitePages.RenderAsync(site.Model("home")), $"{design}-lightbox", 1280);
        await _edge.Edge.EvaluateAsync<bool>("(document.querySelector('.site-gallery__item').focus(), true)");
        await _edge.Edge.PressAsync("Enter");
        Assert.False(await _edge.Edge.EvaluateAsync<bool>("document.getElementById('site-lightbox').hidden"), "the photo did not open");
        for (var press = 1; press <= 6; press++)
        {
            await _edge.Edge.PressAsync("Tab", shift: press > 4);
            Assert.True(await _edge.Edge.EvaluateAsync<bool>("document.getElementById('site-lightbox').contains(document.activeElement)"),
                $"{design}: Tab {press} left the photo viewer for {await _edge.Edge.EvaluateAsync<string>(ActiveDescription)}");
        }
        await _edge.Edge.PressAsync("Escape");
        Assert.True(await _edge.Edge.EvaluateAsync<bool>("document.getElementById('site-lightbox').hidden"));
    }

    // Audit finding 13 (WCAG 4.1.3): a calculator's results arrived without a word to a screen reader.
    [Fact]
    public async Task Calculator_results_are_announced()
    {
        var site = PublicSitePages.Build(PublicSitePages.Modern);
        await _edge.OpenAsync(await PublicSitePages.RenderAsync(site.Model("resources")), "calc", 1280);
        await _edge.Edge.EvaluateAsync<bool>("(document.querySelector('.calc-run').focus(), true)");
        await _edge.Edge.PressAsync("Enter");
        // The status is filled a moment after the results (so a repeat is read out again): wait for it.
        var status = "";
        for (var wait = 0; wait < 40 && !status.Contains("Monthly payment"); wait++)
        {
            if (wait > 0) await Task.Delay(50);
            status = await _edge.Edge.EvaluateAsync<string>(
                "(function(s){return s ? s.getAttribute('role') + '|' + s.textContent : 'none';})(document.querySelector('.calc-block [aria-live]'))") ?? "";
        }
        Assert.StartsWith("status|", status);
        Assert.Contains("Monthly payment", status);
    }

    // Audit finding 14 (WCAG 2.4.1): a skip link, the first thing a keyboard reaches, past the menu.
    [Theory]
    [MemberData(nameof(Designs))]
    public async Task The_first_Tab_offers_a_skip_link_that_leads_into_the_page(string design)
    {
        var site = PublicSitePages.Build(design);
        await _edge.OpenAsync(await PublicSitePages.RenderAsync(site.Model("contact")), $"{design}-skip", 1280);
        await _edge.Edge.PressAsync("Tab");
        Assert.True(await _edge.Edge.EvaluateAsync<bool>("document.activeElement.classList.contains('site-skip-link')"),
            $"{design}: the first Tab went to {await _edge.Edge.EvaluateAsync<string>(ActiveDescription)}");
        Assert.True(await _edge.Edge.EvaluateAsync<bool>("document.activeElement.getBoundingClientRect().top >= 0"), "the skip link stays out of sight when focused");
        await _edge.Edge.PressAsync("Enter");
        await _edge.Edge.PressAsync("Tab");
        Assert.True(await _edge.Edge.EvaluateAsync<bool>("document.getElementById('main-content').contains(document.activeElement)"),
            $"{design}: after the skip link, Tab went to {await _edge.Edge.EvaluateAsync<string>(ActiveDescription)}");
    }

    // Audit finding 15 (WCAG 2.4.11): with the sticky header on, tabbing back up the page hid what had
    // focus under the header.
    [Theory]
    [InlineData(PublicSitePages.Modern)]
    [InlineData(PublicSitePages.Editorial)]
    public async Task A_sticky_header_never_covers_what_has_focus(string design)
    {
        var site = PublicSitePages.Build(design, tweak: w =>
            w.HeaderSettingsJson = new WebsiteHeaderSettings { Sticky = true, ButtonText = "Book a call", ButtonUrl = "/contact" }.ToJson());
        await _edge.OpenAsync(await PublicSitePages.RenderAsync(site.Model("home")), $"{design}-sticky", 1280);
        await _edge.Edge.EvaluateAsync<bool>("(function(){var all=document.querySelectorAll('#main-content a[href], #main-content button'); all[all.length-1].focus(); return true;})()");
        var hidden = new List<string>();
        for (var press = 0; press < 25; press++)
        {
            await _edge.Edge.PressAsync("Tab", shift: true);
            // Covered = the header, not the focused element, is what lies at the element's centre.
            var covered = await _edge.Edge.EvaluateAsync<string>(
                "(function(){var e=document.activeElement, h=document.querySelector('.public-site-header');" +
                "if(!e||!h||h.contains(e)||e===document.body||e.tagName==='IFRAME')return '';var r=e.getBoundingClientRect();" +
                "var top=document.elementFromPoint(r.left+Math.min(r.width/2,20), r.top+Math.min(r.height/2,8));" +
                "return top && h.contains(top) ? (e.tagName+' '+(e.textContent||'').trim().slice(0,30)+' top '+Math.round(r.top)) : '';})()");
            if (!string.IsNullOrEmpty(covered)) hidden.Add(covered!);
        }
        Assert.True(hidden.Count == 0, $"{design}: focus went under the sticky header:\n" + string.Join("\n", hidden));
    }
}
