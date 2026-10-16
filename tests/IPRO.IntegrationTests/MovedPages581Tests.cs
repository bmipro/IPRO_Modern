using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using IPRO.Admin.Models;
using IPRO.Business.SiteImport;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static IPRO.IntegrationTests.SiteHandoverReturn572Tests;

namespace IPRO.IntegrationTests;

// 581 (2026-10-15): a page the designer gave a new address in the builder came back as a NEW page with
// iPro's defaults -- no search title or description, published, in the menu -- and the page it was went
// as "removed in the builder", taking with it everything the builder never shows: its search title and
// description, whether it is published and in the menu, its language, its old addresses (553), its
// visits; and its address stopped arriving. Nothing on the review said so. (A package carries no page's
// identity, only its address; and the hand-over's fingerprint, 574, leaves those settings out on
// purpose, so the red "changed in iPro" mark could not fire for them either.)
//
// The owner's rule (576, 577): what the designer could not have meant to change is not changed, and the
// review names what is lost before anything is written. Now such a page lands on the row of the page
// it was when the plan can tell which one it is -- by the shortened menu label (577), else by its title,
// else by its menu label, each only when it points one way -- and the operator can say otherwise on the
// review. Its old address joins its old addresses, so links keep arriving. A page that really goes says
// what goes with it.
public class MovedPages581Tests
{
    [Fact]
    public void A_page_at_a_new_address_keeps_what_the_builder_never_showed()
    {
        var created = new DateTime(2024, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        var about = SitePage(2, "about-us", "About us");
        about.MetaTitle = "About Sample Bakery";
        about.MetaDescription = "Baking since 1998.";
        about.OldAddresses = "about.html";
        about.Language = WebsiteLanguages.French;
        about.IsPublished = false;
        about.ShowInNavigation = false;
        about.CreatedAt = created;
        var site = new[] { SitePage(1, "home", "Home", home: true), about };

        var plan = PlanWith(site, new[] { PackagePage("home", "Home", "Home", home: true), PackagePage("about", "About us", "About") });

        Assert.Empty(plan.Problems);
        var moved = Single(plan, "about");
        // Not published on the site, so its old address is kept but sends nobody on until it is; the review
        // says so (the review's fix, pinned in full further down).
        Assert.Equal((PageChange.Replaced, 2, "about-us", MoveMatch.SameTitle, false), (moved.Change, moved.Page.Id, moved.MovedFrom, moved.MovedBy, moved.OldAddressKept));
        Assert.Equal("Links to /about-us will be sent on to /about once the page is published; until then they find nothing, as the page itself does.", moved.OldAddressNote);
        var page = moved.Page;
        Assert.Equal(("About Sample Bakery", "Baking since 1998.", WebsiteLanguages.French, false, false, created),
            (page.MetaTitle, page.MetaDescription, page.Language, page.IsPublished, page.ShowInNavigation, page.CreatedAt));
        // Its address on the site joins the old addresses it answers to, so links to it keep arriving.
        Assert.Equal("about.html\nabout-us", page.OldAddresses);
        // What the package does say is the package's.
        Assert.Equal(("about", "About us", "About"), (page.Slug, page.Title, page.NavigationLabel));
        Assert.DoesNotContain(plan.Pages, p => p.Change is PageChange.Removed or PageChange.Added);
        Assert.Equal(new[] { new MoveCandidate(2, "about-us", "About us") }, plan.MoveCandidates);
        // The home page came back as the home page: no word of a move.
        Assert.Equal((string.Empty, MoveMatch.None), (plan.Pages.Single(p => p.Page.IsHomePage).MovedFrom, plan.Pages.Single(p => p.Page.IsHomePage).MovedBy));
    }

    [Fact]
    public void A_page_is_taken_for_another_only_where_its_words_point_one_way()
    {
        var home = SitePage(1, "home", "Home", home: true);
        var homePage = PackagePage("home", "Home", "Home", home: true);

        // Its menu label, when the titles differ; capitals and spaces do not count.
        var plan = PlanWith(new[] { home, SitePage(2, "team", "Meet the team", label: "Our  Team") }, new[] { homePage, PackagePage("people", "The people", "our team") });
        Assert.Equal((2, "team", MoveMatch.SameMenuLabel), (Single(plan, "people").Page.Id, Single(plan, "people").MovedFrom, Single(plan, "people").MovedBy));

        // Two pages of the site with the title the new page has: nothing tells which, so neither.
        plan = PlanWith(new[] { home, SitePage(2, "services", "Services"), SitePage(3, "services-old", "Services") },
            new[] { homePage, PackagePage("what-we-do", "Services", "What we do") });
        Assert.Equal((PageChange.Added, string.Empty), (Single(plan, "what-we-do").Change, Single(plan, "what-we-do").MovedFrom));
        Assert.Equal(2, plan.Pages.Count(p => p.Change == PageChange.Removed && p.RemovedInBuilder));

        // Two new pages with the title of one page of the site: neither.
        plan = PlanWith(new[] { home, SitePage(2, "team", "Team") }, new[] { homePage, PackagePage("team-a", "Team", "Team A"), PackagePage("team-b", "Team", "Team B") });
        Assert.All(new[] { Single(plan, "team-a"), Single(plan, "team-b") }, p => Assert.Equal((PageChange.Added, string.Empty), (p.Change, p.MovedFrom)));

        // Nothing in common: a new page, and the page of the site was removed in the builder.
        plan = PlanWith(new[] { home, SitePage(2, "about-us", "About us") }, new[] { homePage, PackagePage("careers", "Careers", "Careers") });
        Assert.Equal(PageChange.Added, Single(plan, "careers").Change);
        Assert.Equal((PageChange.Removed, true), (plan.Pages.Single(p => p.Page.Id == 2).Change, plan.Pages.Single(p => p.Page.Id == 2).RemovedInBuilder));

        // A page made in iPro after the hand-over never went to the builder: no page of the package is it.
        plan = PlanWith(new[] { home, SitePage(3, "events", "Events") }, new[] { homePage, PackagePage("our-events", "Events", "Events") }, handedOver: new[] { 1 });
        Assert.Equal(PageChange.Added, Single(plan, "our-events").Change);
        Assert.Equal(PageChange.Kept, plan.Pages.Single(p => p.Page.Id == 3).Change);
        Assert.Empty(plan.MoveCandidates);

        // A site built from scratch in the builder answers no hand-over: none of its pages moved.
        var scratch = SiteImportPlan.Build(
            new BuilderTemplate("sample", "modern", string.Empty, string.Empty, string.Empty, new[] { homePage, PackagePage("about", "About us", "About") }),
            new Dictionary<string, string>(), new AgentWebsite { Id = 9 }, new[] { home, SitePage(2, "about-us", "About us") }, new HashSet<int>());
        Assert.Equal(PageChange.Added, Single(scratch, "about").Change);
        Assert.Equal(PageChange.Kept, scratch.Pages.Single(p => p.Page.Id == 2).Change);
        Assert.Empty(scratch.MoveCandidates);
    }

    [Fact]
    public void The_operators_word_decides_and_agreeing_with_the_plan_changes_nothing()
    {
        var site = new[] { SitePage(1, "home", "Home", home: true), SitePage(2, "about-us", "About us"), SitePage(3, "team", "Team") };
        var pages = new[] { PackagePage("home", "Home", "Home", home: true), PackagePage("about", "About us", "About"), PackagePage("people", "People", "People") };

        var guessed = PlanWith(site, pages);
        Assert.Equal((2, MoveMatch.SameTitle), (Single(guessed, "about").Page.Id, Single(guessed, "about").MovedBy));
        Assert.Equal(PageChange.Added, Single(guessed, "people").Change);
        Assert.Equal(new[] { 2, 3 }, guessed.MoveCandidates.Select(c => c.RowId));

        // What the review posts back when nothing was changed on it: the same plan, to the letter, so
        // "Bring it in" does what the screen showed.
        var asShown = PlanWith(site, pages, moved: new Dictionary<string, int> { ["about"] = 2, ["people"] = 0 });
        Assert.Equal(guessed.Fingerprint, asShown.Fingerprint);
        Assert.Equal(MoveMatch.SameTitle, Single(asShown, "about").MovedBy);

        // "No: a new page" undoes the plan's reading; a page chosen where the words did not tell is that page.
        var chosen = PlanWith(site, pages, moved: new Dictionary<string, int> { ["about"] = 0, ["people"] = 3 });
        Assert.Empty(chosen.Problems);
        Assert.Equal((PageChange.Added, string.Empty), (Single(chosen, "about").Change, Single(chosen, "about").MovedFrom));
        Assert.True(chosen.Pages.Single(p => p.Page.Id == 2).RemovedInBuilder);
        Assert.Equal((3, "team", MoveMatch.Chosen), (Single(chosen, "people").Page.Id, Single(chosen, "people").MovedFrom, Single(chosen, "people").MovedBy));
        Assert.NotEqual(guessed.Fingerprint, chosen.Fingerprint);

        // One page of the site for two of the package: it is not brought in until one is chosen. The
        // choice the operator made (People) comes before the one the untouched form repeats (About).
        var twice = PlanWith(site, pages, moved: new Dictionary<string, int> { ["about"] = 2, ["people"] = 2 });
        Assert.Equal(new[] { "\"People\" and \"About us\" are both said to be the page that was at \"/about-us\". Choose it for one of them only." }, twice.Problems);
        Assert.Equal((2, MoveMatch.Chosen), (Single(twice, "people").Page.Id, Single(twice, "people").MovedBy));
        Assert.Equal(PageChange.Added, Single(twice, "about").Change);

        // A row no page of the package can be (the home page, one not on the site) reads as "a new page".
        foreach (var rowId in new[] { 1, 99 })
        {
            var stale = PlanWith(site, pages, moved: new Dictionary<string, int> { ["people"] = rowId });
            Assert.Equal((PageChange.Added, string.Empty), (Single(stale, "people").Change, Single(stale, "people").MovedFrom));
            Assert.Empty(stale.Problems);
        }
    }

    [Fact]
    public void A_removed_page_says_what_goes_with_it_that_its_sections_do_not_show()
    {
        var team = SitePage(2, "team", "Team");
        team.MetaTitle = "Our team";
        team.MetaDescription = "Who we are.";
        team.Language = WebsiteLanguages.French;
        team.OldAddresses = "/staff.html\nOur-Staff\n";
        team.IsPublished = false;
        team.ShowInNavigation = false;
        var site = new[] { SitePage(1, "home", "Home", home: true), team, SitePage(3, "news", "News") };
        var pages = new[] { PackagePage("home", "Home", "Home", home: true), PackagePage("careers", "Careers", "Careers") };

        var plan = PlanWith(site, pages);
        Assert.Equal(new[]
        {
            "Also goes with it: its search title \"Our team\", its search description, its own language (French) and the old addresses it answers to (/staff.html, /our-staff).",
            "On the site it is neither published nor in the menu; a new page of the package comes back published and in the menu.",
            "Links to /team will find nothing afterwards."
        }, plan.Pages.Single(p => p.Page.Id == 2).SettingsLost);
        Assert.Equal(new[] { "Links to /news will find nothing afterwards." }, plan.Pages.Single(p => p.Page.Id == 3).SettingsLost);

        // What a removed page takes with it is part of what the review promised.
        team.MetaTitle = "The people";
        Assert.NotEqual(plan.Fingerprint, PlanWith(site, pages).Fingerprint);

        // A page the operator removes (here one made in iPro since) is not said to come back published.
        var offers = SitePage(4, "offers", "Offers");
        offers.MetaTitle = "Offers";
        offers.IsPublished = false;
        var byChoice = PlanWith(new[] { SitePage(1, "home", "Home", home: true), offers }, new[] { pages[0] }, handedOver: new[] { 1 }, remove: new[] { 4 });
        var gone = byChoice.Pages.Single(p => p.Page.Id == 4);
        Assert.Equal((PageChange.Removed, false), (gone.Change, gone.RemovedInBuilder));
        Assert.Equal(new[] { "Also goes with it: its search title \"Offers\".", "Links to /offers will find nothing afterwards." }, gone.SettingsLost);
    }

    [Fact]
    public void An_old_address_the_page_has_no_room_for_is_said()
    {
        // The plan's limit is the public site's: it reads no more than this many old addresses.
        Assert.Equal(OldAddresses.MaxEntries, SiteImportPlan.OldAddressLimit);
        var home = SitePage(1, "home", "Home", home: true);
        foreach (var listed in new[] { 11, 12 })
        {
            var about = SitePage(2, "about-us", "About us");
            about.OldAddresses = string.Join("\n", Enumerable.Range(1, listed).Select(i => $"old-{i}"));
            var moved = Single(PlanWith(new[] { home, about }, new[] { PackagePage("home", "Home", "Home", home: true), PackagePage("about", "About us", "About") }), "about");
            Assert.Equal((2, listed < 12), (moved.Page.Id, moved.OldAddressKept));
            Assert.Equal(listed < 12 ? string.Empty
                : "Links to /about-us will find nothing: the page already lists 12 old addresses, the most a page answers to. " +
                  "Afterwards, remove one in the page editor (Page Settings, Old addresses) and add /about-us there.", moved.OldAddressNote);
            // No line is written that the public site would never read.
            Assert.Equal(Math.Min(listed + 1, 12), moved.Page.OldAddresses.Split('\n').Length);
            // What the public site does with the page as written: the twelfth arrives; there is no thirteenth.
            Assert.Equal(listed < 12 ? 2 : (int?)null, OldAddresses.Find(new[] { moved.Page }, "about-us")?.Id);
        }
    }

    [Fact]
    public void A_moved_page_says_what_it_keeps_that_visitors_notice_and_where_its_old_address_goes()
    {
        // The function review's case: a hidden page in French, moved. It stays hidden and in French, and
        // links to its old address cannot arrive while it is not published (the public site answers an
        // old address only for a published page).
        var home = SitePage(1, "home", "Home", home: true);
        var homePage = PackagePage("home", "Home", "Home", home: true);
        var blog = SitePage(2, "blog", "Blog");
        blog.IsPublished = false;
        blog.ShowInNavigation = false;
        blog.Language = WebsiteLanguages.French;
        var moved = Single(PlanWith(new[] { home, blog }, new[] { homePage, PackagePage("news", "Blog", "News") }), "news");
        Assert.Equal((2, false, "blog"), (moved.Page.Id, moved.Page.IsPublished, moved.Page.OldAddresses));
        Assert.Equal(new[]
        {
            "On the site it is not published, and it stays so: visitors see it only once the customer publishes it.",
            "It is not in the menu on the site, and it stays out of it.",
            "It keeps its own language (French)."
        }, moved.MoveNotes);
        Assert.Equal((false, "Links to /blog will be sent on to /news once the page is published; until then they find nothing, as the page itself does."),
            (moved.OldAddressKept, moved.OldAddressNote));

        // The designer made another page the home page at the moved page's old address: a page that lives
        // at an address wins over an old address, so links reach the home page.
        var about = SitePage(2, "about", "About us");
        var plan = PlanWith(new[] { home, about }, new[] { PackagePage("about", "Welcome", "Welcome", home: true), PackagePage("about-us", "About us", "About us") });
        Assert.Equal((2, false, "Links to /about reach \"Welcome\", the page at that address afterwards, not this one."),
            (Single(plan, "about-us").Page.Id, Single(plan, "about-us").OldAddressKept, Single(plan, "about-us").OldAddressNote));
        // And a page that is removed there says so, instead of "will find nothing".
        plan = PlanWith(new[] { home, about }, new[] { PackagePage("about", "Welcome", "Welcome", home: true) });
        Assert.Equal("Links to /about reach \"Welcome\", the page at that address afterwards.", plan.Pages.Single(p => p.Page.Id == 2).SettingsLost.Last());

        // Another page that stays already lists the old address among its own (553): which of the two a
        // link reaches is not this page's to promise.
        var contact = SitePage(3, "contact", "Contact");
        contact.OldAddresses = "about";
        plan = PlanWith(new[] { home, about, contact }, new[] { homePage, PackagePage("about-us", "About us", "About us"), PackagePage("contact", "Contact", "Contact") });
        Assert.Equal((false, "Links to /about may go to \"Contact\" instead: it already lists that address among its old addresses. " +
                "Remove it there in the page editor (Page Settings, Old addresses) if they should arrive here."),
            (Single(plan, "about-us").OldAddressKept, Single(plan, "about-us").OldAddressNote));

        // A published page in the menu says nothing more than that its old address arrives.
        plan = PlanWith(new[] { home, about }, new[] { homePage, PackagePage("about-us", "About us", "About us") });
        Assert.Equal((true, string.Empty), (Single(plan, "about-us").OldAddressKept, Single(plan, "about-us").OldAddressNote));
        Assert.Empty(Single(plan, "about-us").MoveNotes);
    }

    [Fact]
    public void A_page_said_to_be_another_than_its_shortened_label_names_keeps_that_label_whole()
    {
        // The function review's case: the page carries the menu label the builder was given shortened for
        // Pensions, and the operator says it is Team. Before, the shortened label was written as it came,
        // cut and ending in an ellipsis, and the review said nothing of it.
        const string Pensions = "What Happens to Your Pension When You Change Jobs?";
        var shortPensions = SiteHandoverBuilder.BuilderLabel(Pensions);
        var site = new[] { SitePage(1, "home", "Home", home: true), SitePage(2, "pensions", Pensions), SitePage(3, "team", "Team") };
        var pages = new[] { PackagePage("home", "Home", "Home", home: true), PackagePage("staff", "Staff", shortPensions) };
        var labels = new[] { new ShortenedLabel("pensions", 2, Pensions, shortPensions) };

        var guessed = Single(PlanWith(site, pages, labels: labels), "staff");
        Assert.Equal((2, MoveMatch.ShortenedLabel, Pensions, true), (guessed.Page.Id, guessed.MovedBy, guessed.Page.NavigationLabel, guessed.LabelKept));

        var chosen = Single(PlanWith(site, pages, moved: new Dictionary<string, int> { ["staff"] = 3 }, labels: labels), "staff");
        Assert.Equal((3, MoveMatch.Chosen), (chosen.Page.Id, chosen.MovedBy));
        Assert.Equal((Pensions, shortPensions, true), (chosen.Page.NavigationLabel, chosen.LabelSent, chosen.LabelKept));
    }

    [Fact]
    public void A_moved_pages_locked_boxes_are_its_own_sections()
    {
        // 576: a section the builder cannot carry travels as a locked box. On a page moved in the builder
        // it is now that page's own section again, with no word of "another page"; one whose box the
        // designer removed can be kept on the page, where before it went with the old page.
        var gallery = Section(20, WebsiteBlockTypes.Gallery, "Our shop", 0);
        var map = Section(21, WebsiteBlockTypes.Maps, "Find us", 1);
        var hosts = new[] { HandoverHostSection.From(gallery, 1, "about-us"), HandoverHostSection.From(map, 2, "about-us") };
        var about = SitePage(2, "about-us", "About us");
        about.Blocks = new List<WebsiteContentBlock> { Copied(gallery, 2), Copied(map, 2) };
        var site = new[] { SitePage(1, "home", "Home", home: true), about };
        var pages = new[]
        {
            PackagePage("home", "Home", "Home", home: true),
            new BuilderPage("about", null, "About us", "About", false, 1, new[] { Box(1, 0) })   // the map's box was removed
        };

        var plan = PlanWith(site, pages, hosts: hosts);
        var moved = Single(plan, "about");
        Assert.Equal((2, MoveMatch.SameTitle), (moved.Page.Id, moved.MovedBy));
        Assert.Equal(new[] { WebsiteBlockTypes.Gallery }, moved.Page.Blocks.Select(b => b.BlockType));
        Assert.Empty(moved.BoxNotes);
        Assert.Equal(new[] { "21|True|False" }, moved.SectionsGoing.Select(g => $"{g.BlockId}|{g.RemovedInBuilder}|{g.Kept}"));
        var kept = Single(PlanWith(site, pages, hosts: hosts, keepSections: new[] { 21 }), "about");
        Assert.Equal(new[] { WebsiteBlockTypes.Gallery, WebsiteBlockTypes.Maps }, kept.Page.Blocks.Select(b => b.BlockType));
    }

    [Fact]
    public void The_choices_travel_as_one_value_and_nothing_else_is_read()
    {
        var moves = BuilderImportService.ParseMoves("team:0, about-us:12,About:3,x-:4,:5,a:b,a:-1,team:7");
        Assert.Equal("about-us:12,team:0", BuilderImportService.JoinMoves(moves));
        // As the review's form posts them: one per page.
        Assert.Equal("about-us:12,team:0", BuilderImportService.JoinMoves(BuilderImportService.ParseMoves(new[] { "team:0", null, "about-us:12" })));
        Assert.Empty(BuilderImportService.ParseMoves((string?)null));
        Assert.Null(BuilderImportService.JoinMoves(new Dictionary<string, int>()));
    }

    [Fact]
    public async Task The_review_names_a_move_and_lets_it_be_undone_or_chosen()
    {
        var team = SitePage(3, "team", "Team");
        team.MetaTitle = "Our team";
        var site = new[] { SitePage(1, "home", "Home", home: true), SitePage(2, "about-us", "About us"), team };
        var plan = PlanWith(site, new[] { PackagePage("home", "Home", "Home", home: true), PackagePage("about", "About us", "About"), PackagePage("people", "People", "People") });
        var review = new BuilderImportReviewViewModel
        {
            Agent = new AgentUser { Id = 4, UserName = "sample", CompanyName = "Sample Planning" },
            Website = new AgentWebsite { Id = 9 },
            Import = new WebsiteBuilderImport { Id = 1, FileName = "return.zip", HandoverId = "h_0123456789abcdef" },
            Package = new BuilderPackage(),
            Plan = plan
        };

        var markup = await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Review.cshtml", review);
        var html = WebUtility.HtmlDecode(markup);
        // To look at it: set BUILDER_SCREENS_OUT to a folder, as for 571's screens.
        var folder = Environment.GetEnvironmentVariable("BUILDER_SCREENS_OUT");
        if (!string.IsNullOrEmpty(folder)) File.WriteAllText(Path.Combine(folder, "review-moved.html"), BuilderImportScreens571Tests.Shell(markup));

        Assert.Contains(">Moved</span>", html);
        Assert.Contains("This is the page that was at /about-us, at a new address. iPro takes it for that page because it has that page's title, and no other page does. " +
            "If that is wrong, choose \"No: a new page\" below.", html);
        Assert.Contains("It keeps that page's place: its search title and description, whether it is published and in the menu, its language, its old addresses and its visits.", html);
        Assert.Contains("Links to /about-us keep arriving: they are sent on to /about.", html);
        // Each page of the package at a new address can be said to be either page that did not come back, or none.
        Assert.Contains("value=\"about:2\" selected=\"selected\">Yes: \"About us\", which was at /about-us</option>", html);
        Assert.Contains("value=\"about:0\">No: a new page</option>", html);
        Assert.Contains("value=\"people:3\">Yes: \"Team\", which was at /team</option>", html);
        Assert.Contains("value=\"people:0\">No: a new page</option>", html);
        Assert.DoesNotContain("value=\"home:", html);
        // The page that did not come back says what goes with it, and how to keep it.
        Assert.Contains("Also goes with it: its search title \"Our team\".", html);
        Assert.Contains("Links to /team will find nothing afterwards.", html);
        Assert.Contains("If one of the package's new pages is this page at a new address, choose it on that page's line: it then keeps all of this, and links to /team keep arriving.", html);

        // A page that cannot keep its old address says so, in red, with what to do.
        var full = SitePage(2, "about-us", "About us");
        full.OldAddresses = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"old-{i}"));
        var fullPlan = PlanWith(new[] { SitePage(1, "home", "Home", home: true), full }, new[] { PackagePage("home", "Home", "Home", home: true), PackagePage("about", "About us", "About") });
        var fullHtml = WebUtility.HtmlDecode(await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Review.cshtml", new BuilderImportReviewViewModel
        {
            Agent = review.Agent, Website = review.Website, Import = review.Import, Package = review.Package, Plan = fullPlan
        }));
        Assert.Contains("Links to /about-us will find nothing: the page already lists 12 old addresses, the most a page answers to. " +
            "Afterwards, remove one in the page editor (Page Settings, Old addresses) and add /about-us there.", fullHtml);
    }

    [Fact]
    public async Task The_review_shows_the_choices_as_they_were_made()
    {
        var site = new[] { SitePage(1, "home", "Home", home: true), SitePage(2, "about-us", "About us"), SitePage(3, "team", "Team") };
        var pages = new[] { PackagePage("home", "Home", "Home", home: true), PackagePage("about", "About us", "About"), PackagePage("people", "People", "People") };
        BuilderImportReviewViewModel Review(SiteImportPlan plan, Dictionary<string, int>? moved = null, int[]? keep = null) => new()
        {
            Agent = new AgentUser { Id = 4, UserName = "sample", CompanyName = "Sample Planning" },
            Website = new AgentWebsite { Id = 9 },
            Import = new WebsiteBuilderImport { Id = 1, FileName = "return.zip", HandoverId = "h_0123456789abcdef" },
            Package = new BuilderPackage(),
            Plan = plan,
            Moved = moved ?? new Dictionary<string, int>(),
            Keep = (keep ?? Array.Empty<int>()).ToHashSet()
        };

        // Two pages said to be one: both choices stay shown as made, beside the problem, so the operator
        // sees which two to settle (the function review: the page that lost showed "No: a new page").
        var twice = new Dictionary<string, int> { ["about"] = 2, ["people"] = 2 };
        var markup = await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Review.cshtml", Review(PlanWith(site, pages, moved: twice), twice));
        Assert.Equal(new[] { "about:2", "people:2" }, PostedChoices(markup));
        Assert.Contains("are both said to be the page that was at", WebUtility.HtmlDecode(markup));

        // A page kept after all, then chosen for a page at a new address, has no tick box while it is that
        // page: its tick travels on, so that undoing the choice keeps it again.
        var keptThenMoved = await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Review.cshtml",
            Review(PlanWith(site, pages, keep: new[] { 2 }), keep: new[] { 2 }));
        Assert.Contains("<input type=\"hidden\" name=\"keep\" value=\"2\" />", keptThenMoved);
        var undone = new Dictionary<string, int> { ["about"] = 0, ["people"] = 0 };
        var keptAgain = PlanWith(site, pages, moved: undone, keep: new[] { 2 });
        Assert.Equal(PageChange.Kept, keptAgain.Pages.Single(p => p.Page.Id == 2).Change);
        var keptMarkup = await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Review.cshtml", Review(keptAgain, undone, new[] { 2 }));
        Assert.DoesNotContain("type=\"hidden\" name=\"keep\"", keptMarkup);
        Assert.Contains("name=\"keep\" value=\"2\" id=\"keep-2\" checked", keptMarkup);

        // A site with no home page: the package's home page is new, and is never offered as a moved page
        // (the home page is the home page; the plan would not read the choice).
        var noHome = PlanWith(new[] { SitePage(2, "about-us", "About us") }, new[] { PackagePage("home", "Home", "Home", home: true), PackagePage("about", "About us", "About") });
        Assert.Equal(PageChange.Added, noHome.Pages.Single(p => p.Page.IsHomePage).Change);
        var noHomeMarkup = await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Review.cshtml", Review(noHome));
        Assert.DoesNotContain("id=\"moved-home\"", noHomeMarkup);
        Assert.Contains("id=\"moved-about\"", noHomeMarkup);
    }

    [Fact]
    public async Task A_page_moved_in_the_builder_goes_in_on_its_own_row_and_its_old_address_arrives()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var audit = new RecordingAudit();
        var seeded = await SeedFittingSiteAsync(db, blob);
        var site = seeded.Site;

        // What the customer set on About that the builder never shows, and a visit to it.
        var about = await db.WebsitePages.SingleAsync(p => p.AgentWebsiteId == site.Id && p.Slug == "about");
        var aboutId = about.Id;
        var created = about.CreatedAt;
        about.MetaTitle = "About Sample Bakery";
        about.MetaDescription = "Baking since 1998.";
        about.OldAddresses = "about.html";
        about.ShowInNavigation = false;
        db.Add(new WebsitePageView { AgentWebsiteId = site.Id, WebsitePageId = aboutId, Path = "/about", VisitorHash = "v1" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var handover = await SiteHandoverService.BuildAsync(db, blob, site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        Assert.True(handover.CanHandOver, string.Join("\n", handover.Blockers));
        await SiteHandoverService.RecordAsync(db, site, handover, "owner");
        db.ChangeTracker.Clear();

        // Meanwhile the customer adds a page under About in iPro.
        db.Add(new WebsitePage
        {
            AgentWebsiteId = site.Id, ParentPageId = aboutId, Title = "Our ovens", Slug = "ovens", NavigationLabel = "Ovens", SortOrder = 5,
            Blocks = { new WebsiteContentBlock { BlockType = WebsiteBlockTypes.Text, Heading = "Ovens", Body = "Wood-fired.", SortOrder = 0 } }
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // The designer gives About a new address, and its heading new words.
        var returned = Return(handover.Zip, template =>
        {
            Page(template, "about")["slug"] = "about-us";
            Block(template, "about-us", 0)["heading"] = "Our story";
        });
        var uploaded = Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Upload(site.AgentUserId, Upload(returned, "return.zip")));
        db.ChangeTracker.Clear();
        var importId = (int)uploaded.RouteValues!["id"]!;

        var review = (BuilderImportReviewViewModel)((ViewResult)await Controller(db, blob, audit).Review(importId, null, null, null, null)).Model!;
        db.ChangeTracker.Clear();
        Assert.Empty(review.Plan!.Problems);
        var moved = review.Plan.Pages.Single(p => p.Page.Slug == "about-us");
        Assert.Equal((PageChange.Replaced, aboutId, "about", MoveMatch.SameTitle, true, false), (moved.Change, moved.Page.Id, moved.MovedFrom, moved.MovedBy, moved.OldAddressKept, moved.ChangedSinceHandover));
        Assert.DoesNotContain(review.Plan.Pages, p => p.Change == PageChange.Removed);
        Assert.Equal(PageChange.Kept, review.Plan.Pages.Single(p => p.Page.Slug == "ovens").Change);
        var markup = await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Review.cshtml", review);
        Assert.Contains("This is the page that was at /about, at a new address.", WebUtility.HtmlDecode(markup));
        Assert.Contains("It is not in the menu on the site, and it stays out of it.", WebUtility.HtmlDecode(markup));
        // What the form posts when nothing on it was changed: the choice the screen shows.
        var posted = PostedChoices(markup);
        Assert.Equal(new[] { $"about-us:{aboutId}" }, posted);

        // The look says it too.
        var import = await db.WebsiteBuilderImports.AsNoTracking().SingleAsync(i => i.Id == importId);
        var look = await BuilderImportPreviewBuilder.BuildAsync(db, import, "about-us", null, null);
        Assert.StartsWith("THIS PAGE COMES FROM THE PACKAGE, at a new address: it would replace the page at /about and keep that page's search settings.", look!.PreviewNote);

        // "Bring it in", with what the form posts: the choice the screen showed.
        var applied = Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Apply(importId, null, null, plan: review.Plan.Fingerprint, moved: posted));
        Assert.Equal("Index", applied.ActionName);
        db.ChangeTracker.Clear();
        Assert.Contains(audit.Entries, e => e.Action == "BuilderImportApplied" && e.Details.Contains($", pages at a new address kept as the page they were: ids {aboutId}. "));

        var rows = await db.WebsitePages.AsNoTracking().Where(p => p.AgentWebsiteId == site.Id).Include(p => p.Blocks).ToListAsync();
        var row = rows.Single(p => p.Id == aboutId);
        Assert.Equal(("about-us", "About Sample Bakery", "Baking since 1998.", "about.html\nabout", true, false, created),
            (row.Slug, row.MetaTitle, row.MetaDescription, row.OldAddresses, row.IsPublished, row.ShowInNavigation, row.CreatedAt));
        Assert.Equal("Our story", row.Blocks.Single().Heading);
        Assert.DoesNotContain(rows, p => p.Slug == "about");
        // The page made under it in iPro is still under it, and its visit is still its own.
        Assert.Equal(aboutId, rows.Single(p => p.Slug == "ovens").ParentPageId);
        Assert.Equal(aboutId, (await db.WebsitePageViews.AsNoTracking().SingleAsync(v => v.AgentWebsiteId == site.Id)).WebsitePageId);
        // A link to the old address finds the page (the public site's own reading of old addresses, 553).
        Assert.Equal(aboutId, OldAddresses.Find(rows.Where(p => p.IsPublished).ToList(), "about")?.Id);

        // Put back, it is the page at its old address again.
        Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Restore(importId));
        db.ChangeTracker.Clear();
        var back = await db.WebsitePages.AsNoTracking().SingleAsync(p => p.Id == aboutId);
        Assert.Equal(("about", "about.html", "About Sample Bakery"), (back.Slug, back.OldAddresses, back.MetaTitle));
    }

    [Fact]
    public async Task Choosing_on_the_review_travels_to_the_review_the_look_and_the_write()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var audit = new RecordingAudit();
        var seeded = await SeedFittingSiteAsync(db, blob);
        var site = seeded.Site;
        var aboutId = await db.WebsitePages.Where(p => p.AgentWebsiteId == site.Id && p.Slug == "about").Select(p => p.Id).SingleAsync();
        var handover = await SiteHandoverService.BuildAsync(db, blob, site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        await SiteHandoverService.RecordAsync(db, site, handover, "owner");
        db.ChangeTracker.Clear();

        // A new address AND a new title: nothing tells the plan which page it is.
        var returned = Return(handover.Zip, template =>
        {
            Page(template, "about")["slug"] = "our-story";
            Page(template, "our-story")["title"] = "Our story";
            Page(template, "our-story")["navigationLabel"] = "Story";
        });
        var uploaded = Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Upload(site.AgentUserId, Upload(returned, "return.zip")));
        db.ChangeTracker.Clear();
        var importId = (int)uploaded.RouteValues!["id"]!;
        var first = (BuilderImportReviewViewModel)((ViewResult)await Controller(db, blob, audit).Review(importId, null, null, null, null)).Model!;
        db.ChangeTracker.Clear();
        Assert.Equal(PageChange.Added, first.Plan!.Pages.Single(p => p.Page.Slug == "our-story").Change);
        Assert.Contains("Links to /about will find nothing afterwards.", first.Plan.Pages.Single(p => p.Page.Id == aboutId).SettingsLost);

        // The operator says which page it is: the review's address carries it, and the look's too.
        var chosen = Assert.IsType<RedirectToActionResult>(Controller(db, blob, audit).Choose(importId, null, null, null, null, new[] { $"our-story:{aboutId}", "not a choice" }));
        Assert.Equal($"our-story:{aboutId}", chosen.RouteValues!["moved"]);
        var second = (BuilderImportReviewViewModel)((ViewResult)await Controller(db, blob, audit).Review(importId, null, null, null, null, (string)chosen.RouteValues["moved"]!)).Model!;
        db.ChangeTracker.Clear();
        Assert.Equal(aboutId, second.Moved["our-story"]);
        var story = second.Plan!.Pages.Single(p => p.Page.Slug == "our-story");
        Assert.Equal((PageChange.Replaced, aboutId, "about", MoveMatch.Chosen), (story.Change, story.Page.Id, story.MovedFrom, story.MovedBy));
        Assert.EndsWith($"&moved={Uri.EscapeDataString($"our-story:{aboutId}")}&page=", second.LookAddress);
        var import = await db.WebsiteBuilderImports.AsNoTracking().SingleAsync(i => i.Id == importId);
        var look = await BuilderImportPreviewBuilder.BuildAsync(db, import, "our-story", null, null, moved: $"our-story:{aboutId}");
        Assert.Contains("it would replace the page at /about", look!.PreviewNote);

        // "Bring it in" with the review read before the choice is refused: it is not what the screen shows now.
        var early = Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Apply(importId, null, null, plan: first.Plan.Fingerprint, moved: new[] { $"our-story:{aboutId}" }));
        Assert.Equal("Review", early.ActionName);
        Assert.Equal($"our-story:{aboutId}", early.RouteValues!["moved"]);
        db.ChangeTracker.Clear();
        Assert.True(await db.WebsitePages.AnyAsync(p => p.Id == aboutId && p.Slug == "about"));

        var applied = Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Apply(importId, null, null, plan: second.Plan.Fingerprint, moved: new[] { $"our-story:{aboutId}" }));
        Assert.Equal("Index", applied.ActionName);
        db.ChangeTracker.Clear();
        var row = await db.WebsitePages.AsNoTracking().SingleAsync(p => p.Id == aboutId);
        Assert.Equal(("our-story", "Our story", "Story", "about"), (row.Slug, row.Title, row.NavigationLabel, row.OldAddresses));
    }

    [Fact]
    public async Task Two_pages_that_trade_addresses_go_in()
    {
        // The function review's case: the site's home page lives at /welcome; the designer made About the
        // home page (at /about), and the operator says the page at /welcome is About. The home row goes
        // welcome -> about while About's row goes about -> welcome: one save of both met the site's own
        // "one page per address", and "Bring it in" failed (before 581, About's row was removed first).
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var audit = new RecordingAudit();
        var seeded = await SeedFittingSiteAsync(db, blob);
        var site = seeded.Site;
        var home = await db.WebsitePages.SingleAsync(p => p.Id == seeded.HomeId);
        home.Slug = "welcome";
        await db.SaveChangesAsync();
        var aboutId = await db.WebsitePages.Where(p => p.AgentWebsiteId == site.Id && p.Slug == "about").Select(p => p.Id).SingleAsync();
        db.ChangeTracker.Clear();
        var handover = await SiteHandoverService.BuildAsync(db, blob, site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        Assert.True(handover.CanHandOver, string.Join("\n", handover.Blockers));
        await SiteHandoverService.RecordAsync(db, site, handover, "owner");
        db.ChangeTracker.Clear();

        var returned = Return(handover.Zip, template =>
        {
            Page(template, "about")["isHomePage"] = true;
            Page(template, "welcome")["isHomePage"] = false;
        });
        var uploaded = Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Upload(site.AgentUserId, Upload(returned, "return.zip")));
        db.ChangeTracker.Clear();
        var importId = (int)uploaded.RouteValues!["id"]!;
        var choice = $"welcome:{aboutId}";
        var review = (BuilderImportReviewViewModel)((ViewResult)await Controller(db, blob, audit).Review(importId, null, null, null, null, choice)).Model!;
        db.ChangeTracker.Clear();
        Assert.Empty(review.Plan!.Problems);
        var moved = review.Plan.Pages.Single(p => p.Page.Slug == "welcome");
        Assert.Equal((aboutId, MoveMatch.Chosen, false), (moved.Page.Id, moved.MovedBy, moved.OldAddressKept));
        Assert.Equal("Links to /about reach \"About\", the page at that address afterwards, not this one.", moved.OldAddressNote);

        var applied = Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Apply(importId, null, null, plan: review.Plan.Fingerprint, moved: new[] { choice }));
        Assert.Equal("Index", applied.ActionName);
        db.ChangeTracker.Clear();
        var rows = await db.WebsitePages.AsNoTracking().Where(p => p.AgentWebsiteId == site.Id).ToDictionaryAsync(p => p.Id);
        Assert.Equal(("about", true), (rows[seeded.HomeId].Slug, rows[seeded.HomeId].IsHomePage));
        Assert.Equal(("welcome", false), (rows[aboutId].Slug, rows[aboutId].IsHomePage));
        Assert.DoesNotContain(rows.Values, p => p.Slug.StartsWith("_importing_", StringComparison.Ordinal));
        // Neither lists the other's live address among its old addresses (the re-check): the editor would
        // never store that, and the next save would warn about it.
        Assert.DoesNotContain("welcome", (rows[seeded.HomeId].OldAddresses ?? "").Split((char)10));
        Assert.DoesNotContain("about", (rows[aboutId].OldAddresses ?? "").Split((char)10));

        // And put back, each at its own address again.
        Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Restore(importId));
        db.ChangeTracker.Clear();
        var back = await db.WebsitePages.AsNoTracking().Where(p => p.AgentWebsiteId == site.Id).ToDictionaryAsync(p => p.Id);
        Assert.Equal(("welcome", "about"), (back[seeded.HomeId].Slug, back[aboutId].Slug));
    }

    // ---------------- helpers ----------------

    private static PlannedPage Single(SiteImportPlan plan, string slug) => plan.Pages.Single(p => p.Page.Slug == slug && p.Change != PageChange.Removed);

    private static WebsitePage SitePage(int id, string slug, string title, bool home = false, string? label = null) => new()
    {
        Id = id, AgentWebsiteId = 9, Title = title, Slug = slug, NavigationLabel = label ?? title, IsHomePage = home, SortOrder = id,
        Blocks = { new WebsiteContentBlock { Id = id * 10, WebsitePageId = id, BlockType = WebsiteBlockTypes.Text, Heading = title, SortOrder = 0 } }
    };

    private static BuilderPage PackagePage(string slug, string title, string label, bool home = false) =>
        new(slug, null, title, label, home, 0, new[] { new BuilderBlock(WebsiteBlockTypes.Text, "Words", "", "", "", "", "", "", 0, true, "{}", 0, 0) });

    private static WebsiteContentBlock Section(int id, string type, string heading, int sort) =>
        new() { Id = id, BlockType = type, Heading = heading, SortOrder = sort, SettingsJson = "{}", IsVisible = true };

    // A section as a page of the site has it (a fresh copy, so that one section can stand on two sites).
    private static WebsiteContentBlock Copied(WebsiteContentBlock section, int pageId)
    {
        var copy = HandoverHostSection.Copy(section, section.SortOrder);
        copy.Id = section.Id;
        copy.WebsitePageId = pageId;
        return copy;
    }

    private static BuilderBlock Box(int number, int sort) =>
        new(BuilderContract.HostSection, "", "", "", "", "", "", "", sort, true, "{}", 0, 0, number);

    // The plan for a return of a hand-over that carried the pages handedOver (all of the site's when
    // null). moved: the operator's word on the review; remove: the pages the operator removes; labels
    // (577), hosts and keepSections (576) as the hand-over recorded them and the operator chose.
    private static SiteImportPlan PlanWith(IReadOnlyList<WebsitePage> site, BuilderPage[] pages, int[]? handedOver = null,
        Dictionary<string, int>? moved = null, int[]? remove = null, ShortenedLabel[]? labels = null,
        HandoverHostSection[]? hosts = null, int[]? keepSections = null, int[]? keep = null)
    {
        var went = (handedOver ?? site.Select(p => p.Id).ToArray()).ToHashSet();
        return SiteImportPlan.Build(
            new BuilderTemplate("sample", "modern", "h_0123456789abcdef", string.Empty, string.Empty, pages) { ContractVersion = BuilderContract.VersionWithHostSections },
            new Dictionary<string, string>(), new AgentWebsite { Id = 9 }, site, (remove ?? Array.Empty<int>()).ToHashSet(),
            handedOverPageIds: went,
            keepPageIds: keep?.ToHashSet(),
            handedOverRowBySlug: site.Where(p => went.Contains(p.Id)).ToDictionary(p => p.Slug, p => p.Id, StringComparer.OrdinalIgnoreCase),
            carriedTypes: BuilderContract.BlockTypes.ToHashSet(StringComparer.Ordinal),
            hostSections: hosts,
            keepSectionIds: keepSections?.ToHashSet(),
            shortenedLabels: labels,
            movedFrom: moved);
    }

    // What the review's form posts for its choices of pages at a new address: each select's chosen
    // option, or its first when none is marked chosen (as a browser does).
    private static string[] PostedChoices(string markup) =>
        System.Text.RegularExpressions.Regex.Matches(markup, "<select[^>]*name=\"moved\"[^>]*>(.*?)</select>", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(select =>
            {
                var options = System.Text.RegularExpressions.Regex.Matches(select.Groups[1].Value, "<option value=\"([^\"]*)\"( selected=\"selected\")?");
                return WebUtility.HtmlDecode((options.FirstOrDefault(o => o.Groups[2].Success) ?? options.First()).Groups[1].Value);
            })
            .ToArray();
}
