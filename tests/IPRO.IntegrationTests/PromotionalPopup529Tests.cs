using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.IntegrationTests.Accessibility;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 529 (the owner, 2026-09-27; "every plan gets it", 2026-11-19; built 2026-11-19): the promotional pop-up on an
// adviser's website, written and switched on and off by the adviser on the Header & Menu page. It offers the
// site's newsletter sign-up or one or two buttons, opens after some seconds, at a scroll depth or on leaving, on
// the home page or every page, and a visitor who closed it is asked again only after the adviser's number of days
// (a year after signing up through it). Kept in the header settings beside the announcement bar.
public class PromotionalPopup529Tests
{
    private const string Headline = "Join the Rivers letter";

    private static WebsitePopup SignUp(Action<WebsitePopup>? tweak = null)
    {
        var popup = new WebsitePopup { Enabled = true, Headline = Headline, Text = "Tips a few times a year.\nWe respect your privacy." };
        tweak?.Invoke(popup);
        return popup.Normalized();
    }

    // ---- the settings ---------------------------------------------------------------------------------

    [Fact]
    public void Old_header_settings_have_no_popup_and_a_saved_popup_lives_beside_the_bar()
    {
        var old = WebsiteHeaderSettings.FromJson("{\"Style\":\"compact\",\"Announcement\":{\"Enabled\":true,\"Message\":\"Closed Dec 24\"}}");
        Assert.False(old.Popup.IsShown);
        Assert.Equal(("card", "signup", "delay", 5, true, 7), (old.Popup.Layout, old.Popup.Action, old.Popup.Trigger, old.Popup.DelaySeconds, old.Popup.HomePageOnly, old.Popup.RepeatDays));

        old.Popup = SignUp(p => p.Layout = "corner");
        var back = WebsiteHeaderSettings.FromJson(old.ToJson());
        Assert.True(back.Popup.IsShown);
        Assert.Equal("corner", back.Popup.Layout);
        Assert.True(back.Announcement.IsShown);   // the bar and the header are untouched
        Assert.Equal("compact", back.Style);
    }

    [Fact]
    public void Every_choice_is_one_of_the_offered_ones_and_the_words_are_trimmed_to_their_limits()
    {
        var popup = new WebsitePopup
        {
            Layout = "fullscreen", Action = "coupon", Trigger = "random", DelaySeconds = 999, ScrollPercent = 33, RepeatDays = 2,
            Headline = "Join\r\n   our  list " + new string('x', 200),
            Text = "First line\r\n\r\n   second   line  \n\n\nthird" + new string('y', 600),
            ImageAssetId = 0
        }.Normalized();
        Assert.Equal(("card", "signup", "delay", 60, 50, 7), (popup.Layout, popup.Action, popup.Trigger, popup.DelaySeconds, popup.ScrollPercent, popup.RepeatDays));
        Assert.StartsWith("Join our list x", popup.Headline);
        Assert.True(popup.Headline.Length <= WebsitePopup.MaxHeadlineLength);
        Assert.StartsWith("First line\nsecond line\nthird", popup.Text);
        Assert.True(popup.Text.Length <= WebsitePopup.MaxTextLength);
        Assert.Equal(3, popup.Paragraphs.Count);
        Assert.Null(popup.ImageAssetId);
        Assert.Equal(0, new WebsitePopup { DelaySeconds = -4 }.Normalized().DelaySeconds);
    }

    [Fact]
    public void A_button_needs_words_and_a_safe_link_and_the_popup_needs_a_headline_and_something_to_do()
    {
        var buttons = new WebsitePopup
        {
            Enabled = true, Headline = "Seminar", Action = "buttons",
            Button1Text = "Book a seat", Button1Url = "javascript:alert(1)",
            Button2Text = "", Button2Url = "/contact"
        }.Normalized();
        Assert.Equal(string.Empty, buttons.Button1Url);
        Assert.Empty(buttons.Buttons);
        Assert.False(buttons.IsShown);

        buttons.Button1Url = "/seminars";
        buttons.Normalized();
        Assert.Equal(new[] { ("Book a seat", "/seminars") }, buttons.Buttons);
        Assert.True(buttons.IsShown);

        Assert.False(SignUp(p => p.Headline = "").IsShown);   // nothing to name the dialog
        Assert.False(SignUp(p => p.Enabled = false).IsShown);
        Assert.Empty(SignUp(p => { p.Button1Text = "Hidden"; p.Button1Url = "/x"; }).Buttons);   // a sign-up shows no buttons
    }

