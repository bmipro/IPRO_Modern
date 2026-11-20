using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using IPRO.Web.Models;
using Xunit;

namespace IPRO.IntegrationTests.Accessibility;

// 529 (2026-11-19): the pop-up as a visitor meets it, in headless Edge on pages the real views render, with real
// key presses. On a computer the centred card is a modal dialog: focus goes into it and stays there, Escape
// closes it, and axe finds nothing. The corner box leaves the page usable and takes no focus. On a phone every
// layout is a small bar along the bottom (Google's rule for intrusive pop-ups), and "Sign up" opens the form.
public class PromotionalPopupKeyboard529Tests : IClassFixture<EdgeFixture>
{
    private readonly EdgeFixture _edge;
    public PromotionalPopupKeyboard529Tests(EdgeFixture edge) => _edge = edge;

    public static IEnumerable<object[]> Designs() => PublicSitePages.Designs.Select(d => new object[] { d });

    private const string Dialog = "document.querySelector('dialog.site-popup')";
    private const string Active =
        "(function(e){return e?(e.tagName+(e.id?'#'+e.id:'')+'.'+(e.className||'')+' '+(e.getAttribute('aria-label')||e.textContent||'').trim().slice(0,40)):'none';})(document.activeElement)";

    private static PublicWebsiteViewModel Page(string design, string layout, int delaySeconds = 0)
    {
        var model = PublicSitePages.Build(design).Model("home");
        model.Popup = new PublicPopupView
        {
            Settings = new WebsitePopup
            {
                Enabled = true, Layout = layout, Headline = "Join the Rivers letter", Text = "Tips a few times a year.",
                Trigger = "delay", DelaySeconds = delaySeconds
            }.Normalized()
        };
        return model;
    }

    private async Task<bool> WaitForAsync(string condition, int milliseconds = 3000)
    {
        for (var waited = 0; waited < milliseconds; waited += 100)
        {
            if (await _edge.Edge.EvaluateAsync<bool>(condition)) return true;
            await Task.Delay(100);
        }
        return false;
    }

    [Theory]
    [MemberData(nameof(Designs))]
    public async Task On_a_computer_the_card_is_a_modal_dialog_that_keeps_focus_and_Escape_closes_it(string design)
    {
        await _edge.OpenAsync(await PublicSitePages.RenderAsync(Page(design, "card")), $"{design}-popup-card", 1280);
        Assert.True(await WaitForAsync($"!!{Dialog}"), $"{design}: the pop-up did not open");
        Assert.True(await _edge.Edge.EvaluateAsync<bool>($"{Dialog}.matches(':modal')"), $"{design}: the card should be modal");
        Assert.True(await _edge.Edge.EvaluateAsync<bool>($"{Dialog}.contains(document.activeElement)"),
            $"{design}: focus did not go into the pop-up; it is on {await _edge.Edge.EvaluateAsync<string>(Active)}");

        var findings = await _edge.AxeOpenPageAsync();
        Assert.True(findings.Count == 0, $"{design}: axe on the open pop-up:\n" + string.Join("\n", findings));

        for (var i = 0; i < 16; i++)
        {
            await _edge.Edge.PressAsync("Tab");
            Assert.True(await _edge.Edge.EvaluateAsync<bool>($"document.activeElement===document.body||{Dialog}.contains(document.activeElement)"),
                $"{design}: Tab {i + 1} left the pop-up for {await _edge.Edge.EvaluateAsync<string>(Active)}");
        }

        await _edge.Edge.PressAsync("Escape");
        Assert.True(await WaitForAsync($"!{Dialog}", 1000), $"{design}: Escape did not close the pop-up");
    }

    [Fact]
    public async Task On_a_computer_the_corner_box_leaves_the_page_usable_and_takes_no_focus()
    {
        await _edge.OpenAsync(await PublicSitePages.RenderAsync(Page(PublicSitePages.Modern, "corner")), "popup-corner", 1280);
        Assert.True(await WaitForAsync($"!!{Dialog}"), "the pop-up did not open");
        Assert.False(await _edge.Edge.EvaluateAsync<bool>($"{Dialog}.matches(':modal')"));
        Assert.False(await _edge.Edge.EvaluateAsync<bool>($"{Dialog}.contains(document.activeElement)"), "the corner box should not take focus");
        Assert.True(await _edge.Edge.EvaluateAsync<bool>($"(function(r){{return r.right<=window.innerWidth&&r.bottom<=window.innerHeight&&r.width<=400;}})({Dialog}.getBoundingClientRect())"),
            "the corner box should sit in the bottom corner, inside the window");

        await _edge.Edge.PressAsync("Escape");
        Assert.True(await WaitForAsync($"!{Dialog}", 1000), "Escape did not close the corner box");
    }

    [Theory]
    [MemberData(nameof(Designs))]
    public async Task On_a_phone_it_is_a_small_bar_at_the_bottom_and_Sign_up_opens_the_form(string design)
    {
        await _edge.OpenAsync(await PublicSitePages.RenderAsync(Page(design, "card")), $"{design}-popup-phone", 390);
        try
        {
            Assert.True(await WaitForAsync($"!!{Dialog}"), $"{design}: the pop-up did not open");
            Assert.False(await _edge.Edge.EvaluateAsync<bool>($"{Dialog}.matches(':modal')"), $"{design}: never modal on a phone");
            Assert.True(await _edge.Edge.EvaluateAsync<bool>($"(function(r){{return Math.abs(r.bottom-window.innerHeight)<2&&r.height<window.innerHeight*0.3;}})({Dialog}.getBoundingClientRect())"),
                $"{design}: on a phone it should be a small bar along the bottom; it is {await _edge.Edge.EvaluateAsync<string>($"JSON.stringify({Dialog}.getBoundingClientRect())")} in a window {await _edge.Edge.EvaluateAsync<int>("window.innerHeight")} high");
            Assert.False(await _edge.Edge.EvaluateAsync<bool>($"{Dialog}.querySelector('.site-popup__form').getClientRects().length>0"), $"{design}: the form waits for Sign up");

            await _edge.Edge.EvaluateAsync<bool>($"({Dialog}.querySelector('[data-popup-expand]').focus(),true)");
            await _edge.Edge.PressAsync("Enter");
            Assert.True(await WaitForAsync($"{Dialog}.querySelector('.site-popup__form').getClientRects().length>0", 1000), $"{design}: Sign up did not open the form");
            Assert.Equal("FirstName", await _edge.Edge.EvaluateAsync<string>("document.activeElement.getAttribute('name')"));
            Assert.Equal("true", await _edge.Edge.EvaluateAsync<string>($"{Dialog}.querySelector('[data-popup-expand]').getAttribute('aria-expanded')"));
        }
        finally
        {
            await _edge.Edge.SetViewportAsync(1280, 900);
        }
    }

    [Fact]
    public async Task Until_its_moment_it_is_no_part_of_the_page()
    {
        await _edge.OpenAsync(await PublicSitePages.RenderAsync(Page(PublicSitePages.Classic, "card", delaySeconds: 60)), "popup-waiting", 1280);
        Assert.False(await _edge.Edge.EvaluateAsync<bool>($"!!{Dialog}"));
        Assert.False(await _edge.Edge.EvaluateAsync<bool>("document.body.innerText.indexOf('Join the Rivers letter')>=0"), "the headline should not be in the page's text before it opens");
        Assert.True(await _edge.Edge.EvaluateAsync<bool>("!!document.getElementById('site-popup-template')"));
    }
}
