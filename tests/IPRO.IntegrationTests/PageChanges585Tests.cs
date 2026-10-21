using System;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Admin.Models;
using IPRO.Business.Services;
using IPRO.Business.SiteImport;
using IPRO.Entities;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace IPRO.IntegrationTests;

// 585 (the owner's bench walk-through, 2026-10-19): the review of a return marked all 18 of the owner's
// pages "Replaced", each with the same sentence, though he had changed two. A reviewer's first question
// about a send from the new builder is which pages the customer changed. Each page of a return is now
// compared, section by section, with the site as it is: a page with no difference says "No change" and
// is folded away below; a changed page says what changed, in plain words, and comes first. A page the
// round trip only re-spells (settings written out in full, a contact form's unused button words, line
// ends) reads as unchanged. A site built from scratch keeps the "Replaced" wording.
public class PageChanges585Tests
{
    [Fact]
    public void A_page_the_round_trip_only_re_spells_reads_as_unchanged()
    {
        var before = new[]
        {
            Section(WebsiteBlockTypes.Hero, "Fresh every morning", 0, image: "/media/front.png", settings: "", buttonText: "Call us ", buttonUrl: "Tel:(416) 555-0199"),
            Section(WebsiteBlockTypes.ContactForm, "Say hello", 1, buttonText: "Send an email"),
            Section(WebsiteBlockTypes.ArticleContent, "", 2, settings: "{\"ArticleId\":7}"),
            Section(WebsiteBlockTypes.Text, "About us", 3, body: "Baking since 1998.\r\nStill at it. ", buttonText: "Read more"),
            Section(WebsiteBlockTypes.Blog, "From the oven", 4, settings: "{\"PostCount\":0}")
        };
        var after = new[]
        {
            Section(WebsiteBlockTypes.Hero, "Fresh every morning", 0, image: "/media/front.png", settings: new WebsiteHeroSettings().ToJson(), buttonText: "Call us", buttonUrl: "tel:(416) 555-0199"),
            Section(WebsiteBlockTypes.ContactForm, "Say hello", 1, settings: new WebsiteContactFormSettings().ToJson()),
            Section(WebsiteBlockTypes.ArticleContent, "", 2, settings: "{\"articleId\":7}"),
            Section(WebsiteBlockTypes.Text, "About us", 3, body: "Baking since 1998.\nStill at it."),
            Section(WebsiteBlockTypes.Blog, "From the oven", 4, settings: new WebsiteBlogSettings { PostCount = WebsiteBlogSettings.MinPostCount }.ToJson())
        };
        Assert.Empty(PageChanges.Between(Page(), before, Page(), after));
    }

    [Fact]
    public void What_the_customer_changed_is_said_section_by_section_in_page_order()
    {
        var before = new[]
        {
            Section(WebsiteBlockTypes.Hero, "Build confidence", 0),
            Section(WebsiteBlockTypes.Text, "Our story", 1, body: "Since 1998."),
            Section(WebsiteBlockTypes.Services, "What we do", 2, body: "Tax\nPlanning"),
            Section(WebsiteBlockTypes.Text, "Old news", 3, body: "Gone soon.")
        };
        var after = new[]
        {
            Section(WebsiteBlockTypes.Hero, "Build confidence", 0, image: "/media/canyon.jpg", settings: new WebsiteHeroSettings { Layout = "full-image" }.ToJson()),
            Section(WebsiteBlockTypes.Services, "What we do", 1, body: "Tax\nPlanning"),
            Section(WebsiteBlockTypes.Text, "Our story", 2, body: "Since 1998, in Toronto."),
            Section(WebsiteBlockTypes.CallToAction, "Ready to talk?", 3, buttonText: "Book a call", buttonUrl: "/contact")
        };
        Assert.Equal(new[]
        {
            "Changed: Hero \"Build confidence\": a picture added, and its layout or style.",
            "Changed: Text \"Our story\": its words.",
            "New section: Call To Action \"Ready to talk?\".",
            "Removed: Text \"Old news\"."
        }, PageChanges.Between(Page(), before, Page(), after));
    }

    [Fact]
    public void A_section_moved_or_hidden_is_said_so()
    {
        var before = new[]
        {
            Section(WebsiteBlockTypes.Text, "A", 0), Section(WebsiteBlockTypes.Text, "B", 1), Section(WebsiteBlockTypes.Text, "C", 2),
            Section(WebsiteBlockTypes.Text, "D", 3), Section(WebsiteBlockTypes.Hero, "E", 4)
        };
        var after = new[]
        {
            Section(WebsiteBlockTypes.Text, "B", 0), Section(WebsiteBlockTypes.Text, "C", 1), Section(WebsiteBlockTypes.Text, "D", 2),
            Section(WebsiteBlockTypes.Text, "A", 3), Section(WebsiteBlockTypes.Hero, "E", 4, shown: false)
        };
        Assert.Equal(new[]
        {
            "Moved: Text \"A\" (now section 4 of 5).",
            "Changed: Hero \"E\": now hidden from visitors."
        }, PageChanges.Between(Page(), before, Page(), after));
    }

    [Fact]
    public void A_new_title_or_menu_label_is_said()
    {
        Assert.Equal(new[] { "Its title: \"About\" becomes \"About the bakery\".", "Its menu label: \"About\" becomes \"Our story\"." },
            PageChanges.Between(Page("About", "About"), Array.Empty<WebsiteContentBlock>(), Page("About the bakery", "Our story"), Array.Empty<WebsiteContentBlock>()));
    }