    [Fact]
    public void Changing_what_it_says_gives_it_a_new_key_and_changing_when_or_where_does_not()
    {
        var a = SignUp();
        Assert.Matches("^[0-9a-f]{8}$", a.SeenKey);
        Assert.NotEqual(a.SeenKey, SignUp(p => p.Headline = "Join our list today").SeenKey);
        Assert.NotEqual(a.SeenKey, SignUp(p => p.ImageAssetId = 5).SeenKey);
        Assert.Equal(a.SeenKey, SignUp(p => { p.Layout = "bar"; p.Trigger = "exit"; p.DelaySeconds = 30; p.HomePageOnly = false; p.RepeatDays = 30; }).SeenKey);
    }

    [Theory]
    [InlineData(WebsiteLeadTypes.Newsletter, 0, "popup")]
    [InlineData(WebsiteLeadTypes.Newsletter, 12, "newsletter")]
    [InlineData(WebsiteLeadTypes.Newsletter, null, "newsletter")]
    [InlineData(WebsiteLeadTypes.Contact, 0, "contact")]
    public void Only_a_sign_up_through_the_popups_own_form_comes_back_as_popup(string type, int? blockId, string expected)
    {
        Assert.Equal(expected, PublicWebsiteController.SubmittedResult(new WebsiteLeadFormViewModel { BlockId = blockId }, type));
        Assert.Equal(PublicFormRetry.LeadForm(WebsiteLeadTypes.Newsletter, WebsitePopup.FormBlockId),
            PublicFormRetry.LeadForm(new WebsiteLeadFormViewModel { BlockId = WebsitePopup.FormBlockId }, WebsiteLeadTypes.Newsletter));
    }

    // ---- every plan, and SuperAdmin's untick holds ----------------------------------------------------

    [Fact]
    public async Task Every_package_gets_the_line_including_ones_the_catalogue_does_not_know_and_an_untick_is_kept()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var custom = new BillingRule { PackageName = $"Trial-{Guid.NewGuid():N}"[..20], MonthlyPrice = 0m, IsActive = true };
        db.Add(custom);
        await db.SaveChangesAsync();

        await PackageEntitlementSeeder.SeedAsync(db);
        var rows = await db.PackageFeatures.AsNoTracking().Where(f => f.FeatureCode == PackageFeatureCodes.PromotionalPopup).ToListAsync();
        Assert.Equal(await db.BillingRules.CountAsync(), rows.Count);
        Assert.All(rows, r => Assert.True(r.IsIncluded));
        Assert.Contains(rows, r => r.BillingRuleId == custom.Id);
        Assert.All(rows, r => Assert.Equal("Promotional pop-up on your website", r.FeatureName));

