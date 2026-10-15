using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests.Accessibility;

// 579 (2026-10-15): every customer website links an accessibility statement from its footer, and iPro's
// own pages link iPro's. A government web lead asked whether iPro's sites are accessible; the expected
// answer is a statement page and a way to ask for help, not an overlay widget. The customer's statement
// is written from what the site already has (its name, language and contact details), its request form
// is the site's own contact form, and it claims only what 578's checks showed: WCAG 2.1 AA is the aim,
// nothing more, until a person has checked the designs with a screen reader.
public class AccessibilityStatement579Tests : IClassFixture<EdgeFixture>
{
    private readonly EdgeFixture _edge;
    public AccessibilityStatement579Tests(EdgeFixture edge) => _edge = edge;

    public static IEnumerable<object[]> Designs() => PublicSitePages.Designs.Select(d => new object[] { d });

    private const string ActiveDescription =
        "(function(e){return e?(e.tagName+(e.id?'#'+e.id:'')+'.'+(e.className||'')+' '+(e.getAttribute('aria-label')||e.textContent||'').trim().slice(0,40)):'none';})(document.activeElement)";

    private static PublicWebsiteViewModel Statement(PublicSitePages.Site site)
    {
        var model = site.Model(null);
        model.AccessibilityStatement = true;
        return model;
    }

    private static Task<string> RenderStatementAsync(PublicSitePages.Site site) =>
        PublicSitePages.RenderAsync(Statement(site), new PublicSitePages.RenderOptions { Path = AccessibilityStatement.Path });

    private static void French(AgentWebsite website) => website.SiteLanguage = WebsiteLanguages.French;

    // Words a statement may not use until a screen-reader check has been done and a conformance report
    // written: it must never say the site meets the standard. The one sentence that says it does NOT is
    // set aside first (each test asserts it is there).
    private static readonly string[] Claims =
    {
        "conforms", "conformant", "compliant", "certified", "fully accessible", "meets wcag", "meets the standard", "meet wcag",
        "est conforme", "certifié", "entièrement accessible", "respecte la norme", "respecte entièrement"
    };