    [Fact]
    public async Task A_return_folds_away_the_pages_the_customer_did_not_change_and_says_what_changed_on_the_others()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SiteHandoverReturn572Tests.SeedFittingSiteAsync(db, blob);
        var handover = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        await SiteHandoverService.RecordAsync(db, seeded.Site, handover, "owner");
        // A call to action added on About; Home and Contact come back as they went.
        var zip = SiteHandoverReturn572Tests.Return(handover.Zip, t =>
        {
            var cta = SiteHandoverReturn572Tests.NewBlock(WebsiteBlockTypes.CallToAction, 1);
            cta["heading"] = "Order a cake";
            SiteHandoverReturn572Tests.Page(t, "about")["blocks"]!.AsArray().Add(cta);
        });
        var audit = new SiteHandoverReturn572Tests.RecordingAudit();
        var uploaded = Assert.IsType<RedirectToActionResult>(await SiteHandoverReturn572Tests.Controller(db, blob, audit)
            .Upload(seeded.Site.AgentUserId, SiteHandoverReturn572Tests.Upload(zip, "return.zip")));
        var importId = (int)uploaded.RouteValues!["id"]!;
        db.ChangeTracker.Clear();

        var review = (BuilderImportReviewViewModel)Assert.IsType<ViewResult>(await SiteHandoverReturn572Tests.Controller(db, blob, audit).Review(importId, null, null)).Model!;
        var pages = review.Plan!.Pages.ToDictionary(p => p.Page.Slug);
        Assert.True(pages["home"].Unchanged, string.Join(" ", pages["home"].Changes ?? Array.Empty<string>()));
        Assert.True(pages["contact"].Unchanged, string.Join(" ", pages["contact"].Changes ?? Array.Empty<string>()));
        Assert.False(pages["about"].Unchanged);
        Assert.Equal(new[] { "New section: Call To Action \"Order a cake\"." }, pages["about"].Changes);

        // SuperAdmin's review: the changed page first, with what changed; the two others folded away below.
        var html = await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Review.cshtml", review);
        Assert.Contains("New section: Call To Action &quot;Order a cake&quot;.", html);
        Assert.Contains("<strong>No change</strong>: 2 pages the customer did not change.", html);
        Assert.DoesNotContain("give way to the package", html);
        Assert.True(html.IndexOf("New section:", StringComparison.Ordinal) < html.IndexOf("<strong>No change</strong>", StringComparison.Ordinal));
        // Finding 8 (the owner's re-check): since a look keeps its links inside it (LookLinks), its menu works.
        Assert.Contains("its menu takes you through the other pages of the look.", html);
        Assert.DoesNotContain("not live", html);

        // The adviser's own review, when they publish themselves, says the same.
        var mine = await BuilderBeta585Tests.RenderPortalAsync("~/Views/WebsiteBuilder/Review.cshtml",
            new WebsiteBuilderReviewViewModel(review.Import, review.Plan, "/look?page=", SitePublished: true));
        Assert.Contains("New section: Call To Action &quot;Order a cake&quot;.", mine);
        Assert.Contains("<strong>No change</strong>: 2 pages you did not change.", mine);
        Assert.Contains("works for an hour; its menu takes you through your other pages.", mine);
        Assert.True(mine.IndexOf("New section:", StringComparison.Ordinal) < mine.IndexOf("<strong>No change</strong>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_site_built_from_scratch_keeps_the_replaced_wording()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await BuilderImport571Tests.SeedSiteAsync(db);
        var blob = new BuilderImport571Tests.MemoryBlob();
        var audit = new SiteHandoverReturn572Tests.RecordingAudit();
        var uploaded = Assert.IsType<RedirectToActionResult>(await SiteHandoverReturn572Tests.Controller(db, blob, audit)
            .Upload(site.AgentUserId, SiteHandoverReturn572Tests.Upload(BuilderImport571Tests.Zip(BuilderImport571Tests.Designer()), "bakery.zip")));
        db.ChangeTracker.Clear();
        var review = (BuilderImportReviewViewModel)Assert.IsType<ViewResult>(await SiteHandoverReturn572Tests.Controller(db, blob, audit).Review((int)uploaded.RouteValues!["id"]!, null, null)).Model!;
        Assert.All(review.Plan!.Pages.Where(p => p.Change == PageChange.Replaced), p => Assert.Null(p.Changes));
        Assert.DoesNotContain(review.Plan.Pages, p => p.Unchanged);
        var html = await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Review.cshtml", review);
        Assert.Contains("give way to the package", html);
        Assert.DoesNotContain("<strong>No change</strong>", html);
    }

    private static WebsitePage Page(string title = "About", string label = "About") => new() { Title = title, NavigationLabel = label, Slug = "about" };

    private static WebsiteContentBlock Section(string type, string heading, int sort, string body = "", string image = "", string settings = "{}",
        string buttonText = "", string buttonUrl = "", bool shown = true) => new()
    {
        BlockType = type, Heading = heading, Body = body, ImageUrl = image, SettingsJson = settings, ButtonText = buttonText, ButtonUrl = buttonUrl,
        SortOrder = sort, IsVisible = shown
    };
}