        var row = await db.PackageFeatures.SingleAsync(f => f.BillingRuleId == custom.Id && f.FeatureCode == PackageFeatureCodes.PromotionalPopup);
        row.IsIncluded = false;
        await db.SaveChangesAsync();
        await PackageEntitlementSeeder.SeedAsync(db);
        Assert.Equal(rows.Count, await db.PackageFeatures.CountAsync(f => f.FeatureCode == PackageFeatureCodes.PromotionalPopup));
        Assert.False(await db.PackageFeatures.Where(f => f.BillingRuleId == custom.Id && f.FeatureCode == PackageFeatureCodes.PromotionalPopup).Select(f => f.IsIncluded).SingleAsync());
    }

    // ---- the live page ------------------------------------------------------------------------------

    [Fact]
    public async Task The_home_page_carries_it_until_this_visitor_closes_it_and_a_changed_popup_shows_again()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var popup = SignUp();
        var seed = await SeedSiteAsync(db, popup);

        var model = await RenderAsync(db, seed.Host, new Grant(true), slug: null, cookie: null);
        Assert.NotNull(model.Popup);
        Assert.Equal(Headline, model.Popup!.Settings.Headline);
        Assert.Equal(string.Empty, model.Popup.OpenAs);

        Assert.Null((await RenderAsync(db, seed.Host, new Grant(true), slug: null, cookie: popup.SeenKey)).Popup);
        Assert.NotNull((await RenderAsync(db, seed.Host, new Grant(true), slug: null, cookie: "0badc0de")).Popup);

        await ChangeAsync(db, seed.WebsiteId, p => p.Headline = "Join our list for 2027");
        Assert.NotNull((await RenderAsync(db, seed.Host, new Grant(true), slug: null, cookie: popup.SeenKey)).Popup);
    }

    [Fact]
    public async Task Home_page_only_means_the_home_page_and_every_page_means_every_page()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedSiteAsync(db, SignUp());

        Assert.NotNull((await RenderAsync(db, seed.Host, new Grant(true), slug: "home", cookie: null)).Popup);
        Assert.Null((await RenderAsync(db, seed.Host, new Grant(true), slug: "about", cookie: null)).Popup);

        await ChangeAsync(db, seed.WebsiteId, p => p.HomePageOnly = false);
        Assert.NotNull((await RenderAsync(db, seed.Host, new Grant(true), slug: "about", cookie: null)).Popup);
        Assert.Null((await RenderAsync(db, seed.Host, new Grant(true), slug: "no-such-page", cookie: null)).Popup);   // a 404 has none
    }

    [Fact]
    public async Task After_a_sign_up_through_it_it_comes_back_to_say_thank_you_whatever_the_cookie_and_the_page()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var popup = SignUp();
        var seed = await SeedSiteAsync(db, popup);

        var model = await RenderAsync(db, seed.Host, new Grant(true), slug: "about", cookie: popup.SeenKey, query: "?submitted=popup");
        Assert.NotNull(model.Popup);
        Assert.Equal("done", model.Popup!.OpenAs);
        // A newsletter block's own sign-up says "newsletter", which the pop-up leaves alone.
        Assert.Null((await RenderAsync(db, seed.Host, new Grant(true), slug: "about", cookie: null, query: "?submitted=newsletter")).Popup);
    }

    [Fact]
    public async Task A_package_without_the_line_shows_none_and_one_that_is_off_costs_the_page_no_package_question()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedSiteAsync(db, SignUp());
        Assert.Null((await RenderAsync(db, seed.Host, new Grant(false), slug: null, cookie: null)).Popup);

        await ChangeAsync(db, seed.WebsiteId, p => p.Enabled = false);
        var counting = new Grant(true);
        Assert.Null((await RenderAsync(db, seed.Host, counting, slug: null, cookie: null)).Popup);
        Assert.Equal(0, counting.PopupQuestions);
    }

    [Fact]
    public async Task Its_picture_comes_from_the_sites_own_library_with_the_pictures_description()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedSiteAsync(db, SignUp());
        var own = new WebsiteMediaAsset { AgentWebsiteId = seed.WebsiteId, OriginalFileName = "team.jpg", BlobUrl = "https://blob.example.test/website-media/team.jpg", ContentType = "image/jpeg", Description = "Our team at the office" };
        var other = await SeedSiteAsync(db, SignUp());
        var foreign = new WebsiteMediaAsset { AgentWebsiteId = other.WebsiteId, OriginalFileName = "x.jpg", BlobUrl = "https://blob.example.test/website-media/x.jpg", ContentType = "image/jpeg" };
        db.AddRange(own, foreign);
        await db.SaveChangesAsync();

        await ChangeAsync(db, seed.WebsiteId, p => p.ImageAssetId = own.Id);
        var shown = (await RenderAsync(db, seed.Host, new Grant(true), slug: null, cookie: null)).Popup!;
        Assert.Equal((own.BlobUrl, "Our team at the office"), (shown.ImageUrl, shown.ImageAlt));

        await ChangeAsync(db, seed.WebsiteId, p => p.ImageAssetId = foreign.Id);
        Assert.Equal(string.Empty, (await RenderAsync(db, seed.Host, new Grant(true), slug: null, cookie: null)).Popup!.ImageUrl);
    }

    // ---- the adviser's card -------------------------------------------------------------------------

    [Fact]
    public async Task The_adviser_saves_it_and_is_told_what_was_not_kept()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedSiteAsync(db, new WebsitePopup(), popupLine: true);
        db.Add(new WebsiteMediaAsset { AgentWebsiteId = seed.WebsiteId, OriginalFileName = "a.jpg", BlobUrl = "https://blob.example.test/a.jpg", ContentType = "image/jpeg" });
        await db.SaveChangesAsync();
        var picture = await db.WebsiteMediaAssets.Where(a => a.AgentWebsiteId == seed.WebsiteId).Select(a => a.Id).SingleAsync();

        var controller = PagesController(db, seed.AgentId);
        var result = Assert.IsType<RedirectToActionResult>(await controller.SavePopup(new WebsitePopupForm
        {
            Enabled = true, Headline = "Free seminar", Text = "May 4 at 7 pm.", Action = "buttons", Layout = "corner",
            Button1Text = "Book a seat", Button1Url = "/seminars", Button2Text = "Call us", Button2Url = "javascript:x()",
            Trigger = "scroll", ScrollPercent = 75, Pages = "all", RepeatDays = 14, ImageAssetId = picture
        }));
        Assert.Equal(("Navigation", "popup"), (result.ActionName, result.Fragment));
        Assert.Contains("The second button's link was not kept", controller.TempData[WebsitePagesController.PopupSavedKey] as string);
        Assert.Equal("warning", controller.TempData[WebsitePagesController.PopupSavedToneKey]);
        Assert.Null(controller.TempData["Warning"]);   // said in the card, not in the banner at the top of the page

        db.ChangeTracker.Clear();
        var saved = WebsiteHeaderSettings.FromJson((await db.AgentWebsites.SingleAsync(w => w.Id == seed.WebsiteId)).HeaderSettingsJson).Popup;
        Assert.True(saved.IsShown);
        Assert.Equal(("corner", "buttons", "scroll", 75, false, 14, (int?)picture), (saved.Layout, saved.Action, saved.Trigger, saved.ScrollPercent, saved.HomePageOnly, saved.RepeatDays, saved.ImageAssetId));
        Assert.Equal(new[] { ("Book a seat", "/seminars") }, saved.Buttons);

        // No headline: saved, and the card's status line says what it still needs.
        controller = PagesController(db, seed.AgentId);
        await controller.SavePopup(new WebsitePopupForm { Enabled = true, Headline = "  ", Action = "signup" });
        Assert.Equal(("Pop-up saved.", "success"), (controller.TempData[WebsitePagesController.PopupSavedKey] as string, controller.TempData[WebsitePagesController.PopupSavedToneKey] as string));
        db.ChangeTracker.Clear();
        saved = WebsiteHeaderSettings.FromJson((await db.AgentWebsites.SingleAsync(w => w.Id == seed.WebsiteId)).HeaderSettingsJson).Popup;
        Assert.Equal((true, false, "it needs a headline"), (saved.Enabled, saved.IsShown, saved.MissingPart));
    }

    [Fact]
    public async Task A_package_without_the_line_cannot_save_it_and_another_sites_picture_is_refused()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var without = await SeedSiteAsync(db, new WebsitePopup(), popupLine: false);
        var controller = PagesController(db, without.AgentId);
        await controller.SavePopup(new WebsitePopupForm { Enabled = true, Headline = "Hello" });
        Assert.NotNull(controller.TempData["Error"]);
        db.ChangeTracker.Clear();
        Assert.False(WebsiteHeaderSettings.FromJson((await db.AgentWebsites.SingleAsync(w => w.Id == without.WebsiteId)).HeaderSettingsJson).Popup.Enabled);

        var with = await SeedSiteAsync(db, new WebsitePopup(), popupLine: true);
        var foreign = new WebsiteMediaAsset { AgentWebsiteId = without.WebsiteId, OriginalFileName = "x.jpg", BlobUrl = "https://blob.example.test/x.jpg", ContentType = "image/jpeg" };
        db.Add(foreign);
        await db.SaveChangesAsync();
        controller = PagesController(db, with.AgentId);
        await controller.SavePopup(new WebsitePopupForm { Enabled = true, Headline = "Hello", ImageAssetId = foreign.Id });
        Assert.Contains("no longer in your media library", controller.TempData[WebsitePagesController.PopupSavedKey] as string);
        db.ChangeTracker.Clear();
        Assert.Null(WebsiteHeaderSettings.FromJson((await db.AgentWebsites.SingleAsync(w => w.Id == with.WebsiteId)).HeaderSettingsJson).Popup.ImageAssetId);
    }

    [Fact]
    public async Task The_preview_opens_the_saved_popup_at_once_even_while_it_is_switched_off()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedSiteAsync(db, SignUp(p => p.Enabled = false), popupLine: true);

        var view = Assert.IsType<ViewResult>(await PagesController(db, seed.AgentId).Preview(seed.HomePageId, popup: true));
        var model = Assert.IsType<PublicWebsiteViewModel>(view.Model);
        Assert.NotNull(model.Popup);
        Assert.Equal("now", model.Popup!.OpenAs);
        Assert.Equal(true, view.ViewData["IsTemplatePreview"]);

        // Any other preview of the page leaves it out.
        var plain = Assert.IsType<ViewResult>(await PagesController(db, seed.AgentId).Preview(seed.HomePageId));
        Assert.Null(Assert.IsType<PublicWebsiteViewModel>(plain.Model).Popup);
    }

    // The owner's first try on staging (2026-11-19): switched on, no headline, saved, and "nothing popped up". The
    // save's warning had gone to the banner at the top of the page, out of sight once the page returned to the
    // card. Now the card itself says the save and, always, whether the pop-up shows and what it still needs.
    [Fact]
    public async Task The_card_says_the_save_where_the_page_returns_and_whether_it_shows()
    {
        var html = await RenderCardAsync(SignUp(), ("Pop-up saved.", "success"));
        var card = html[html.IndexOf("id=\"popup\"", StringComparison.Ordinal)..];
        var form = card.IndexOf("action=\"/portal/WebsitePages/SavePopup\"", StringComparison.Ordinal);
        var said = card.IndexOf("<div class=\"alert alert-success py-2 mb-2\" role=\"status\">", StringComparison.Ordinal);
        Assert.True(said > 0 && said < form, "the save is said inside the card, above its form");
        Assert.Contains("Pop-up saved.", card[said..form]);
        Assert.Contains("<strong>On your website now:</strong> showing on your home page, 5 seconds after the page opens.", card);
        Assert.Contains("popup-status--on", card);

        html = await RenderCardAsync(SignUp(p => p.Headline = string.Empty), null);
        card = html[html.IndexOf("id=\"popup\"", StringComparison.Ordinal)..];
        Assert.Contains("<strong>On your website now:</strong> not showing yet, it needs a headline.", card);
        Assert.DoesNotContain("Pop-up saved.", card);   // said once, just after a save

        html = await RenderCardAsync(SignUp(p => { p.Action = "buttons"; p.Button1Text = "Book a seat"; p.Trigger = "scroll"; p.HomePageOnly = false; }), ("Pop-up saved. The first button needs a link, so it is not shown.", "warning"));
        Assert.Contains("<div class=\"alert alert-warning py-2 mb-2\" role=\"status\">", html);
        Assert.Contains("not showing yet, it needs a button with words and a link (or offer your newsletter sign-up instead).", html);

        html = await RenderCardAsync(SignUp(p => { p.Action = "buttons"; p.Button1Text = "Book a seat"; p.Button1Url = "/seminars"; p.Trigger = "scroll"; p.ScrollPercent = 75; p.HomePageOnly = false; }), null);
        Assert.Contains("showing on every page, once a visitor has scrolled 75% of the page.", html);

        html = await RenderCardAsync(SignUp(p => p.Enabled = false), null);
        Assert.Contains("<strong>On your website now:</strong> not showing (switched off).", html);
        Assert.Contains("popup-status--off", html);

        // The browser asks for a headline at the Headline box before a switched-on pop-up is saved.
        Assert.Contains("const sync = () => { headline.required = on.checked; };", html);
        Assert.Contains("<script nonce=", html);
    }

    [Fact]
    public void Its_status_words_cover_every_way_it_can_be_hidden()
    {
        Assert.Equal(string.Empty, SignUp().MissingPart);
        Assert.Equal(string.Empty, SignUp(p => { p.Enabled = false; p.Headline = string.Empty; }).MissingPart);
        Assert.Equal("it needs a headline", SignUp(p => p.Headline = "   ").MissingPart);
        Assert.StartsWith("it needs a button", SignUp(p => { p.Action = "buttons"; p.Button1Url = "/x"; }).MissingPart);
        foreach (var popup in new[] { SignUp(), SignUp(p => p.Enabled = false), SignUp(p => p.Headline = ""), SignUp(p => p.Action = "buttons") })
            Assert.Equal(popup.IsShown || !popup.Enabled, popup.MissingPart.Length == 0);

        Assert.Equal("as the page opens", SignUp(p => p.DelaySeconds = 0).OpensWhen);
        Assert.Equal("1 second after the page opens", SignUp(p => p.DelaySeconds = 1).OpensWhen);
        Assert.Equal("when a visitor is about to leave the page (on phones, halfway down)", SignUp(p => p.Trigger = "exit").OpensWhen);
    }

    // ---- the screens --------------------------------------------------------------------------------

    public static IEnumerable<object[]> Designs() => PublicSitePages.Designs.Select(d => new object[] { d });

    [Theory]
    [MemberData(nameof(Designs))]
    public async Task Each_design_puts_it_last_in_a_template_with_the_sites_newsletter_form(string design)
    {
        var site = PublicSitePages.Build(design);
        var model = site.Model("home");
        model.Popup = new PublicPopupView { Settings = SignUp(), ImageUrl = "https://blob.example.test/team.jpg", ImageAlt = "Our team" };
        var html = await PublicSitePages.RenderAsync(model);

        var template = html.IndexOf("<template id=\"site-popup-template\"", StringComparison.Ordinal);
        var templateEnd = html.IndexOf("</template>", template, StringComparison.Ordinal);
        Assert.True(template > html.IndexOf("</footer>", StringComparison.Ordinal), $"{design}: the pop-up comes after the footer");
        Assert.True(html.IndexOf(Headline, StringComparison.Ordinal) > template, $"{design}: the headline is only inside the template, no part of the page's text");
        var inside = html[template..templateEnd];
        Assert.Contains("aria-labelledby=\"site-popup-title\"", inside);
        Assert.Contains("<img class=\"site-popup__image\" src=\"https://blob.example.test/team.jpg\" alt=\"Our team\" />", inside);
        Assert.Contains("action=\"/PublicWebsite/SubmitLead\"", inside);
        Assert.Contains("<input type=\"hidden\" name=\"BlockId\" value=\"0\" />", inside);
        Assert.Contains("<input type=\"hidden\" name=\"SubmissionType\" value=\"Newsletter\" />", inside);
        Assert.Contains("I agree to receive newsletters and understand I can unsubscribe later.", inside);
        Assert.Contains("data-popup-open=\"\"", inside);
        Assert.Contains("<p>We respect your privacy.</p>", inside);
        Assert.DoesNotContain("disabled=\"disabled\"", inside);   // a live form
    }

    [Fact]
    public async Task Buttons_thanks_a_turned_down_sign_up_and_the_preview_each_draw_what_they_should()
    {
        var site = PublicSitePages.Build(PublicSitePages.Modern);

        var model = site.Model("home");
        model.Popup = new PublicPopupView { Settings = SignUp(p => { p.Action = "buttons"; p.Button1Text = "Book a seat"; p.Button1Url = "/seminars"; p.Button2Text = "Call us"; p.Button2Url = "/contact"; }) };
        var html = await PublicSitePages.RenderAsync(model);
        Assert.Contains("<a class=\"site-popup__button\" href=\"/seminars\" data-popup-went>Book a seat</a>", html);
        Assert.Contains("<a class=\"site-popup__button\" href=\"/contact\" data-popup-went>Call us</a>", html);
        Assert.DoesNotContain("name=\"BlockId\" value=\"0\"", html);

        model = site.Model("home");
        model.Popup = new PublicPopupView { Settings = SignUp(), OpenAs = "done" };
        html = await PublicSitePages.RenderAsync(model);
        Assert.Contains("data-popup-open=\"done\"", html);
        Assert.Contains("You are subscribed. Thank you for staying connected.", html);
        Assert.DoesNotContain("name=\"BlockId\" value=\"0\"", html);

        // A sign-up the server turned down comes back in the pop-up, open, with the visitor's email kept.
        model = site.Model("home");
        model.Popup = new PublicPopupView { Settings = SignUp() };
        var retry = new PublicFormRetry { Form = PublicFormRetry.LeadForm(WebsiteLeadTypes.Newsletter, WebsitePopup.FormBlockId), Message = "Nothing was sent yet." }
            .Keep("Email", "pat@example.test", 200).Problem("CaptchaAnswer", "Security check", "Answer the new question.");
        html = await PublicSitePages.RenderAsync(model, new PublicSitePages.RenderOptions { Retry = retry });
        var template = html.IndexOf("<template id=\"site-popup-template\"", StringComparison.Ordinal);
        Assert.Contains("data-popup-open=\"retry\"", html);
        Assert.True(html.IndexOf("Nothing was sent yet.", StringComparison.Ordinal) > template, "the message is in the pop-up");
        Assert.Contains("value=\"pat@example.test\"", html[template..]);

        model = site.Model("home");
        model.Popup = new PublicPopupView { Settings = SignUp(p => { p.Trigger = "scroll"; p.ScrollPercent = 75; }), OpenAs = "now" };
        var options = new PublicSitePages.RenderOptions();
        options.ViewBag["IsTemplatePreview"] = true;
        html = await PublicSitePages.RenderAsync(model, options);
        Assert.Contains("data-popup-open=\"now\"", html);
        Assert.Contains("data-popup-preview=\"true\"", html);
        Assert.Contains("Preview: on your website this opens once a visitor has scrolled 75% of the page, on your home page only.", html);
        Assert.Contains("disabled=\"disabled\"", html[html.IndexOf("<template id=\"site-popup-template\"", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void The_script_the_card_the_privacy_policy_and_the_guide_agree()
    {
        foreach (var shell in new[] { "_ClassicSidebar", "_EditorialVisual", "_ModernProfessional" })
        {
            var src = Read($@"src\IPRO.Web\Views\PublicWebsite\{shell}.cshtml");
            var popup = src.IndexOf("@await Html.PartialAsync(\"_PublicPopup\", Model)", StringComparison.Ordinal);
            Assert.True(popup > 0 && popup < src.IndexOf("</body>", StringComparison.Ordinal) && popup > src.IndexOf("Footer\", Model)", StringComparison.Ordinal), $"{shell}: the pop-up after the footer");
        }

        var partial = Read(@"src\IPRO.Web\Views\PublicWebsite\_PublicPopup.cshtml");
        Assert.Contains("<script nonce=\"@Context.GetCspNonce()\">", partial);
        Assert.Contains("@IPRO.Entities.WebsitePopup.SeenCookie=", partial);
        Assert.Contains("SameSite=Lax", partial);
        Assert.Contains("dialog.showModal()", partial);
        Assert.Contains("window.matchMedia('(max-width: 640px)')", partial);
        Assert.DoesNotContain("Html.Raw", partial);   // the adviser's words are plain text

        var card = Read(@"src\IPRO.Web\Views\WebsitePages\Navigation.cshtml");
        Assert.Contains("action=\"/portal/WebsitePages/SavePopup\"", card);
        Assert.Contains("Show the pop-up on my website", card);
        Assert.Contains("?popup=true", card);
        Assert.True(card.IndexOf("id=\"announcement-bar\"", StringComparison.Ordinal) < card.IndexOf("id=\"popup\"", StringComparison.Ordinal));

        Assert.Equal("ipro_popup_seen", WebsitePopup.SeenCookie);
        Assert.Contains("<code>ipro_popup_seen</code>", Read(@"src\IPRO.Web\Views\Shared\_LegalPrivacy.cshtml"));
        Assert.Contains("## Add a Pop-Up", Read(@"DOCS\04_WEBSITE_BUILDER.md"));
    }

    // ---- harness --------------------------------------------------------------------------------------

    private sealed record Seed(int AgentId, int WebsiteId, int HomePageId, string Host);

    private static async Task<Seed> SeedSiteAsync(IPRODbContext db, WebsitePopup popup, bool popupLine = true)
    {
        var rule = new BillingRule { PackageName = $"PU-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        await db.SaveChangesAsync();
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.InstantWebsite, FeatureName = "Website", IsIncluded = true });
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.PromotionalPopup, FeatureName = "Promotional pop-up on your website", IsIncluded = popupLine });
        var host = $"pu-{Guid.NewGuid():N}"[..14] + ".example.test";
        var agent = new AgentUser
        {
            UserName = $"pu-{Guid.NewGuid():N}"[..20],
            Email = $"pu-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Pop", LastName = "Up",
            DomainName = $"pu-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario", PackageId = rule.Id, TrialEndsAt = null
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        db.Add(new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 60m, Status = BillingStatus.Active, Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddDays(-40), NextBillingDate = DateTime.UtcNow.AddDays(20)
        });
        var template = new WebsiteTemplate { TemplateKey = $"tk-{Guid.NewGuid():N}"[..16], Name = "Test", BusinessType = "All" };
        db.Add(template);
        await db.SaveChangesAsync();
        var header = new WebsiteHeaderSettings { Popup = popup };
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, IsPublished = true, CustomDomain = host, HeaderSettingsJson = header.ToJson() };
        db.Add(website);
        await db.SaveChangesAsync();
        var home = new WebsitePage { AgentWebsiteId = website.Id, Title = "Home", Slug = "home", NavigationLabel = "Home", IsHomePage = true, SortOrder = 1 };
        db.AddRange(home, new WebsitePage { AgentWebsiteId = website.Id, Title = "About", Slug = "about", NavigationLabel = "About", SortOrder = 2 });
        db.Add(new AgentDomain { AgentUserId = agent.Id, AgentWebsiteId = website.Id, DomainName = host, WwwDomain = host, RootDomain = host, IsPrimary = true, OwnershipVerifiedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seed(agent.Id, website.Id, home.Id, host);
    }

    private static async Task ChangeAsync(IPRODbContext db, int websiteId, Action<WebsitePopup> change)
    {
        var site = await db.AgentWebsites.SingleAsync(w => w.Id == websiteId);
        var settings = WebsiteHeaderSettings.FromJson(site.HeaderSettingsJson);
        change(settings.Popup);
        settings.Popup.Normalized();
        site.HeaderSettingsJson = settings.ToJson();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<PublicWebsiteViewModel> RenderAsync(IPRODbContext db, string host, IPackageEntitlementService entitlements, string? slug, string? cookie, string? query = null)
    {
        var controller = new PublicWebsiteController(
            db, entitlements, new NullEmail(), NullLogger<PublicWebsiteController>.Instance,
            new ConfigurationBuilder().Build(), DataProtectionProvider.Create($"t529-{Guid.NewGuid():N}"),
            new NullBlob(), new MemoryCache(new MemoryCacheOptions()), new NullConsent());
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        if (query != null) context.Request.QueryString = new QueryString(query);
        if (cookie != null) context.Request.Headers.Cookie = $"{WebsitePopup.SeenCookie}={cookie}";
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, new NullTempData());
        var view = Assert.IsType<ViewResult>(slug == null ? await controller.Index() : await controller.Page(slug));
        return Assert.IsType<PublicWebsiteViewModel>(view.Model);
    }

    private static WebsitePagesController PagesController(IPRODbContext db, int agentId)
    {
        var controller = new WebsitePagesController(db, new PackageEntitlementService(new UnitOfWork(db), db), new NullBlob());
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString(CultureInfo.InvariantCulture)) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, new NullTempData());
        return controller;
    }

    private sealed class Grant : IPackageEntitlementService
    {
        private readonly bool _popup;
        public int PopupQuestions;
        public Grant(bool popup) { _popup = popup; }
        public Task<PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) =>
            Task.FromResult(new PackageFeatureAccess { FeatureCode = featureCode, IsIncluded = featureCode != PackageFeatureCodes.PromotionalPopup || _popup });
        public Task<bool> HasAccessAsync(int agentId, string featureCode)
        {
            if (featureCode == PackageFeatureCodes.PromotionalPopup) PopupQuestions++;
            return Task.FromResult(featureCode != PackageFeatureCodes.PromotionalPopup || _popup);
        }
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode) => throw new NotSupportedException();
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(false);
    }

    private sealed class NullEmail : IPRO.Email.IEmailService
    {
        public Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) => Task.FromResult(true);
        public Task<IPRO.Email.EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) => Task.FromResult(IPRO.Email.EmailSendResult.Sent());
        public Task<bool> SendBulkAsync(IEnumerable<IPRO.Email.EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
    }

    private sealed class NullBlob : IPRO.Utility.IBlobStorageService
    {
        public Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate) => Task.FromResult("https://blob.example.test/x");
        public Task<bool> DeleteAsync(string blobUrl) => Task.FromResult(true);
        public Task<Stream?> DownloadAsync(string blobUrl) => Task.FromResult<Stream?>(null);
        public Task<List<string>> ListAsync(string containerName) => Task.FromResult(new List<string>());
        public string GetPublicUrl(string containerName, string fileName) => $"https://blob.example.test/{containerName}/{fileName}";
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => Task.CompletedTask;
    }

    private sealed class NullConsent : IEmailConsentService
    {
        public bool IsSuppressed(Client client, EmailChannel channel, bool designSurvivesOptOut = false) => false;
        public Task<SuppressionResult> SuppressAllAsync(Client client, string source) => throw new NotSupportedException();
        public Task ResubscribeAsync(Client client) => throw new NotSupportedException();
        public bool LiftBounceSuppression(Client client) => throw new NotSupportedException();
        public Task<int> CancelSuppressedDripEnrollmentsAsync(int batchLimit = 500) => Task.FromResult(0);
        public Task<string> GetOrCreateTokenAsync(Client client) => Task.FromResult("tok");
        public string BuildPreferencesUrl(string token) => $"https://example.test/prefs/{token}";
    }

    // The Header & Menu page from its compiled view, without the layout (as BuilderBeta585Tests renders portal
    // views), with what a save leaves in TempData.
    private static async Task<string> RenderCardAsync(WebsitePopup popup, (string Message, string Tone)? saved)
    {
        var model = new WebsiteNavigationViewModel
        {
            Website = new AgentWebsite(),
            Header = new WebsiteHeaderSettings { Popup = popup },
            Pages = new List<WebsitePage> { new() { Id = 7, Title = "Home", Slug = "home", NavigationLabel = "Home", IsHomePage = true, SortOrder = 1 } },
            AnnouncementAccess = new PackageFeatureAccess { IsIncluded = true },
            PopupAccess = new PackageFeatureAccess { IsIncluded = true }
        };
        var values = new Dictionary<string, object>();
        if (saved is { } s)
        {
            values[WebsitePagesController.PopupSavedKey] = s.Message;
            values[WebsitePagesController.PopupSavedToneKey] = s.Tone;
        }
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions
        {
            ApplicationName = typeof(WebsitePagesController).Assembly.GetName().Name,
            ContentRootPath = Path.Combine(RepoRootDir(), "src", "IPRO.Web"),
            EnvironmentName = "Production"
        });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(WebsitePagesController).Assembly);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Items["CspNonce"] = "n529";
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "render"));
        var actionContext = new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var engine = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Mvc.Razor.IRazorViewEngine>();
        var found = engine.GetView(executingFilePath: null, viewPath: "~/Views/WebsitePages/Navigation.cshtml", isMainPage: false);
        Assert.True(found.Success, "Navigation.cshtml was not found among the portal's compiled views");
        var viewData = new ViewDataDictionary<WebsiteNavigationViewModel>(
            new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary()) { Model = model };
        await using var writer = new StringWriter();
        await found.View!.RenderAsync(new Microsoft.AspNetCore.Mvc.Rendering.ViewContext(actionContext, found.View, viewData,
            new TempDataDictionary(http, new FixedTempData(values)), writer, new Microsoft.AspNetCore.Mvc.ViewFeatures.HtmlHelperOptions()));
        return writer.ToString();
    }

    private sealed class FixedTempData : ITempDataProvider
    {
        private readonly Dictionary<string, object> _values;
        public FixedTempData(Dictionary<string, object> values) { _values = values; }
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>(_values);
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static string RepoRootDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private sealed class NullTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
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