    private static void ClaimsNothing(string text, string disclaimer)
    {
        Assert.Contains(disclaimer, text);
        var rest = text.Replace(disclaimer, string.Empty, StringComparison.Ordinal);
        foreach (var claim in Claims) Assert.DoesNotContain(claim, rest, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ checked by axe and by keyboard --

    [Theory]
    [MemberData(nameof(Designs))]
    public async Task The_statement_passes_axe_in_English_and_French_on_a_desktop_and_a_phone(string design)
    {
        var findings = new List<string>();
        foreach (var (name, site) in new[]
        {
            ("en", PublicSitePages.Build(design)),
            ("fr", PublicSitePages.Build(design, tweak: French)),
            // The lightest offered colour: links and buttons on the statement use the site's colour.
            ("sky", PublicSitePages.Build(design, "#0EA5E9"))
        })
        {
            var html = await RenderStatementAsync(site);
            Assert.Contains("<main class=\"public-a11y\" id=\"main-content\">", html);
            findings.AddRange((await _edge.AxeAsync(html, $"{design}-statement-{name}")).Select(f => $"{name} desktop: {f}"));
            findings.AddRange((await _edge.AxeOpenPageAsync("page-has-heading-one")).Select(f => $"{name} desktop: {f}"));
            findings.AddRange((await _edge.AxeAsync(html, $"{design}-statement-{name}-phone", 375)).Select(f => $"{name} phone: {f}"));
            // WCAG 1.4.10: at 320 px (a 1280 px screen at 400%) nothing scrolls sideways.
            await _edge.OpenAsync(html, $"{design}-statement-{name}-320", 320);
            if (await _edge.Edge.EvaluateAsync<bool>("document.documentElement.scrollWidth > window.innerWidth"))
                findings.Add($"{name} 320 px: the page scrolls sideways ({await _edge.Edge.EvaluateAsync<int>("document.documentElement.scrollWidth")} px)");
        }
        Assert.True(findings.Count == 0, $"{design}: {findings.Count} findings\n" + string.Join("\n", findings));
    }

    [Theory]
    [MemberData(nameof(Designs))]
    public async Task A_keyboard_goes_through_the_whole_statement_and_its_form_to_the_footer_link(string design)
    {
        var site = PublicSitePages.Build(design);
        await _edge.OpenAsync(await RenderStatementAsync(site), $"{design}-statement-keys", 1280);

        await _edge.Edge.PressAsync("Tab");
        Assert.True(await _edge.Edge.EvaluateAsync<bool>("document.activeElement.classList.contains('site-skip-link')"),
            $"{design}: the first Tab went to {await _edge.Edge.EvaluateAsync<string>(ActiveDescription)}");
        await _edge.Edge.PressAsync("Enter");

        // From the skip link, every stop in order: the statement's own links, the form, then the footer.
        var stops = new List<string>();
        for (var press = 0; press < 80; press++)
        {
            await _edge.Edge.PressAsync("Tab");
            var stop = await _edge.Edge.EvaluateAsync<string>(
                "(function(e){if(!e||e===document.body)return 'body';" +
                "var visible=e.matches(':focus-visible')&&getComputedStyle(e).outlineStyle!=='none';" +
                "return (e.getAttribute('href')||e.getAttribute('name')||e.tagName)+(visible?'':' (no focus ring)');})(document.activeElement)");
            stops.Add(stop!);
            if (stop!.StartsWith(AccessibilityStatement.Path, StringComparison.Ordinal)) break;
        }
        var walk = string.Join(" > ", stops);
        // The statement's own phone and email come before its form (the footer has the same links after).
        Assert.InRange(stops.IndexOf("tel:416-555-0100"), 0, stops.IndexOf("FirstName") - 1);
        Assert.InRange(stops.IndexOf("mailto:dana@example.test"), 0, stops.IndexOf("FirstName") - 1);
        Assert.Contains("ConsentGiven", stops);
        Assert.True(stops.IndexOf("FirstName") < stops.IndexOf("ConsentGiven"), $"{design}: {walk}");
        Assert.True(stops.Last() == AccessibilityStatement.Path, $"{design}: Tab never reached the footer's Accessibility link with a focus ring: {walk}");
        Assert.DoesNotContain(stops, s => s.EndsWith("(no focus ring)", StringComparison.Ordinal));
        Assert.Equal("page", await _edge.Edge.EvaluateAsync<string>("document.activeElement.getAttribute('aria-current')"));
    }

    // ------------------------------------------------------------------ what the statement says --

    [Theory]
    [MemberData(nameof(Designs))]
    public async Task The_statement_names_the_aim_its_checks_and_its_limits_and_claims_nothing_more(string design)
    {
        var html = await RenderStatementAsync(PublicSitePages.Build(design));
        var text = WebUtility.HtmlDecode(html);

        Assert.Single(Regex.Matches(html, "<h1[ >]"));
        Assert.Contains("<h1>Accessibility</h1>", text);
        Assert.Contains("Rivers Financial wants everyone to be able to use this website", text);
        Assert.Contains("Web Content Accessibility Guidelines (WCAG) 2.1 at level AA", text);
        Assert.Contains("In October 2026 each design was checked against that standard with automated tests", text);
        Assert.Contains("The first thing the keyboard reaches is a “Skip to main content” link.", text);
        Assert.Contains("a picture we have not described in words, a video without captions, or a document to download, such as a PDF", text);
        Assert.Contains("work the way those companies made them", text);
        Assert.Contains("<html lang=\"en\">", html);
        Assert.DoesNotContain("lang=\"fr\"", html);
        Assert.Contains("Last updated: <time datetime=\"2026-10-15\">15 October 2026</time>", text);
        ClaimsNothing(text, "No one has yet checked the designs with a screen reader, so we do not claim that this website fully meets the standard.");

        // The page's own address in the tab, and kept out of search results like a 404: the same words
        // stand on every site.
        Assert.Contains("<title>Accessibility | Rivers Financial</title>", text);
        Assert.Contains("<meta name=\"robots\" content=\"noindex,follow\" />", html);
        Assert.DoesNotContain("rel=\"canonical\"", html);
        Assert.DoesNotContain("application/ld+json", html);
    }

    [Theory]
    [MemberData(nameof(Designs))]
    public async Task A_French_site_has_its_statement_in_French_and_says_iPros_own_words_are_English(string design)
    {
        var html = await RenderStatementAsync(PublicSitePages.Build(design, tweak: French));
        var text = WebUtility.HtmlDecode(html);

        Assert.Contains("<html lang=\"fr\">", html);
        Assert.Contains("<h1>Accessibilité</h1>", text);
        Assert.Contains("Règles pour l’accessibilité des contenus Web (WCAG) 2.1, niveau AA", text);
        Assert.Contains("«\u00a0Aller au contenu principal\u00a0»", text);
        Assert.Contains("sont en anglais pour l’instant", text);
        Assert.Contains("Téléphone\u00a0: <a href=\"tel:416-555-0100\">", text);
        Assert.Contains("Dernière mise à jour\u00a0: <time datetime=\"2026-10-15\">15 octobre 2026</time>", text);
        Assert.Contains("<title>Accessibilité | Rivers Financial</title>", text);
        ClaimsNothing(text, "Personne n’a encore vérifié les modèles avec un lecteur d’écran; nous n’affirmons donc pas que ce site respecte entièrement la norme.");
        // The form's words are iPro's English ones (578 did not translate them), marked as English.
        Assert.Matches("class=\"website-lead-form-shell\"[^>]*lang=\"en\"", html);
        Assert.Contains($">{AccessibilityStatement.LinkText(WebsiteLanguages.French)}</a>", text);
    }

    [Fact]
    public async Task The_request_form_is_the_sites_contact_form_and_comes_back_to_the_statement()
    {
        var html = await RenderStatementAsync(PublicSitePages.Build(PublicSitePages.Modern));
        var form = Regex.Match(html, "<form[^>]*action=\"/PublicWebsite/SubmitLead\"[\\s\\S]*?</form>");
        Assert.True(form.Success, "the statement has no form posting to the site's contact form");
        Assert.Contains("name=\"SubmissionType\" value=\"Contact\"", form.Value);
        Assert.Contains("name=\"ReturnPath\" value=\"/accessibility\"", form.Value);
        Assert.Contains("name=\"Message\"", form.Value);
        Assert.Contains("name=\"CaptchaToken\"", form.Value);
        Assert.Single(Regex.Matches(html, "action=\"/PublicWebsite/SubmitLead\""));
        Assert.Contains("This form goes straight to Rivers Financial.", WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task With_no_contact_details_in_the_footer_the_statement_gives_the_accounts_phone_but_never_its_email()
    {
        var site = PublicSitePages.Build(PublicSitePages.Classic, tweak: w =>
        {
            var footer = WebsiteFooterSettings.FromJson(w.FooterSettingsJson);
            footer.Phone = footer.Email = footer.Address = string.Empty;
            w.FooterSettingsJson = footer.ToJson();
            w.AgentUser.Phone = "905-555-0199";
            w.AgentUser.Email = "owner@example.test";
        });
        var text = WebUtility.HtmlDecode(await RenderStatementAsync(site));
        Assert.Contains("Phone: <a href=\"tel:905-555-0199\">905-555-0199</a>", text);
        // The account's email is its sign-in: on the site only where the owner turns it on.
        Assert.DoesNotContain("owner@example.test", Regex.Match(text, "<main class=\"public-a11y\"[\\s\\S]*?</main>").Value);
        Assert.DoesNotContain("Address:", text);
    }

    // The colour control allows any page background; a dark one is not adjusted (578 adjusts the site's
    // own colour), so a site that has one says so, and only such a site.
    [Theory]
    [InlineData(PublicSitePages.Modern, "#1B3A5C", true)]
    [InlineData(PublicSitePages.Editorial, "#222222", true)]
    [InlineData(PublicSitePages.Classic, "#f8f6f0", false)]
    [InlineData(PublicSitePages.Modern, "", false)]
    public async Task A_dark_page_background_is_named_as_a_limit_on_that_site_only(string design, string background, bool named)
    {
        foreach (var french in new[] { false, true })
        {
            var site = PublicSitePages.Build(design, tweak: w =>
            {
                w.BackgroundColorOverride = background;
                if (french) French(w);
            });
            var text = WebUtility.HtmlDecode(await RenderStatementAsync(site));
            var line = french ? "fond foncé du site" : "dark page background, a colour we chose ourselves";
            Assert.Equal(named, text.Contains(line, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task The_page_names_the_business_the_way_its_tab_does()
    {
        var site = PublicSitePages.Build(PublicSitePages.Editorial, tweak: w => w.SiteTitle = string.Empty);
        var text = WebUtility.HtmlDecode(await RenderStatementAsync(site));
        Assert.Contains("<title>Accessibility | Dana Rivers</title>", text);
        Assert.Contains("Dana Rivers wants everyone to be able to use this website", text);
        Assert.Contains("This form goes straight to Dana Rivers.", text);
    }

    [Theory]
    [InlineData("en", 2026, 10, 15, "15 October 2026")]
    [InlineData("fr", 2026, 10, 15, "15 octobre 2026")]
    [InlineData("en", 2027, 2, 1, "1 February 2027")]
    [InlineData("fr", 2027, 2, 1, "1er février 2027")]
    [InlineData("fr", 2027, 8, 21, "21 août 2027")]
    public void The_date_is_written_the_way_each_language_writes_it(string language, int year, int month, int day, string expected)
    {
        Assert.Equal(expected, AccessibilityStatement.Date(language, new DateOnly(year, month, day)));
    }

    [Fact]
    public async Task A_footer_with_only_a_phone_keeps_the_owners_email_unsaid()
    {
        var site = PublicSitePages.Build(PublicSitePages.Modern, tweak: w =>
        {
            var footer = WebsiteFooterSettings.FromJson(w.FooterSettingsJson);
            footer.Email = string.Empty;
            w.FooterSettingsJson = footer.ToJson();
            w.AgentUser.Email = "login-only@example.test";
        });
        var html = await RenderStatementAsync(site);
        var main = Regex.Match(html, "<main class=\"public-a11y\"[\\s\\S]*?</main>").Value;
        Assert.Contains("Phone: <a href=\"tel:416-555-0100\">416-555-0100</a>", main);
        Assert.DoesNotContain("login-only@example.test", main);
        Assert.DoesNotContain("Email:", main);
    }

    // ------------------------------------------------------------------ the footer link --

    [Theory]
    [MemberData(nameof(Designs))]
    public async Task Every_page_of_every_design_ends_its_footer_legal_links_with_the_statement(string design)
    {
        var site = PublicSitePages.Build(design);
        foreach (var slug in PublicSitePages.Slugs.Append("no-such-page"))
        {
            var html = await PublicSitePages.RenderAsync(site.Model(slug));
            var legal = Regex.Match(html, "<div class=\"public-footer__legal\">([\\s\\S]*?)</div>");
            Assert.True(legal.Success, $"{design} {slug}: no legal links row in the footer");
            var links = Regex.Matches(legal.Groups[1].Value, "<a href=\"([^\"]*)\"[^>]*>([^<]*)</a>").Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
            Assert.Equal(new[] { ("/privacy", "Privacy"), ("/accessibility", "Accessibility") }, links);
            Assert.DoesNotContain("aria-current", legal.Value);
        }
    }

    // Previews are drawn on the platform's host, where /accessibility is iPro's own statement.
    [Theory]
    [InlineData("IsTemplatePreview")]
    [InlineData("IsUnsavedPreview")]
    [InlineData("IsProspectPreview")]
    public async Task A_preview_shows_no_statement_link(string flag)
    {
        var site = PublicSitePages.Build(PublicSitePages.Classic, tweak: w =>
        {
            var footer = WebsiteFooterSettings.FromJson(w.FooterSettingsJson);
            footer.LegalLinks.Clear();
            w.FooterSettingsJson = footer.ToJson();
        });
        var html = await PublicSitePages.RenderAsync(site.Model("home"), new PublicSitePages.RenderOptions { ViewBag = { [flag] = true } });
        Assert.DoesNotContain("href=\"/accessibility\"", html);
        Assert.DoesNotContain("<div class=\"public-footer__legal\">", html);
    }

    [Fact]
    public async Task A_site_with_no_legal_links_still_links_its_statement()
    {
        var site = PublicSitePages.Build(PublicSitePages.Editorial, tweak: w =>
        {
            var footer = WebsiteFooterSettings.FromJson(w.FooterSettingsJson);
            footer.LegalLinks.Clear();
            w.FooterSettingsJson = footer.ToJson();
        });
        var html = await PublicSitePages.RenderAsync(site.Model("home"));
        Assert.Matches("<div class=\"public-footer__legal\">\\s*<a href=\"/accessibility\">Accessibility</a>\\s*</div>", html);
    }

    [Theory]
    [InlineData("Accessibility statement", "/our-accessibility")]
    [InlineData("Accessibilité", "https://example.test/a11y")]
    [InlineData("Access for all", "/accessibility")]
    [InlineData("Access for all", "https://www.riversfinancial.example/Accessibility/")]
    public void An_owners_own_statement_link_is_not_doubled(string label, string url)
    {
        Assert.True(AccessibilityStatement.IsLinked(new[] { new WebsiteFooterLink { Label = label, Url = url } }));
    }

    [Fact]
    public async Task An_owners_own_statement_link_stands_alone_in_the_footer()
    {
        var site = PublicSitePages.Build(PublicSitePages.Modern, tweak: w =>
        {
            var footer = WebsiteFooterSettings.FromJson(w.FooterSettingsJson);
            footer.LegalLinks.Add(new WebsiteFooterLink { Label = "Accessibility statement", Url = "/our-accessibility", SortOrder = 2 });
            w.FooterSettingsJson = footer.ToJson();
        });
        var html = await PublicSitePages.RenderAsync(site.Model("home"));
        Assert.DoesNotContain("href=\"/accessibility\"", html);
        Assert.Contains("<a href=\"/our-accessibility\">Accessibility statement</a>", html);
        Assert.False(AccessibilityStatement.IsLinked(new[] { new WebsiteFooterLink { Label = "Privacy", Url = "/privacy" } }));
    }

    [Theory]
    [InlineData("accessibility", true)]
    [InlineData("Accessibility", true)]
    [InlineData("/accessibility/", true)]
    [InlineData("accessibility/more", false)]
    // Addresses an old site's lookup would read as "accessibility" (a host-like first part, a URL, a
    // second decoding) are not the statement: each is a 404.
    [InlineData("x.y/accessibility", false)]
    [InlineData("https://evil.test/accessibility", false)]
    [InlineData("%61ccessibility", false)]
    [InlineData("accessibility.html", false)]
    [InlineData("accessible", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_one_address_is_the_statement(string? slug, bool statement)
    {
        Assert.Equal(statement, AccessibilityStatement.IsAddress(slug));
    }

    // ------------------------------------------------------------------ served by the public site --

    [Fact]
    public async Task A_site_answers_its_statement_unless_the_owner_has_a_page_there()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (host, websiteId) = await SeedSiteAsync(db);

        var controller = NewController(db, host);
        var view = Assert.IsType<ViewResult>(await controller.Page("accessibility"));
        var model = Assert.IsType<PublicWebsiteViewModel>(view.Model);
        Assert.Equal("Index", view.ViewName);
        Assert.True(model.AccessibilityStatement);
        Assert.False(model.PageNotFound);
        Assert.Null(model.CurrentPage);
        Assert.Equal(StatusCodes.Status200OK, controller.Response.StatusCode);
        // Not a visit to a page of the site: the 404's rule.
        Assert.False(await db.WebsitePageViews.AnyAsync(v => v.AgentWebsiteId == websiteId));

        // A longer address is not the statement.
        var deeper = NewController(db, host);
        var missing = Assert.IsType<PublicWebsiteViewModel>(Assert.IsType<ViewResult>(await deeper.Page("accessibility/more")).Model);
        Assert.True(missing.PageNotFound);
        Assert.False(missing.AccessibilityStatement);
        Assert.Equal(StatusCodes.Status404NotFound, deeper.Response.StatusCode);

        // The owner's own page at the address wins: their words, not the standard ones.
        db.WebsitePages.Add(new WebsitePage
        {
            AgentWebsiteId = websiteId, Slug = "accessibility", Title = "Our accessibility promise", NavigationLabel = "Accessibility",
            IsPublished = true, ShowInNavigation = false, SortOrder = 9
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var own = Assert.IsType<PublicWebsiteViewModel>(Assert.IsType<ViewResult>(await NewController(db, host).Page("accessibility")).Model);
        Assert.False(own.AccessibilityStatement);
        Assert.Equal("Our accessibility promise", own.CurrentPage?.Title);
        // ...and a visit to a real page is counted, where the statement's was not.
        Assert.True(await db.WebsitePageViews.AnyAsync(v => v.AgentWebsiteId == websiteId && v.Path == "/accessibility"));
    }

    [Fact]
    public async Task A_request_sent_from_the_statement_reaches_the_owner_as_a_website_message()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (host, websiteId) = await SeedSiteAsync(db);
        var controller = NewController(db, host);

        var result = await controller.SubmitLead(new WebsiteLeadFormViewModel
        {
            SubmissionType = WebsiteLeadTypes.Contact, ReturnPath = AccessibilityStatement.Path,
            FirstName = "Robin", Email = "robin@example.test", Message = "Could I have your fee guide in large print?", ConsentGiven = true,
            FormStartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30,
            CaptchaToken = ValidCaptcha(controller, 8), CaptchaAnswer = "8"
        });

        Assert.Equal("/accessibility?submitted=contact", Assert.IsType<LocalRedirectResult>(result).Url);
        var lead = await db.WebsiteLeads.SingleAsync(l => l.AgentWebsiteId == websiteId);
        Assert.Equal(WebsiteLeadTypes.Contact, lead.SubmissionType);
        Assert.Equal("/accessibility", lead.SourcePage);
        Assert.Equal("Could I have your fee guide in large print?", lead.Message);
        Assert.True(lead.NotificationSent);
    }

    // ------------------------------------------------------------------ iPro's own statement --

    [Fact]
    public async Task iPros_own_statement_passes_axe_names_its_open_problems_and_claims_nothing_more()
    {
        var html = await PublicSitePages.RenderViewAsync("~/Views/Home/Accessibility.cshtml", "Home", "/accessibility");
        var text = WebUtility.HtmlDecode(html);

        Assert.Contains("<title>Accessibility — IPRO Advisers</title>", text);
        Assert.Contains("<html lang=\"en-CA\">", html);
        Assert.Contains("Web Content Accessibility Guidelines (WCAG) 2.1 at level AA", text);
        Assert.Contains("some orange buttons have white text with too little contrast", text);
        Assert.Contains("the sign-up and sign-in pages do not say which language they are in", text);
        Assert.Contains("many form fields show a label on screen that a screen reader does not announce", text);
        Assert.Contains($"<a href=\"{PlatformContact.SupportPhoneHref}\">{PlatformContact.SupportPhone}</a>", text);
        Assert.Contains("<a href=\"mailto:support@iproadvisers.com?subject=Accessibility\">support@iProAdvisers.com</a>", text);
        Assert.Contains($"Last updated: <time datetime=\"{AccessibilityStatement.PlatformUpdatedOn:yyyy-MM-dd}\">15 October 2026</time>", text);
        Assert.Contains("on any theme colour a customer picks", text);
        Assert.Contains("<a href=\"/accessibility\">Accessibility</a>", html);
        ClaimsNothing(text, "No one has yet checked the designs with a screen reader, so we do not claim that they fully meet WCAG 2.1 AA.");

        var findings = await _edge.AxeAsync(html, "ipro-statement");
        findings.AddRange(await _edge.AxeAsync(html, "ipro-statement-phone", 375));
        Assert.True(findings.Count == 0, $"{findings.Count} findings\n" + string.Join("\n", findings));
    }

    // The legal-review notice and its noindex show while Legal:ReviewComplete is false (it has been true
    // since 2026-08-17, so a render cannot show the difference): only for the legal documents, never on
    // the statement, which no one agrees to.
    [Fact]
    public void The_terms_and_privacy_pages_keep_their_review_notice()
    {
        var layout = File.ReadAllText(Path.Combine(PublicSitePages.RepoRoot(), "src", "IPRO.Web", "Views", "Shared", "_LegalLayout.cshtml"));
        Assert.Contains("var legalDocument = ViewData[\"LegalDocument\"] as bool? ?? true;", layout);
        Assert.Equal(2, Regex.Matches(layout, @"@if \(!reviewComplete && legalDocument\)").Count);
        foreach (var page in new[] { "Privacy", "Terms" })
            Assert.DoesNotContain("LegalDocument", File.ReadAllText(Path.Combine(PublicSitePages.RepoRoot(), "src", "IPRO.Web", "Views", "Home", page + ".cshtml")));
    }

    [Theory]
    [InlineData(@"Views\Home\Index.cshtml")]
    [InlineData(@"Views\Home\_LandingFooter.cshtml")]
    public void Every_platform_footer_links_iPros_statement(string view)
    {
        var source = File.ReadAllText(Path.Combine(PublicSitePages.RepoRoot(), "src", "IPRO.Web", view));
        Assert.Contains("<a href=\"/privacy\">Privacy</a><a href=\"/terms\">Terms</a><a href=\"/accessibility\">Accessibility</a>", source);
    }

    [Fact]
    public void iPros_statement_has_its_short_address_and_answers_HEAD()
    {
        var controller = File.ReadAllText(Path.Combine(PublicSitePages.RepoRoot(), "src", "IPRO.Web", "Controllers", "HomeController.cs"));
        Assert.Contains("[HttpGet(\"accessibility\")]", controller);
        Assert.Contains("public IActionResult Accessibility() => View();", controller);
        Assert.True(HeadRequests.IsAnswered(new PathString("/accessibility")));
    }

    // ------------------------------------------------------------------ helpers --

    private static PublicWebsiteController NewController(IPRODbContext db, string host)
    {
        var protection = DataProtectionProvider.Create($"s579-{Guid.NewGuid():N}");
        var controller = new PublicWebsiteController(
            db, new PackageEntitlementService(new UnitOfWork(db), db), new NullEmail(), NullLogger<PublicWebsiteController>.Instance,
            new ConfigurationBuilder().Build(), protection, new NullBlob(), new MemoryCache(new MemoryCacheOptions()), new NullConsent());
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        // A browser, so a visit WOULD be counted if the controller counted this one.
        context.Request.Headers.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
        context.Items["test-protection"] = protection;
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, new NullTempData());
        return controller;
    }

    private static string ValidCaptcha(PublicWebsiteController controller, int answer) =>
        ((IDataProtectionProvider)controller.HttpContext.Items["test-protection"]!)
            .CreateProtector("IPRO.Web.PublicWebsite.Captcha.v1")
            .Protect($"{answer}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}|{Guid.NewGuid():N}");

    private static async Task<(string Host, int WebsiteId)> SeedSiteAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = $"S-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        var host = $"s579-{Guid.NewGuid():N}"[..14] + ".example.test";
        var agent = new AgentUser
        {
            UserName = $"s579-{Guid.NewGuid():N}"[..20], Email = $"s579-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Dana", LastName = "Rivers", DomainName = $"s579-{Guid.NewGuid():N}"[..24], Country = "Canada", Province = "Ontario"
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        agent.PackageId = rule.Id;
        db.Add(new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 60m, Status = BillingStatus.Active, Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddDays(-40), NextBillingDate = DateTime.UtcNow.AddDays(20)
        });
        var template = new WebsiteTemplate { TemplateKey = $"tk-{Guid.NewGuid():N}"[..16], Name = "Test", BusinessType = "All" };
        db.Add(template);
        await db.SaveChangesAsync();
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, IsPublished = true, CustomDomain = host };
        db.Add(website);
        await db.SaveChangesAsync();
        db.WebsitePages.Add(new WebsitePage { AgentWebsiteId = website.Id, Slug = "home", Title = "Home", IsHomePage = true, IsPublished = true, SortOrder = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (host, website.Id);
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

    private sealed class NullTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
