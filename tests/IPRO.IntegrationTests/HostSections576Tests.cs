using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using IPRO.Admin.Models;
using IPRO.Business.Services;
using IPRO.Business.SiteImport;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static IPRO.IntegrationTests.SiteHandoverReturn572Tests;

namespace IPRO.IntegrationTests;

// 576: the first real hand-over was refused. The owner's own site has fourteen pages with sections
// the builder does not carry (a calculator, a gallery, a poll), and so will most customers'. From
// contract v0.5 such a section travels as a LOCKED BOX: the package says where it stands and which
// one it is (its kind and its heading), never what is in it; the designer can move, hide or remove
// the box; and when it comes back iPro writes the section exactly as it was. A section the package
// does not bring back is named one by one on the review, each with a box to keep it.
//
// Three independent reviews (security, function, code) read the first version the same day. What
// they found is tested here too: a section counts as handed over by what it SAYS, not by its kind
// and place; a kept section is the one the site has now; a box that comes back shown, moved or twice
// is said; a picture with an accent in its name is still protected; erasure takes the pictures only
// a builder record names.
public class HostSections576Tests
{
    [Fact]
    public async Task A_site_with_sections_the_builder_does_not_carry_goes_out_with_locked_boxes()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedWithHostSectionsAsync(db, blob);

        var handover = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        Assert.True(handover.CanHandOver, string.Join("\n", handover.Blockers));
        Assert.Contains(handover.Notes, n => n.StartsWith("3 sections travel as locked boxes (Calculator, Photo Gallery, Map)"));

        // For the real trip through the builder's own code, when a folder is named (never set in the gate).
        var folder = Environment.GetEnvironmentVariable("HOST_SECTIONS_OUT");
        if (!string.IsNullOrEmpty(folder)) File.WriteAllBytes(Path.Combine(folder, "roundtrip-v05-ipro-handover.zip"), handover.Zip);

        var files = Entries(handover.Zip);
        var template = JsonNode.Parse(Encoding.UTF8.GetString(files["template.json"]))!.AsObject();
        Assert.Equal("0.5", template["contractVersion"]!.GetValue<string>());
        Assert.Equal(new[] { "Hero", "HostSection", "Blog" }, Types(template, "home"));
        Assert.Equal(new[] { "Text", "HostSection", "HostSection" }, Types(template, "about"));

        // A box says which section it is and nothing else (contract section 4.10).
        var box = Block(template, "home", 1);
        Assert.Equal("{\"hostSectionId\":1}", box["settings"]!.ToJsonString());
        foreach (var field in new[] { "heading", "subheading", "body", "imageRef", "buttonText", "buttonUrl", "layoutVariant" })
        {
            Assert.Equal(string.Empty, box[field]!.GetValue<string>());
        }
        Assert.True(box["isVisible"]!.GetValue<bool>());
        Assert.False(Block(template, "about", 2)["isVisible"]!.GetValue<bool>());   // the map was hidden on the site

        // handover.json names the boxes (section 9.3) and says it is the newer shape (9.2).
        var said = JsonNode.Parse(Encoding.UTF8.GetString(files["handover.json"]))!.AsObject();
        Assert.Equal("1.1", said["handoverVersion"]!.GetValue<string>());
        Assert.Equal("0.5", said["contractVersion"]!.GetValue<string>());
        Assert.Equal(new[] { "articles", "forms", "hostSections" }, said["catalog"]!.AsObject().Select(p => p.Key));
        Assert.Equal(
            new[] { "1|Calculator|Calculator|Mortgage payment", "2|Gallery|Photo Gallery|Our shop", "3|Maps|Map|Find us" },
            said["catalog"]!["hostSections"]!.AsArray().Select(h => $"{h!["id"]}|{h["kind"]}|{h["label"]}|{h["title"]}"));
        Assert.DoesNotContain("HostSection", said["allowedBlockTypes"]!.AsArray().Select(t => t!.GetValue<string>()));

        // Of a section only its heading travels. Not its other words, its settings or its pictures:
        // the calculator's chart and the gallery's photo are not in the package at all.
        Assert.Equal(2, files.Keys.Count(name => name.StartsWith("assets/")));   // the logo and the hero picture, as before
        Assert.DoesNotContain(files.Keys, name => name.Contains("inside") || name.Contains("rates-chart"));
        var everything = Encoding.UTF8.GetString(files["template.json"]) + Encoding.UTF8.GetString(files["handover.json"]);
        foreach (var kept in new[] { "website-gallery", "rates-chart", "12 Main Street", "zq-only-in-ipro", "Rates change weekly", "What would it cost", "Ask us", "masonry" })
        {
            Assert.DoesNotContain(kept, everything);
        }

        // The hand-over's record keeps each section as it was.
        var record = await SiteHandoverService.RecordAsync(db, seeded.Site, handover, "owner");
        db.ChangeTracker.Clear();
        var recorded = SiteHandoverService.HostSections(await SiteHandoverService.FindAsync(db, seeded.Site.Id, record.HandoverId));
        Assert.Equal(new[] { "1|home|Calculator", "2|about|Gallery", "3|about|Maps" }, recorded.Select(h => $"{h.Id}|{h.PageSlug}|{h.BlockType}"));
        Assert.Contains(seeded.GalleryPicture, recorded[1].SettingsJson);
        Assert.Equal("12 Main Street", recorded[2].Body);
        Assert.False(recorded[2].IsVisible);
        // And which row each was, so that the section the site has when the box returns can be told to be this one.
        Assert.Equal(new[] { seeded.CalculatorId, seeded.GalleryId, seeded.MapId }, recorded.Select(h => h.BlockId));
        Assert.DoesNotContain("BlockId", everything);
    }

    [Fact]
    public async Task A_site_without_such_sections_goes_out_as_the_v04_package_it_always_was()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedFittingSiteAsync(db, blob);

        var handover = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        Assert.True(handover.CanHandOver, string.Join("\n", handover.Blockers));
        var files = Entries(handover.Zip);
        Assert.Equal("0.4", JsonNode.Parse(Encoding.UTF8.GetString(files["template.json"]))!["contractVersion"]!.GetValue<string>());
        var said = JsonNode.Parse(Encoding.UTF8.GetString(files["handover.json"]))!.AsObject();
        Assert.Equal("1.0", said["handoverVersion"]!.GetValue<string>());
        Assert.Equal("0.4", said["contractVersion"]!.GetValue<string>());
        Assert.Equal(new[] { "articles", "forms" }, said["catalog"]!.AsObject().Select(p => p.Key));
        Assert.DoesNotContain("HostSection", Encoding.UTF8.GetString(files["template.json"]));
        Assert.Empty(handover.HostSections);
        var record = await SiteHandoverService.RecordAsync(db, seeded.Site, handover, "owner");
        Assert.Null(record.HostSectionsJson);
        // And its return says "0.4": one that claims "0.5" is not this hand-over's (section 0).
        var context = await BuilderImportService.ContextAsync(db, seeded.Site, 0, handover.HandoverId);
        Assert.Empty(BuilderPackageReader.Read(Return(handover.Zip, _ => { }), context).Problems);
        var claimed = Assert.Single(BuilderPackageReader.Read(Return(handover.Zip, template => template["contractVersion"] = "0.5"), context).Problems);
        Assert.Equal(("0", "This site was handed over as a version \"0.4\" package, and its return states the same version. This package says \"0.5\"."), (claimed.Rule, claimed.Message));
    }

    [Fact]
    public async Task The_return_puts_every_section_back_exactly_as_it_was_and_again_after_a_put_back()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var audit = new RecordingAudit();
        var seeded = await SeedWithHostSectionsAsync(db, blob);
        var before = await HostSectionsOnSiteAsync(db, seeded.Site.Id);
        Assert.Equal(3, before.Count);

        var handover = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        await SiteHandoverService.RecordAsync(db, seeded.Site, handover, "owner");
        db.ChangeTracker.Clear();

        // The builder changes a headline and nothing else.
        var returned = Return(handover.Zip, template => Block(template, "home", 0)["heading"] = "Edited in the builder");
        var importId = await UploadAsync(db, blob, audit, seeded.Site.AgentUserId, returned);

        var review = await ReviewAsync(db, blob, audit, importId);
        Assert.Empty(review.Plan!.Problems);
        Assert.All(review.Plan.Pages, p => Assert.Empty(p.SectionsGoing));
        Assert.All(review.Plan.Pages, p => Assert.Empty(p.SectionsLost));
        Assert.All(review.Plan.Pages, p => Assert.Empty(p.BoxNotes));
        Assert.All(review.Plan.Pages, p => Assert.False(p.ChangedSinceHandover));
        // The review names the sections by what they are, never as boxes.
        var home = review.Plan.Pages.Single(p => p.Page.IsHomePage);
        Assert.Equal(new[] { WebsiteBlockTypes.Hero, WebsiteBlockTypes.Calculator, WebsiteBlockTypes.Blog }, home.Page.Blocks.OrderBy(b => b.SortOrder).Select(b => b.BlockType));
        var html = await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Review.cshtml", review);
        Assert.Contains("Hero, Calculator, Blog", html);
        Assert.DoesNotContain("name=\"sections\"", html);
        Assert.DoesNotContain("HostSection", html);
        Assert.DoesNotContain("Host Section", html);

        var applied = Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Apply(importId, null, null, plan: review.Plan.Fingerprint));
        Assert.Equal("Index", applied.ActionName);
        db.ChangeTracker.Clear();

        // Every field of every such section: text, picture, button, settings, layout, shown or hidden.
        Assert.Equal(before, await HostSectionsOnSiteAsync(db, seeded.Site.Id));
        Assert.Equal("Edited in the builder", await db.WebsiteContentBlocks.Where(b => b.WebsitePageId == seeded.HomeId && b.BlockType == WebsiteBlockTypes.Hero).Select(b => b.Heading).SingleAsync());
        // A box is never stored: the site holds only iPro's own kinds of section.
        Assert.False(await db.WebsiteContentBlocks.AnyAsync(b => b.BlockType == BuilderContract.HostSection));
        Assert.DoesNotContain(BuilderContract.HostSection, WebsiteBlockTypes.All);

        // The site is put back and the same return uploaded again. Every section has a new row now:
        // it is still known for what it says, so nothing is named as going and nothing comes twice.
        Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Restore(importId));
        db.ChangeTracker.Clear();
        Assert.Equal(before, await HostSectionsOnSiteAsync(db, seeded.Site.Id));
        var again = await UploadAsync(db, blob, audit, seeded.Site.AgentUserId, returned);
        var second = await ReviewAsync(db, blob, audit, again);
        Assert.Empty(second.Plan!.Problems);
        Assert.All(second.Plan.Pages, p => Assert.Empty(p.SectionsGoing));
        Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Apply(again, null, null, plan: second.Plan.Fingerprint));
        db.ChangeTracker.Clear();
        Assert.Equal(before, await HostSectionsOnSiteAsync(db, seeded.Site.Id));
    }

    [Fact]
    public async Task A_box_moved_hidden_or_removed_in_the_builder_and_a_removed_one_kept()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var audit = new RecordingAudit();
        var seeded = await SeedWithHostSectionsAsync(db, blob);
        var handover = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        await SiteHandoverService.RecordAsync(db, seeded.Site, handover, "owner");
        db.ChangeTracker.Clear();

        // In the builder: the calculator's box goes from Home to the end of Contact, the gallery's
        // box is hidden, the map's box is removed.
        var returned = Return(handover.Zip, template =>
        {
            var home = Page(template, "home")["blocks"]!.AsArray();
            var calculator = home.Single(b => BoxId(b) == 1)!;
            home.Remove(calculator);
            calculator["sortOrder"] = 9;
            Page(template, "contact")["blocks"]!.AsArray().Add(calculator);
            var about = Page(template, "about")["blocks"]!.AsArray();
            about.Single(b => BoxId(b) == 2)!["isVisible"] = false;
            about.Remove(about.Single(b => BoxId(b) == 3));
        });
        var importId = await UploadAsync(db, blob, audit, seeded.Site.AgentUserId, returned);

        // The review: the map is named on its page, by its own row, with a box to keep it. Nothing else is going.
        var review = await ReviewAsync(db, blob, audit, importId);
        var about = review.Plan!.Pages.Single(p => p.Page.Slug == "about");
        var going = Assert.Single(about.SectionsGoing);
        Assert.Equal((seeded.MapId, WebsiteBlockTypes.Maps, "Find us", true, false), (going.BlockId, going.BlockType, going.Title, going.RemovedInBuilder, going.Kept));
        Assert.Equal(new[] { WebsiteBlockTypes.Maps }, about.SectionsLost);
        Assert.All(review.Plan.Pages.Where(p => p.Page.Slug != "about"), p => Assert.Empty(p.SectionsGoing));
        Assert.Equal(new[] { WebsiteBlockTypes.Hero, WebsiteBlockTypes.Blog }, review.Plan.Pages.Single(p => p.Page.IsHomePage).Page.Blocks.OrderBy(b => b.SortOrder).Select(b => b.BlockType));
        var contact = review.Plan.Pages.Single(p => p.Page.Slug == "contact");
        Assert.Equal(WebsiteBlockTypes.Calculator, contact.Page.Blocks.OrderBy(b => b.SortOrder).Last().BlockType);
        // A section nobody could look at in the builder now stands on another page: the review says so.
        Assert.Equal(new[] { "Calculator \"Mortgage payment\" comes from another page (/home)." }, contact.BoxNotes);
        // ...and so does a section visitors see today that the designer hid.
        Assert.Equal(new[] { "Photo Gallery \"Our shop\" is shown on the site and comes back HIDDEN." }, about.BoxNotes);
        var html = await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Review.cshtml", review);
        Assert.Contains($"name=\"sections\" value=\"{seeded.MapId}\"", html);
        Assert.Contains("was removed in the builder, so it goes.", html);
        Assert.Contains("Tick the box to keep it at the end of this page.", html);
        Assert.Contains("comes from another page (/home).", html);

        // Bringing it in with the box ticked but the page not updated first is refused: what is written
        // is what the screen showed.
        var early = Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Apply(importId, null, null, plan: review.Plan.Fingerprint, sections: new[] { seeded.MapId }));
        Assert.Equal("Review", early.ActionName);
        Assert.Equal(seeded.MapId.ToString(), early.RouteValues!["sections"]);
        Assert.Contains(audit.Entries, e => e.Action == "BuilderImportNotApplied" && e.Details.Contains("no longer what the screen showed"));
        db.ChangeTracker.Clear();
        Assert.Equal(WebsiteBuilderImportStatus.Pending, await db.WebsiteBuilderImports.Where(i => i.Id == importId).Select(i => i.Status).SingleAsync());

        // "Update" turns the ticked box into the review's own address; the map is kept, at the end of its page.
        var chosen = Assert.IsType<RedirectToActionResult>(Controller(db, blob, audit).Choose(importId, null, null, null, new[] { seeded.MapId, -4, 0 }));
        Assert.Equal(seeded.MapId.ToString(), chosen.RouteValues!["sections"]);
        var keptReview = await ReviewAsync(db, blob, audit, importId, seeded.MapId.ToString());
        var keptAbout = keptReview.Plan!.Pages.Single(p => p.Page.Slug == "about");
        Assert.True(Assert.Single(keptAbout.SectionsGoing).Kept);
        Assert.Empty(keptAbout.SectionsLost);
        Assert.Equal(new[] { WebsiteBlockTypes.Text, WebsiteBlockTypes.Gallery, WebsiteBlockTypes.Maps }, keptAbout.Page.Blocks.OrderBy(b => b.SortOrder).Select(b => b.BlockType));
        Assert.NotEqual(review.Plan.Fingerprint, keptReview.Plan.Fingerprint);
        Assert.Contains($"&sections={seeded.MapId}", keptReview.LookAddress);
        var keptHtml = await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Review.cshtml", keptReview);
        Assert.Contains("and is kept as the site has it: it goes to the end of this page.", keptHtml);
        Assert.Matches($"name=\"sections\" value=\"{seeded.MapId}\"[^>]*checked", keptHtml);
        // A section of another customer's site, one that comes back in its box, a row that does not exist:
        // none of them is this review's to keep, and posting them changes nothing.
        var otherSite = await BuilderImport571Tests.SeedSiteAsync(db);
        var theirs = await db.WebsiteContentBlocks.Where(b => b.WebsitePage.AgentWebsiteId == otherSite.Id && b.BlockType == WebsiteBlockTypes.PriceList).Select(b => b.Id).SingleAsync();
        db.ChangeTracker.Clear();
        var foreign = await ReviewAsync(db, blob, audit, importId, $"{theirs},{seeded.CalculatorId},987654321");
        Assert.Equal(review.Plan.Fingerprint, foreign.Plan!.Fingerprint);
        Assert.All(foreign.Plan.Pages, p => Assert.DoesNotContain(p.SectionsGoing, g => g.Kept));
        Assert.Equal(review.Plan.Pages.Sum(p => p.Page.Blocks.Count), foreign.Plan.Pages.Sum(p => p.Page.Blocks.Count));

        var applied = Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Apply(importId, null, null, plan: keptReview.Plan.Fingerprint, sections: new[] { seeded.MapId }));
        Assert.Equal("Index", applied.ActionName);
        Assert.Contains(audit.Entries, e => e.Action == "BuilderImportApplied" && e.Details.Contains("1 section(s) of replaced pages kept though not in the package"));
        db.ChangeTracker.Clear();

        var after = await db.WebsiteContentBlocks.AsNoTracking().Where(b => b.WebsitePage.AgentWebsiteId == seeded.Site.Id)
            .OrderBy(b => b.WebsitePage.Slug).ThenBy(b => b.SortOrder)
            .Select(b => new { b.WebsitePage.Slug, b.BlockType, b.Heading, b.IsVisible }).ToListAsync();
        Assert.Equal(new[] { "about|Gallery|Our shop|False", "about|Maps|Find us|False", "contact|Calculator|Mortgage payment|True" },
            after.Where(x => !BuilderContract.BlockTypes.Contains(x.BlockType)).Select(x => $"{x.Slug}|{x.BlockType}|{x.Heading}|{x.IsVisible}"));
        // The map is where a kept section goes, the end of its page, and says what it said.
        var map = await db.WebsiteContentBlocks.AsNoTracking().SingleAsync(b => b.BlockType == WebsiteBlockTypes.Maps && b.WebsitePage.AgentWebsiteId == seeded.Site.Id);
        Assert.Equal("12 Main Street", map.Body);
        Assert.Equal(map.SortOrder, await db.WebsiteContentBlocks.Where(b => b.WebsitePageId == map.WebsitePageId).MaxAsync(b => b.SortOrder));

        // The designer goes on working and the same hand-over comes back a second time. Every section
        // has a new row since the first return went in, and the calculator stands on Contact now. It is
        // still known for what it says: it is not named as going, and not as coming "from another page"
        // again. The map, which this package still does not have, is named again.
        var secondId = await UploadAsync(db, blob, audit, seeded.Site.AgentUserId, returned);
        var second = await ReviewAsync(db, blob, audit, secondId);
        Assert.Empty(second.Plan!.Problems);
        var stillGoing = Assert.Single(second.Plan.Pages.SelectMany(p => p.SectionsGoing));
        Assert.Equal((map.Id, WebsiteBlockTypes.Maps, true), (stillGoing.BlockId, stillGoing.BlockType, stillGoing.RemovedInBuilder));
        Assert.All(second.Plan.Pages, p => Assert.Empty(p.BoxNotes));
        var secondKept = await ReviewAsync(db, blob, audit, secondId, map.Id.ToString());
        var secondApplied = Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Apply(secondId, null, null, plan: secondKept.Plan!.Fingerprint, sections: new[] { map.Id }));
        Assert.Equal("Index", secondApplied.ActionName);
        db.ChangeTracker.Clear();
        Assert.Equal(new[] { "about|Gallery", "about|Maps", "contact|Calculator" },
            (await HostSectionsOnSiteAsync(db, seeded.Site.Id)).Select(line => string.Join("|", line.Split('|').Take(2))));
    }

    [Fact]
    public async Task A_second_section_of_the_same_kind_placed_above_the_first_is_the_one_named()
    {
        // The first version told the two apart by counting kinds in order, so the calculator that went
        // to the builder was named as "added since" and the new one was dropped without a word. All
        // three reviews found it.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var audit = new RecordingAudit();
        var seeded = await SeedWithHostSectionsAsync(db, blob);
        var handover = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        await SiteHandoverService.RecordAsync(db, seeded.Site, handover, "owner");
        db.ChangeTracker.Clear();

        // Meanwhile the customer adds a second calculator ABOVE the first, and a price list below.
        (await db.WebsiteContentBlocks.SingleAsync(b => b.Id == seeded.CalculatorId)).SortOrder = 2;
        (await db.WebsiteContentBlocks.SingleAsync(b => b.WebsitePageId == seeded.HomeId && b.BlockType == WebsiteBlockTypes.Blog)).SortOrder = 3;
        var second = new WebsiteContentBlock { WebsitePageId = seeded.HomeId, BlockType = WebsiteBlockTypes.Calculator, Heading = "Savings growth", SortOrder = 1, SettingsJson = "{\"Type\":\"savings\"}" };
        var prices = new WebsiteContentBlock { WebsitePageId = seeded.HomeId, BlockType = WebsiteBlockTypes.PriceList, Heading = "Fees", SortOrder = 8, SettingsJson = "{\"Kind\":\"fees\"}" };
        db.AddRange(second, prices);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var importId = await UploadAsync(db, blob, audit, seeded.Site.AgentUserId, Return(handover.Zip, _ => { }));
        var review = await ReviewAsync(db, blob, audit, importId);
        var home = review.Plan!.Pages.Single(p => p.Page.IsHomePage);
        Assert.True(home.ChangedSinceHandover);
        // The calculator that went out comes back in its box; the two made since are the ones named.
        Assert.Equal(new[] { $"{second.Id}|Calculator|Savings growth|False", $"{prices.Id}|PriceList|Fees|False" },
            home.SectionsGoing.Select(g => $"{g.BlockId}|{g.BlockType}|{g.Title}|{g.RemovedInBuilder}"));
        var html = await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Review.cshtml", review);
        Assert.Contains("is on the site but not in the package, so it goes.", html);
        Assert.Contains("Calculator &quot;Savings growth&quot;", html);

        // Both kept: each stands once, the first calculator where its box is, the kept ones at the end.
        var keys = new[] { second.Id, prices.Id };
        var keptReview = await ReviewAsync(db, blob, audit, importId, string.Join(",", keys));
        var applied = Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Apply(importId, null, null, plan: keptReview.Plan!.Fingerprint, sections: keys));
        Assert.Equal("Index", applied.ActionName);
        db.ChangeTracker.Clear();
        var blocks = await db.WebsiteContentBlocks.AsNoTracking().Where(b => b.WebsitePageId == seeded.HomeId).OrderBy(b => b.SortOrder).ToListAsync();
        Assert.Equal(new[] { "Hero|Fresh every morning", "Calculator|Mortgage payment", "Blog|From the oven", "Calculator|Savings growth", "PriceList|Fees" },
            blocks.Select(b => $"{b.BlockType}|{b.Heading}"));
        Assert.Equal("{\"Kind\":\"fees\"}", blocks[4].SettingsJson);
    }

    [Fact]
    public async Task What_is_kept_is_the_section_the_site_has_now_and_one_the_customer_deleted_is_not_offered_back()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var audit = new RecordingAudit();
        var seeded = await SeedWithHostSectionsAsync(db, blob);
        var handover = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        await SiteHandoverService.RecordAsync(db, seeded.Site, handover, "owner");
        db.ChangeTracker.Clear();

        // Meanwhile the customer rewrites the map's section, deletes the gallery and makes a new one.
        (await db.WebsiteContentBlocks.SingleAsync(b => b.Id == seeded.MapId)).Body = "99 New Street";
        db.WebsiteContentBlocks.Remove(await db.WebsiteContentBlocks.SingleAsync(b => b.Id == seeded.GalleryId));
        var fresh = new WebsiteContentBlock { WebsitePageId = seeded.AboutId, BlockType = WebsiteBlockTypes.Gallery, Heading = "New photos", SortOrder = 1 };
        db.Add(fresh);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // And in the builder both boxes of that page were removed.
        var returned = Return(handover.Zip, template =>
        {
            var about = Page(template, "about")["blocks"]!.AsArray();
            foreach (var box in about.Where(b => BoxId(b) > 0).ToList()) about.Remove(box);
        });
        var importId = await UploadAsync(db, blob, audit, seeded.Site.AgentUserId, returned);

        // What the site has is what is named: the new gallery (which the package never had) and the map
        // (the section that went out, rewritten since; its box was removed). The gallery the customer
        // deleted is not offered back.
        var review = await ReviewAsync(db, blob, audit, importId);
        var about = review.Plan!.Pages.Single(p => p.Page.Slug == "about");
        Assert.Equal(new[] { $"{fresh.Id}|Gallery|New photos|False", $"{seeded.MapId}|Maps|Find us|True" },
            about.SectionsGoing.Select(g => $"{g.BlockId}|{g.BlockType}|{g.Title}|{g.RemovedInBuilder}"));

        var keptReview = await ReviewAsync(db, blob, audit, importId, seeded.MapId.ToString());
        Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Apply(importId, null, null, plan: keptReview.Plan!.Fingerprint, sections: new[] { seeded.MapId }));
        db.ChangeTracker.Clear();
        var blocks = await db.WebsiteContentBlocks.AsNoTracking().Where(b => b.WebsitePageId == seeded.AboutId).OrderBy(b => b.SortOrder).ToListAsync();
        Assert.Equal(new[] { WebsiteBlockTypes.Text, WebsiteBlockTypes.Maps }, blocks.Select(b => b.BlockType));
        Assert.Equal(("99 New Street", false), (blocks[1].Body, blocks[1].IsVisible));   // what the customer wrote, still hidden
    }

    [Fact]
    public async Task A_box_that_comes_back_twice_or_shown_though_hidden_is_said_and_written_so()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var audit = new RecordingAudit();
        var seeded = await SeedWithHostSectionsAsync(db, blob);
        var handover = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        await SiteHandoverService.RecordAsync(db, seeded.Site, handover, "owner");
        db.ChangeTracker.Clear();

        // The calculator's box is on Home and, a second time, on Contact; the hidden map's box is shown.
        var returned = Return(handover.Zip, template =>
        {
            var copy = JsonNode.Parse(Page(template, "home")["blocks"]!.AsArray().Single(b => BoxId(b) == 1)!.ToJsonString())!;
            copy["sortOrder"] = 9;
            Page(template, "contact")["blocks"]!.AsArray().Add(copy);
            Page(template, "about")["blocks"]!.AsArray().Single(b => BoxId(b) == 3)!["isVisible"] = true;
        });
        var importId = await UploadAsync(db, blob, audit, seeded.Site.AgentUserId, returned);

        var review = await ReviewAsync(db, blob, audit, importId);
        Assert.Empty(review.Plan!.Pages.Single(p => p.Page.IsHomePage).BoxNotes);   // where it stood, it is simply the section
        Assert.Equal(new[] { "Calculator \"Mortgage payment\" comes back as a copy: the package has it 2 times." }, review.Plan.Pages.Single(p => p.Page.Slug == "contact").BoxNotes);
        Assert.Equal(new[] { "Map \"Find us\" is HIDDEN on the site and comes back shown." }, review.Plan.Pages.Single(p => p.Page.Slug == "about").BoxNotes);
        var html = await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Review.cshtml", review);
        Assert.Contains("is HIDDEN on the site and comes back shown.", html);
        Assert.Contains("comes back as a copy: the package has it 2 times.", html);
        // The notes are part of what the reviewer read: a plan that says other things is another plan.
        Assert.NotEqual(review.Plan.Fingerprint,
            (await ReviewAsync(db, blob, audit, await UploadAsync(db, blob, audit, seeded.Site.AgentUserId, Return(handover.Zip, _ => { })))).Plan!.Fingerprint);

        Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Apply(importId, null, null, plan: review.Plan.Fingerprint));
        db.ChangeTracker.Clear();
        var calculators = await db.WebsiteContentBlocks.AsNoTracking().Where(b => b.BlockType == WebsiteBlockTypes.Calculator && b.WebsitePage.AgentWebsiteId == seeded.Site.Id)
            .OrderBy(b => b.WebsitePage.Slug).Select(b => new { b.WebsitePage.Slug, b.Heading, b.SettingsJson, b.ImageUrl }).ToListAsync();
        Assert.Equal(new[] { "contact", "home" }, calculators.Select(c => c.Slug));
        Assert.All(calculators, c => Assert.Equal(("Mortgage payment", "{\"Type\":\"zq-only-in-ipro\"}"), (c.Heading, c.SettingsJson)));
        Assert.Equal(calculators[0].ImageUrl, calculators[1].ImageUrl);
        Assert.True(await db.WebsiteContentBlocks.Where(b => b.Id != seeded.MapId && b.BlockType == WebsiteBlockTypes.Maps && b.WebsitePage.AgentWebsiteId == seeded.Site.Id).Select(b => b.IsVisible).SingleAsync());

        // The same package once more: the site now has the two calculators and a shown map, and that is
        // what the package has. Nothing is named, nothing is said twice, and nothing multiplies.
        var secondId = await UploadAsync(db, blob, audit, seeded.Site.AgentUserId, returned);
        var second = await ReviewAsync(db, blob, audit, secondId);
        Assert.All(second.Plan!.Pages, p => Assert.Empty(p.SectionsGoing));
        Assert.All(second.Plan.Pages, p => Assert.Empty(p.BoxNotes));
        Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Apply(secondId, null, null, plan: second.Plan.Fingerprint));
        db.ChangeTracker.Clear();
        Assert.Equal(new[] { "about|Gallery", "about|Maps", "contact|Calculator", "home|Calculator" },
            (await HostSectionsOnSiteAsync(db, seeded.Site.Id)).Select(line => string.Join("|", line.Split('|').Take(2))));
    }

    [Fact]
    public async Task A_section_changed_hidden_or_deleted_in_iPro_while_its_package_is_out()
    {
        // The re-check's finding. A box brought the section "as it was at the hand-over": a customer's
        // edit was undone, and the edited section was listed as going beside the old one coming back
        // (tick it and there were two). Saving a section without changing it was enough: the page
        // editor then writes the section's settings out in full.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var audit = new RecordingAudit();
        var seeded = await SeedWithHostSectionsAsync(db, blob);
        var handover = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        await SiteHandoverService.RecordAsync(db, seeded.Site, handover, "owner");
        db.ChangeTracker.Clear();

        // Meanwhile: the map is rewritten; the calculator is saved again (its settings written out in
        // full) and hidden; the gallery is deleted.
        (await db.WebsiteContentBlocks.SingleAsync(b => b.Id == seeded.MapId)).Body = "99 New Street";
        var calculator = await db.WebsiteContentBlocks.SingleAsync(b => b.Id == seeded.CalculatorId);
        calculator.SettingsJson = "{\"Type\":\"zq-only-in-ipro\",\"Rate\":5.25}";
        calculator.IsVisible = false;
        db.WebsiteContentBlocks.Remove(await db.WebsiteContentBlocks.SingleAsync(b => b.Id == seeded.GalleryId));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // The return has all three boxes, untouched.
        var importId = await UploadAsync(db, blob, audit, seeded.Site.AgentUserId, Return(handover.Zip, _ => { }));
        var review = await ReviewAsync(db, blob, audit, importId);
        Assert.Empty(review.Plan!.Problems);
        // Nothing is named as going: each section the site has IS the one its box brings.
        Assert.All(review.Plan.Pages, p => Assert.Empty(p.SectionsGoing));
        Assert.Empty(review.Plan.Pages.Single(p => p.Page.IsHomePage).BoxNotes);
        // The gallery the customer deleted comes back, because its box did, and the review says so.
        Assert.Equal(new[] { "Photo Gallery \"Our shop\" is not on the site any more as it was handed over: it comes back with its box, as it was then." },
            review.Plan.Pages.Single(p => p.Page.Slug == "about").BoxNotes);

        Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Apply(importId, null, null, plan: review.Plan.Fingerprint));
        db.ChangeTracker.Clear();
        var after = await db.WebsiteContentBlocks.AsNoTracking().Where(b => b.WebsitePage.AgentWebsiteId == seeded.Site.Id).ToListAsync();
        var map = after.Single(b => b.BlockType == WebsiteBlockTypes.Maps);
        Assert.Equal(("99 New Street", false), (map.Body, map.IsVisible));                              // the customer's words, still hidden
        var calc = after.Single(b => b.BlockType == WebsiteBlockTypes.Calculator);
        Assert.Equal(("{\"Type\":\"zq-only-in-ipro\",\"Rate\":5.25}", false), (calc.SettingsJson, calc.IsVisible));   // saved again, hidden since: both stay
        Assert.Contains(seeded.GalleryPicture, after.Single(b => b.BlockType == WebsiteBlockTypes.Gallery).SettingsJson);

        // A section rewritten between the review and "bring it in" is not written unseen: the plan the
        // reviewer read is no longer the plan.
        var againId = await UploadAsync(db, blob, audit, seeded.Site.AgentUserId, Return(handover.Zip, _ => { }));
        var read = await ReviewAsync(db, blob, audit, againId);
        (await db.WebsiteContentBlocks.SingleAsync(b => b.Id == map.Id)).Body = "1 Late Change Road";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var late = Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Apply(againId, null, null, plan: read.Plan!.Fingerprint));
        Assert.Equal("Review", late.ActionName);
    }

    [Fact]
    public async Task A_page_given_another_address_in_iPro_is_still_the_page_its_boxes_stood_on()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var audit = new RecordingAudit();
        var seeded = await SeedWithHostSectionsAsync(db, blob);
        var handover = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        await SiteHandoverService.RecordAsync(db, seeded.Site, handover, "owner");
        db.ChangeTracker.Clear();

        (await db.WebsitePages.SingleAsync(p => p.Id == seeded.AboutId)).Slug = "about-us";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // The gallery's box was removed in the builder; the map's box stayed.
        var returned = Return(handover.Zip, template =>
        {
            var about = Page(template, "about")["blocks"]!.AsArray();
            about.Remove(about.Single(b => BoxId(b) == 2));
        });
        var review = await ReviewAsync(db, blob, audit, await UploadAsync(db, blob, audit, seeded.Site.AgentUserId, returned));
        var page = review.Plan!.Pages.Single(p => p.Page.Id == seeded.AboutId);
        Assert.Equal(PageChange.Replaced, page.Change);
        var going = Assert.Single(page.SectionsGoing);
        Assert.Equal((seeded.GalleryId, true), (going.BlockId, going.RemovedInBuilder));
        Assert.Empty(page.BoxNotes);   // the map did not come "from another page": it is this page
    }

    [Fact]
    public async Task A_package_built_from_scratch_can_keep_a_section_it_cannot_carry_and_the_look_says_what_each_page_is()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var audit = new RecordingAudit();
        var site = await BuilderImport571Tests.SeedSiteAsync(db);   // Home: a hero and a hidden reviews badge
        var homeId = await db.WebsitePages.Where(p => p.AgentWebsiteId == site.Id && p.IsHomePage).Select(p => p.Id).SingleAsync();
        var reviewsId = await db.WebsiteContentBlocks.Where(b => b.WebsitePageId == homeId && b.BlockType == WebsiteBlockTypes.Reviews).Select(b => b.Id).SingleAsync();

        var importId = await UploadAsync(db, blob, audit, site.AgentUserId, BuilderImport571Tests.Zip(BuilderImport571Tests.Designer()));
        var review = await ReviewAsync(db, blob, audit, importId);
        var going = Assert.Single(review.Plan!.Pages.Single(p => p.Page.IsHomePage).SectionsGoing);
        Assert.Equal((reviewsId, WebsiteBlockTypes.Reviews, false, false), (going.BlockId, going.BlockType, going.RemovedInBuilder, going.Kept));
        var html = await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Review.cshtml", review);
        Assert.Contains("is not in the package (a site built from scratch cannot bring one), so it goes.", html);

        var key = reviewsId.ToString();
        var keptReview = await ReviewAsync(db, blob, audit, importId, key);
        var home = keptReview.Plan!.Pages.Single(p => p.Page.IsHomePage);
        Assert.Empty(home.SectionsLost);
        Assert.Equal(WebsiteBlockTypes.Reviews, home.Page.Blocks.OrderBy(b => b.SortOrder).Last().BlockType);

        // The look says which pages come from the package, and shows the kept section's page as it will be.
        var import = await db.WebsiteBuilderImports.AsNoTracking().SingleAsync(i => i.Id == importId);
        var look = await BuilderImportPreviewBuilder.BuildAsync(db, import, "home", null, null, null, key);
        Assert.StartsWith("THIS PAGE COMES FROM THE PACKAGE", look!.PreviewNote);
        Assert.Contains(look.CurrentPage!.Blocks, b => b.BlockType == WebsiteBlockTypes.Reviews);
        Assert.StartsWith("THIS PAGE IS NOT IN THE PACKAGE", (await BuilderImportPreviewBuilder.BuildAsync(db, import, "menu", null, null))!.PreviewNote);
        Assert.StartsWith("THIS PAGE IS NEW", (await BuilderImportPreviewBuilder.BuildAsync(db, import, "resources", null, null))!.PreviewNote);

        // And the address the review's "Look" opens really carries the choice to the page that draws it.
        var lookKey = await BuilderImportService.LookKeyAsync(db, await db.WebsiteBuilderImports.SingleAsync(i => i.Id == importId));
        db.ChangeTracker.Clear();
        var portal = new PublicWebsiteController(
            db, new PackageEntitlementService(new UnitOfWork(db), db), null!, NullLogger<PublicWebsiteController>.Instance, new ConfigurationBuilder().Build(),
            DataProtectionProvider.Create($"look-576-{Guid.NewGuid():N}"), blob, new MemoryCache(new MemoryCacheOptions()), null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var shown = Assert.IsType<ViewResult>(await portal.PreviewBuilderImport(importId, lookKey, "home", null, null, null, key));
        Assert.Contains(Assert.IsType<PublicWebsiteViewModel>(shown.Model).CurrentPage!.Blocks, b => b.BlockType == WebsiteBlockTypes.Reviews);
        Assert.Equal("A look at a Template Builder package before it is brought in. THIS PAGE COMES FROM THE PACKAGE and would replace the one the site has now. " +
            "Header, menu, colours and fonts are the site's own design, not the builder's. Nothing on the site has changed.", (string)portal.ViewData["PreviewBanner"]!);
        var plain = Assert.IsType<ViewResult>(await portal.PreviewBuilderImport(importId, lookKey, "home", null, null, null));
        Assert.DoesNotContain(Assert.IsType<PublicWebsiteViewModel>(plain.Model).CurrentPage!.Blocks, b => b.BlockType == WebsiteBlockTypes.Reviews);

        await Controller(db, blob, audit).Apply(importId, null, null, plan: keptReview.Plan.Fingerprint, sections: new[] { reviewsId });
        db.ChangeTracker.Clear();
        var reviews = await db.WebsiteContentBlocks.AsNoTracking().SingleAsync(b => b.WebsitePageId == homeId && b.BlockType == WebsiteBlockTypes.Reviews);
        Assert.Equal(("Reviews", "badge", false), (reviews.Heading, reviews.LayoutVariant, reviews.IsVisible));
        Assert.Equal(reviews.SortOrder, await db.WebsiteContentBlocks.Where(b => b.WebsitePageId == homeId).MaxAsync(b => b.SortOrder));
    }

    [Fact]
    public async Task Kept_sections_cannot_take_a_page_past_what_a_package_holds()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var audit = new RecordingAudit();
        var site = await BuilderImport571Tests.SeedSiteAsync(db);
        var homeId = await db.WebsitePages.Where(p => p.AgentWebsiteId == site.Id && p.IsHomePage).Select(p => p.Id).SingleAsync();
        db.AddRange(Enumerable.Range(0, 40).Select(i => new WebsiteContentBlock { WebsitePageId = homeId, BlockType = WebsiteBlockTypes.PriceList, Heading = $"List {i}", SortOrder = 10 + i }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var ids = await db.WebsiteContentBlocks.Where(b => b.WebsitePageId == homeId && b.BlockType == WebsiteBlockTypes.PriceList).Select(b => b.Id).ToListAsync();

        var importId = await UploadAsync(db, blob, audit, site.AgentUserId, BuilderImport571Tests.Zip(BuilderImport571Tests.Designer()));
        Assert.Empty((await ReviewAsync(db, blob, audit, importId, string.Join(",", ids.Take(3)))).Plan!.Problems);
        var all = await ReviewAsync(db, blob, audit, importId, string.Join(",", ids));
        var problem = Assert.Single(all.Plan!.Problems);
        Assert.Matches("^\"Home\" would have 4\\d sections with the ones kept, and a page that goes to the builder holds at most 40\\. Keep fewer sections on that page\\.$", problem);
        var refused = Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Apply(importId, null, null, plan: all.Plan.Fingerprint, sections: ids.ToArray()));
        Assert.Equal("Review", refused.ActionName);
        db.ChangeTracker.Clear();
        Assert.Equal(42, await db.WebsiteContentBlocks.CountAsync(b => b.WebsitePageId == homeId));   // nothing was written
    }

    [Fact]
    public async Task The_door_takes_a_locked_box_only_as_the_contract_says()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedWithHostSectionsAsync(db, blob);
        var handover = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        await SiteHandoverService.RecordAsync(db, seeded.Site, handover, "owner");
        db.ChangeTracker.Clear();
        var context = await BuilderImportService.ContextAsync(db, seeded.Site, 0, handover.HandoverId);
        Assert.Equal(new[] { 1, 2, 3 }, context.HostSectionIds!.OrderBy(id => id));

        IReadOnlyList<ImportProblem> Problems(Action<JsonObject> edit) => BuilderPackageReader.Read(Return(handover.Zip, edit), context).Problems;
        JsonObject Box(JsonObject template) => Page(template, "home")["blocks"]!.AsArray().Single(b => BoxId(b) == 1)!.AsObject();
        void RemoveBoxes(JsonObject template)
        {
            foreach (var page in template["pages"]!.AsArray())
            {
                var blocks = page!["blocks"]!.AsArray();
                foreach (var box in blocks.Where(b => BoxId(b) > 0).ToList()) blocks.Remove(box);
            }
        }

        // As it went out, it comes back in; the same box twice is allowed; so is a return with no box left.
        Assert.Empty(Problems(_ => { }));
        Assert.Empty(Problems(template =>
        {
            var copy = JsonNode.Parse(Box(template).ToJsonString())!;
            copy["sortOrder"] = 30;
            Page(template, "contact")["blocks"]!.AsArray().Add(copy);
        }));
        Assert.Empty(Problems(RemoveBoxes));

        // Section 4.10 and section 7, "Added in v0.5": each refusal names its rule.
        var old = Problems(template => template["contractVersion"] = "0.4");
        Assert.Contains("needs contract version \"0.5\"", Assert.Single(old, p => p.Rule == "4.10" && p.Page == "home").Message);
        // Which version a package states is not its own choice (section 0): a return says what its hand-over said...
        Assert.Contains(old, p => p.Rule == "0" && p.Message == "This site was handed over as a version \"0.5\" package, and its return states the same version. This package says \"0.4\".");
        Assert.Equal("0", Assert.Single(Problems(template => { RemoveBoxes(template); template["contractVersion"] = "0.4"; })).Rule);
        Assert.Contains(Problems(template => Box(template)["settings"]!["hostSectionId"] = 99), p => p.Rule == "7" && p.Message == "Locked box 99 is not one this site's hand-over sent out.");
        Assert.Contains(Problems(template => Box(template)["settings"]!["hostSectionId"] = 0), p => p.Rule == "4.10" && p.Message.Contains("hostSectionId"));
        Assert.Contains(Problems(template => Box(template)["heading"] = "Mine now"), p => p.Rule == "4.10" && p.Message == "A HostSection section does not use heading. It must be empty.");
        Assert.Contains(Problems(template => Box(template)["layoutVariant"] = "cards"), p => p.Message.Contains("has no layoutVariant"));
        Assert.Contains(Problems(template => Box(template)["settings"] = new JsonObject()), p => p.Rule == "4.10" && p.Message.Contains("is missing \"hostSectionId\""));
        Assert.Contains(Problems(template => Box(template)["settings"]!["extra"] = 1), p => p.Rule == "4.10" && p.Message.Contains("a key the contract does not have"));
        var unknown = Assert.Single(Problems(template => template["contractVersion"] = "0.6"));
        Assert.Contains("This iPro takes versions \"0.4\" and \"0.5\" only.", unknown.Message);

        // ...and a site built from scratch says "0.4": it has no hand-over, so it can have no box either.
        var (scratch, scratchFiles) = BuilderImport571Tests.Designer();
        scratch["contractVersion"] = "0.5";
        var claimed = Assert.Single(BuilderPackageReader.Read(BuilderImport571Tests.Zip(scratch, scratchFiles), BuilderImportContext.DesignerTemplate()).Problems);
        Assert.Equal(("0", "A site built from scratch states contract version \"0.4\". \"0.5\" is the version of a site iPro handed over with locked boxes."), (claimed.Rule, claimed.Message));
        scratch["pages"]![0]!["blocks"]!.AsArray().Add(NewBlock("HostSection", 40, settings: new JsonObject { ["hostSectionId"] = 1 }));
        Assert.Contains(BuilderPackageReader.Read(BuilderImport571Tests.Zip(scratch, scratchFiles), BuilderImportContext.DesignerTemplate()).Problems,
            p => p.Rule == "4.10" && p.Message.Contains("exists only in a site iPro handed to the builder"));

        // A newer hand-over closes the door on this return: it is told so once, not once more for every box.
        var newer = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        await SiteHandoverService.RecordAsync(db, seeded.Site, newer, "owner");
        db.ChangeTracker.Clear();
        var stale = BuilderPackageReader.Read(Return(handover.Zip, _ => { }), await BuilderImportService.ContextAsync(db, seeded.Site, 0, handover.HandoverId)).Problems;
        Assert.Contains(stale, p => p.Message == $"The package belongs to hand-over {handover.HandoverId}. This site was handed over as {newer.HandoverId}.");
        Assert.DoesNotContain(stale, p => p.Message.Contains("Locked box") || p.Rule == "0");
    }

    [Fact]
    public async Task A_section_deleted_while_its_package_is_out_comes_back_with_its_box_and_its_pictures()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var audit = new RecordingAudit();
        var seeded = await SeedWithHostSectionsAsync(db, blob);
        var handover = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        await SiteHandoverService.RecordAsync(db, seeded.Site, handover, "owner");
        db.ChangeTracker.Clear();

        // The customer deletes the gallery while the package is out. Its picture is still named by the
        // hand-over's record, so a tidy-up of storage leaves it for the return.
        var gallery = await db.WebsiteContentBlocks.AsNoTracking().SingleAsync(b => b.Id == seeded.GalleryId);
        db.WebsiteContentBlocks.Remove(await db.WebsiteContentBlocks.SingleAsync(b => b.Id == seeded.GalleryId));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.True(await BlobReferences.IsReferencedAsync(db, seeded.GalleryPicture));
        Assert.Contains(("WebsiteBuilderHandovers", "HostSectionsJson"), BlobReferences.ContentColumns);

        // The return still holds its box: the gallery is written back as it was, picture and all, and
        // the review says the page was changed in iPro meanwhile.
        var importId = await UploadAsync(db, blob, audit, seeded.Site.AgentUserId, Return(handover.Zip, _ => { }));
        var review = await ReviewAsync(db, blob, audit, importId);
        Assert.True(review.Plan!.Pages.Single(p => p.Page.Slug == "about").ChangedSinceHandover);
        Assert.Contains(review.Plan.Pages.Single(p => p.Page.Slug == "about").BoxNotes, n => n.EndsWith("is not on the site any more as it was handed over: it comes back with its box, as it was then."));
        Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Apply(importId, null, null, plan: review.Plan.Fingerprint));
        db.ChangeTracker.Clear();
        var back = await db.WebsiteContentBlocks.AsNoTracking().SingleAsync(b => b.BlockType == WebsiteBlockTypes.Gallery && b.WebsitePage.AgentWebsiteId == seeded.Site.Id);
        Assert.Equal((gallery.Heading, gallery.Subheading, gallery.SettingsJson, gallery.LayoutVariant), (back.Heading, back.Subheading, back.SettingsJson, back.LayoutVariant));
        Assert.Contains(seeded.GalleryPicture, back.SettingsJson);

        // Deleted again; a newer hand-over then closes the door on the older one, which lets go of what it held.
        db.WebsiteContentBlocks.Remove(await db.WebsiteContentBlocks.SingleAsync(b => b.Id == back.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.True(await BlobReferences.IsReferencedAsync(db, seeded.GalleryPicture));

        var again = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        Assert.Equal(2, again.HostSections.Count);
        await SiteHandoverService.RecordAsync(db, seeded.Site, again, "owner");
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.WebsiteBuilderHandovers.CountAsync(h => h.HostSectionsJson != null));
        // The applied return's kept copy of the site (as it was before: no gallery) does not name it either.
        Assert.False(await BlobReferences.IsReferencedAsync(db, seeded.GalleryPicture));
    }

    [Fact]
    public async Task Erasing_a_customer_takes_the_gallery_photos_only_a_builder_record_names()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedWithHostSectionsAsync(db, blob);
        var handover = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        await SiteHandoverService.RecordAsync(db, seeded.Site, handover, "owner");
        db.WebsiteContentBlocks.Remove(await db.WebsiteContentBlocks.SingleAsync(b => b.Id == seeded.GalleryId));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var report = await AgentDataEraser.EraseAsync(db, seeded.Site.AgentUserId, eraseFinancialRecords: true);

        Assert.Contains(seeded.GalleryPicture, report.Blobs);
        Assert.Equal(0, await db.WebsiteBuilderHandovers.CountAsync());
    }

    [Fact]
    public async Task Erasing_a_customer_takes_the_gallery_photos_only_the_kept_copy_of_a_replaced_site_names()
    {
        // The same gap, older than locked boxes: a gallery on a page that a package built from scratch
        // replaced is named by nothing but the copy of the site kept for "put the previous site back".
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var audit = new RecordingAudit();
        var site = await BuilderImport571Tests.SeedSiteAsync(db);
        var homeId = await db.WebsitePages.Where(p => p.AgentWebsiteId == site.Id && p.IsHomePage).Select(p => p.Id).SingleAsync();
        var picture = await blob.UploadAsync(new MemoryStream(Png(6)), "window.png", "website-gallery", "image/png", false);
        db.Add(new WebsiteContentBlock { WebsitePageId = homeId, BlockType = WebsiteBlockTypes.Gallery, Heading = "The window", SortOrder = 5,
            SettingsJson = new WebsiteGallerySettings { Images = { new WebsiteGalleryImage { Url = picture } } }.ToJson() });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var importId = await UploadAsync(db, blob, audit, site.AgentUserId, BuilderImport571Tests.Zip(BuilderImport571Tests.Designer()));
        var review = await ReviewAsync(db, blob, audit, importId);
        Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Apply(importId, null, null, plan: review.Plan!.Fingerprint));
        db.ChangeTracker.Clear();
        Assert.False(await db.WebsiteContentBlocks.AnyAsync(b => b.BlockType == WebsiteBlockTypes.Gallery && b.WebsitePage.AgentWebsiteId == site.Id));
        Assert.Contains(picture, (await db.WebsiteBuilderImports.AsNoTracking().SingleAsync(i => i.Id == importId)).SnapshotJson);

        var report = await AgentDataEraser.EraseAsync(db, site.AgentUserId, eraseFinancialRecords: true);

        Assert.Contains(picture, report.Blobs);
    }

    [Fact]
    public async Task A_picture_with_an_accent_in_its_name_is_still_seen_as_used()
    {
        // Found by the security review: a gallery's settings are written by the default JSON writer,
        // which turns an accent into an escape, and the "is this file still used?" check looked for
        // the address as it is. The photo of a live gallery read as unused; inside a hand-over's
        // record, where the escape's backslash is doubled, it read as unused a second way.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedFittingSiteAsync(db, blob);
        var name = "Montr" + (char)0xE9 + "al.png";
        var picture = await blob.UploadAsync(new MemoryStream(Png(9)), name, "website-gallery", "image/png", false);
        var aboutId = await db.WebsitePages.Where(p => p.AgentWebsiteId == seeded.Site.Id && p.Slug == "about").Select(p => p.Id).SingleAsync();
        var settings = new WebsiteGallerySettings { Images = { new WebsiteGalleryImage { Url = picture } } }.ToJson();
        Assert.DoesNotContain(picture, settings);
        Assert.Contains("\\u00E9", settings);
        var gallery = new WebsiteContentBlock { WebsitePageId = aboutId, BlockType = WebsiteBlockTypes.Gallery, Heading = "Trip", SortOrder = 1, SettingsJson = settings };
        db.Add(gallery);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(3, BlobReferences.Spellings(picture).Count);
        Assert.Single(BlobReferences.Spellings("https://blob.example.test/website-gallery/plain.png"));
        Assert.True(await BlobReferences.IsReferencedAsync(db, picture), "the photo of a live gallery");

        var handover = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        await SiteHandoverService.RecordAsync(db, seeded.Site, handover, "owner");
        db.WebsiteContentBlocks.Remove(await db.WebsiteContentBlocks.SingleAsync(b => b.Id == gallery.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.True(await BlobReferences.IsReferencedAsync(db, picture), "the photo of a gallery only the hand-over's record names");
        Assert.False(await BlobReferences.IsReferencedAsync(db, picture.Replace("al.png", "al-other.png")));
    }

    [Fact]
    public void Every_column_of_a_section_makes_the_trip()
    {
        // A section is written back "exactly as it was" from a record that lists its columns by hand.
        // A column added to WebsiteContentBlock later must be added there too, or it would be dropped
        // without a word: this fails until it is.
        // (The write itself, BuilderImportService.ApplyAsync, copies the columns by hand too: the round
        // trip above fills every text column of one section by this same list and compares them all.)
        Assert.All(Columns, p => Assert.True(p.PropertyType == typeof(string) || p.Name == nameof(WebsiteContentBlock.IsVisible),
            $"{p.Name} is a {p.PropertyType.Name}: teach this test and HandoverHostSection what to do with it"));

        WebsiteContentBlock Filled()
        {
            var block = new WebsiteContentBlock { Id = 7, WebsitePageId = 3, SortOrder = 4, IsVisible = true };
            foreach (var p in Columns.Where(p => p.PropertyType == typeof(string))) p.SetValue(block, $"the {p.Name}");
            return block;
        }

        var original = Filled();
        var record = HandoverHostSection.From(original, 5, "about");
        Assert.Equal(7, record.BlockId);
        var written = record.ToBlock(sortOrder: 9, isVisible: false);
        var copied = HandoverHostSection.Copy(original, 9);
        foreach (var p in Columns.Where(p => p.PropertyType == typeof(string)))
        {
            Assert.Equal(p.GetValue(original), p.GetValue(written));
            Assert.Equal(p.GetValue(original), p.GetValue(copied));
            // And each one is part of what a section "says".
            var changed = Filled();
            p.SetValue(changed, "something else");
            Assert.False(record.Says(changed), $"a change of {p.Name} went unseen");
            Assert.NotEqual(SiteFingerprint.OfSection(original), SiteFingerprint.OfSection(changed));
        }
        Assert.True(record.Says(Filled()));
        Assert.Equal((9, false, true), (written.SortOrder, written.IsVisible, copied.IsVisible));   // the box's place and visibility; a copy keeps its own
        var hidden = Filled();
        hidden.IsVisible = false;
        hidden.Id = 99;
        Assert.True(record.Says(hidden));   // hidden or shown, on whatever row: it says the same
        Assert.NotEqual(SiteFingerprint.OfSection(original), SiteFingerprint.OfSection(hidden));   // but it is not the same thing to write
    }

    // Everything of a section that is its own: not which row or page it is, where it stands or when it was saved.
    private static readonly List<PropertyInfo> Columns = typeof(WebsiteContentBlock).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.Name is not (nameof(WebsiteContentBlock.Id) or nameof(WebsiteContentBlock.WebsitePageId) or nameof(WebsiteContentBlock.WebsitePage)
            or nameof(WebsiteContentBlock.SortOrder) or nameof(WebsiteContentBlock.CreatedAt) or nameof(WebsiteContentBlock.UpdatedAt)))
        .OrderBy(p => p.Name, StringComparer.Ordinal).ToList();

    [Fact]
    public void The_real_packages_of_a_first_trip_with_locked_boxes_still_agree()
    {
        // Kept from 2026-10-13: a hand-over made by iPro with three locked boxes, opened in the
        // builder's own editor, where the calculator's box was moved below the blog, the gallery's box
        // hidden and the map's box removed, then exported by the builder's own code. If either side
        // drifts, this says so.
        var handoverZip = BuilderImport571Tests.Fixture("roundtrip-v05-ipro-handover.zip");
        var sent = Entries(handoverZip);
        var said = JsonNode.Parse(Encoding.UTF8.GetString(sent["handover.json"]))!;
        Assert.Equal("1.1", said["handoverVersion"]!.GetValue<string>());
        Assert.Equal(new[] { "Calculator", "Photo Gallery", "Map" }, said["catalog"]!["hostSections"]!.AsArray().Select(h => h!["label"]!.GetValue<string>()));
        var delivered = sent.Where(f => f.Key.StartsWith("assets/"))
            .Select(f => BuilderPackageReader.Hex(System.Security.Cryptography.SHA256.HashData(f.Value))).ToHashSet();
        var context = new BuilderImportContext(
            said["handoverId"]!.GetValue<string>(),
            said["allowedBlockTypes"]!.AsArray().Select(t => t!.GetValue<string>()).ToHashSet(),
            said["catalog"]!["articles"]!.AsArray().Select(a => a!["id"]!.GetValue<int>()).ToHashSet(),
            said["catalog"]!["forms"]!.AsArray().Select(f => f!["id"]!.GetValue<int>()).ToHashSet(),
            delivered, RemainingBytes: 0,
            said["catalog"]!["hostSections"]!.AsArray().Select(h => h!["id"]!.GetValue<int>()).ToHashSet());

        var returned = BuilderImport571Tests.Fixture("roundtrip-v05-builder-return.zip");
        Assert.Equal(context.HandoverId, BuilderPackageReader.PeekHandoverId(returned));
        var back = BuilderPackageReader.Read(returned, context);

        Assert.True(back.IsValid, string.Join("\n", back.Problems.Select(p => $"[{p.Rule}] {p.Where}: {p.Message}")));
        var home = back.Template!.Pages.Single(p => p.IsHomePage).Blocks.OrderBy(b => b.SortOrder).ToList();
        Assert.Equal(new[] { "Hero", "Blog", "HostSection" }, home.Select(b => b.Type));
        Assert.Equal((1, true), (home[2].HostSectionId, home[2].IsVisible));
        var gallery = back.Template.Pages.Single(p => p.Slug == "about").Blocks.Single(b => b.Type == BuilderContract.HostSection);
        Assert.Equal((2, false), (gallery.HostSectionId, gallery.IsVisible));
        Assert.DoesNotContain(back.Template.Pages.SelectMany(p => p.Blocks), b => b.HostSectionId == 3);   // the map's box was removed
        Assert.Empty(back.NewImages(delivered));   // both pictures are recognized by content
        // Without the boxes this hand-over sent out, the same return is refused.
        Assert.Contains(BuilderPackageReader.Read(returned, context with { HostSectionIds = new HashSet<int>() }).Problems,
            p => p.Message == "Locked box 1 is not one this site's hand-over sent out.");
    }

    // ---- which section of the site a box is (SiteImportPlan.Build on its own, no database) ----
    //
    // The second re-check found that no test above would fail if the heart of the rework were taken
    // out: every case was also found some other way. Each of these is found ONE way only.

    [Fact]
    public void A_box_is_the_section_on_the_row_the_hand_over_recorded_however_it_reads_now()
    {
        // Since the hand-over the customer renamed and re-set the calculator, and added a second one above it.
        var was = Section(10, WebsiteBlockTypes.Calculator, "Mortgage", 2, "{\"Rate\":4}");
        var hosts = new[] { HandoverHostSection.From(was, 1, "home") };
        var site = new[]
        {
            SitePage(1, "home", true, Section(5, WebsiteBlockTypes.Hero, "Welcome", 0), Section(11, WebsiteBlockTypes.Calculator, "Savings", 1), Section(10, WebsiteBlockTypes.Calculator, "Mortgage 2026", 2, "{\"Rate\":5}")),
            SitePage(2, "contact", false, Section(6, WebsiteBlockTypes.Text, "Write to us", 0))
        };

        // The box stayed on Home: it is the renamed one, not the newer one above it, which is named.
        var stayed = Plan(site, hosts, PackagePage("home", true, 0, Words(0, "Welcome"), Box(1, 1)), PackagePage("contact", false, 1, Words(0, "Write to us")));
        var home = stayed.Pages.Single(p => p.Page.IsHomePage);
        Assert.Equal(("Mortgage 2026", "{\"Rate\":5}"), (home.Page.Blocks.Last().Heading, home.Page.Blocks.Last().SettingsJson));
        Assert.Equal(new[] { 11 }, home.SectionsGoing.Select(g => g.BlockId));
        Assert.Empty(home.BoxNotes);

        // The box was moved to Contact: still that section, as it reads now, and said to come from Home.
        var moved = Plan(site, hosts, PackagePage("home", true, 0, Words(0, "Welcome")), PackagePage("contact", false, 1, Words(0, "Write to us"), Box(1, 1)));
        var contact = moved.Pages.Single(p => p.Page.Slug == "contact");
        Assert.Equal(("Mortgage 2026", "{\"Rate\":5}"), (contact.Page.Blocks.Last().Heading, contact.Page.Blocks.Last().SettingsJson));
        Assert.Equal(new[] { "Calculator \"Mortgage 2026\" comes from another page (/home)." }, contact.BoxNotes);
        Assert.Equal(new[] { 11 }, moved.Pages.Single(p => p.Page.IsHomePage).SectionsGoing.Select(g => g.BlockId));
    }

    [Fact]
    public void Once_rows_are_new_a_changed_section_is_known_by_its_kind_and_heading()
    {
        // After a put-back or an earlier return every section has a new row. The customer changed the
        // calculator's settings and added a second calculator above it.
        var hosts = new[] { HandoverHostSection.From(Section(10, WebsiteBlockTypes.Calculator, "Mortgage", 1, "{\"Rate\":4}"), 1, "home") };
        var site = new[]
        {
            SitePage(1, "home", true, Section(111, WebsiteBlockTypes.Calculator, "Savings", 0), Section(110, WebsiteBlockTypes.Calculator, "Mortgage", 1, "{\"Rate\":5}")),
            SitePage(2, "contact", false, Section(6, WebsiteBlockTypes.Text, "Write to us", 0))
        };

        var stayed = Plan(site, hosts, PackagePage("home", true, 0, Box(1, 0)), PackagePage("contact", false, 1, Words(0, "Write to us")));
        var home = stayed.Pages.Single(p => p.Page.IsHomePage);
        Assert.Equal(("Mortgage", "{\"Rate\":5}"), (home.Page.Blocks.Single().Heading, home.Page.Blocks.Single().SettingsJson));   // the customer's rate, not the old one
        Assert.Equal(new[] { 111 }, home.SectionsGoing.Select(g => g.BlockId));
        Assert.Empty(home.BoxNotes);

        // The same, with the box on another page: found there too, by its heading.
        var moved = Plan(site, hosts, PackagePage("home", true, 0, Words(0, "Welcome")), PackagePage("contact", false, 1, Box(1, 0)));
        Assert.Equal("{\"Rate\":5}", moved.Pages.Single(p => p.Page.Slug == "contact").Page.Blocks.Single().SettingsJson);
        Assert.Equal(new[] { 111 }, moved.Pages.Single(p => p.Page.IsHomePage).SectionsGoing.Select(g => g.BlockId));

        // And when its box was removed, it is "removed in the builder", not "not in the package".
        var removed = Plan(site, hosts, PackagePage("home", true, 0, Words(0, "Welcome")), PackagePage("contact", false, 1, Words(0, "Write to us")));
        Assert.Equal(new[] { "111|False", "110|True" }, removed.Pages.Single(p => p.Page.IsHomePage).SectionsGoing.Select(g => $"{g.BlockId}|{g.RemovedInBuilder}"));
    }

    [Fact]
    public void By_its_kind_alone_only_as_a_last_resort_and_then_the_review_says_so()
    {
        // Rows are new and the section reads differently, heading included: nothing but its kind and
        // its page says which one it is. The box takes it (so the customer's section stays, and the old
        // one is not brought back beside it), and the review says that it did.
        var hosts = new[] { HandoverHostSection.From(Section(10, WebsiteBlockTypes.Calculator, "Mortgage", 1), 1, "home") };
        var site = new[]
        {
            SitePage(1, "home", true, Section(110, WebsiteBlockTypes.Calculator, "Retirement", 0)),
            SitePage(2, "contact", false, Section(120, WebsiteBlockTypes.Calculator, "Savings", 0))
        };
        var one = Plan(site, hosts, PackagePage("home", true, 0, Box(1, 0)), PackagePage("contact", false, 1, Words(0, "Write to us")));
        var home = one.Pages.Single(p => p.Page.IsHomePage);
        Assert.Equal("Retirement", home.Page.Blocks.Single().Heading);
        Assert.Equal(new[] { "Calculator \"Retirement\" is taken for the section this box stood for (Calculator \"Mortgage\"): that one is not on the site any more as it was handed over, and this is the one of its kind on this page." }, home.BoxNotes);
        Assert.Equal(new[] { 120 }, one.Pages.Single(p => p.Page.Slug == "contact").SectionsGoing.Select(g => g.BlockId));

        // A second box of the same section is a copy of it, never somebody else's section of that kind:
        // the customer's own calculator on Contact is named as going, not quietly taken for the copy.
        var hosts2 = new[] { HandoverHostSection.From(Section(110, WebsiteBlockTypes.Calculator, "Retirement", 0), 1, "home") };
        var two = Plan(site, hosts2, PackagePage("home", true, 0, Box(1, 0)), PackagePage("contact", false, 1, Box(1, 0)));
        var contact = two.Pages.Single(p => p.Page.Slug == "contact");
        Assert.Equal("Retirement", contact.Page.Blocks.Single().Heading);
        Assert.Equal(new[] { "Calculator \"Retirement\" comes back as a copy: the package has it 2 times." }, contact.BoxNotes);
        Assert.Equal(new[] { 120 }, contact.SectionsGoing.Select(g => g.BlockId));
    }

    [Fact]
    public void A_box_on_its_own_page_comes_first_and_a_copy_reads_as_the_section_does_now()
    {
        // The gallery stands on About and was renamed since. Its box is in the package twice: on Home,
        // which comes first, and on About.
        var hosts = new[] { HandoverHostSection.From(Section(20, WebsiteBlockTypes.Gallery, "Our shop", 0), 1, "about") };
        var site = new[]
        {
            SitePage(1, "home", true, Section(5, WebsiteBlockTypes.Hero, "Welcome", 0)),
            SitePage(2, "about", false, Section(20, WebsiteBlockTypes.Gallery, "Our shop today", 0))
        };
        var plan = Plan(site, hosts, PackagePage("home", true, 0, Words(0, "Welcome"), Box(1, 1)), PackagePage("about", false, 1, Box(1, 0)));
        var home = plan.Pages.Single(p => p.Page.IsHomePage);
        var about = plan.Pages.Single(p => p.Page.Slug == "about");
        Assert.Empty(about.BoxNotes);                                                    // on its own page it is simply the section
        Assert.Equal(new[] { "Photo Gallery \"Our shop today\" comes back as a copy: the package has it 2 times." }, home.BoxNotes);
        Assert.Equal("Our shop today", home.Page.Blocks.Last().Heading);                 // the copy says what the section says now
        Assert.Equal("Our shop today", about.Page.Blocks.Single().Heading);
        Assert.All(plan.Pages, p => Assert.Empty(p.SectionsGoing));
    }

    [Fact]
    public void A_page_that_is_not_replaced_says_which_of_its_sections_come_back_elsewhere()
    {
        // The designer gave About another address: the package has "about-us" and no "about".
        var gallery = Section(20, WebsiteBlockTypes.Gallery, "Our shop", 0);
        var map = Section(21, WebsiteBlockTypes.Maps, "Find us", 1);
        var hosts = new[] { HandoverHostSection.From(gallery, 1, "about"), HandoverHostSection.From(map, 2, "about") };
        var site = new[] { SitePage(1, "home", true, Section(5, WebsiteBlockTypes.Hero, "Welcome", 0)), SitePage(2, "about", false, gallery, map) };
        var pages = new[] { PackagePage("home", true, 0, Words(0, "Welcome")), PackagePage("about-us", false, 1, Words(0, "About us"), Box(1, 1)) };   // the map's box was removed

        var plan = Plan(site, hosts, pages);
        var old = plan.Pages.Single(p => p.Page.Id == 2);
        Assert.Equal((PageChange.Removed, true), (old.Change, old.RemovedInBuilder));
        Assert.Equal(new[] { "Photo Gallery \"Our shop\" comes back on another page, in its box." }, old.BoxNotes);   // the map is not said to: it goes with the page
        Assert.Equal(new[] { "Photo Gallery \"Our shop\" comes from another page (/about)." }, plan.Pages.Single(p => p.Page.Slug == "about-us").BoxNotes);

        // Kept after all, the old page still has its gallery, and the new page has it too.
        var kept = Plan(site, hosts, pages, keepPages: new HashSet<int> { 2 });
        var stays = kept.Pages.Single(p => p.Page.Id == 2);
        Assert.Equal(PageChange.Kept, stays.Change);
        Assert.Equal(new[] { "Photo Gallery \"Our shop\" also comes back on another page, in its box: it will stand on both." }, stays.BoxNotes);
        Assert.NotEqual(plan.Fingerprint, kept.Fingerprint);
    }

    [Fact]
    public void Hidden_or_shown_as_the_site_has_it_unless_the_designer_changed_it_and_then_it_is_said()
    {
        var hosts = new[] { HandoverHostSection.From(Section(10, WebsiteBlockTypes.Calculator, "Mortgage", 0), 1, "home") };   // shown at the hand-over
        (bool Shown, IReadOnlyList<string> Notes) Back(bool siteShows, bool boxShows)
        {
            var site = new[] { SitePage(1, "home", true, Section(10, WebsiteBlockTypes.Calculator, "Mortgage", 0, visible: siteShows)) };
            var home = Plan(site, hosts, PackagePage("home", true, 0, Box(1, 0, boxShows))).Pages.Single();
            return (home.Page.Blocks.Single().IsVisible, home.BoxNotes);
        }

        var untouched = Back(siteShows: true, boxShows: true);
        Assert.True(untouched.Shown);
        Assert.Empty(untouched.Notes);
        // The customer hid it since and the designer did not touch it: it stays hidden, and nothing needs saying.
        var hiddenSince = Back(siteShows: false, boxShows: true);
        Assert.False(hiddenSince.Shown);
        Assert.Empty(hiddenSince.Notes);
        // The designer hid it: hidden, and said when visitors see it today.
        var hid = Back(siteShows: true, boxShows: false);
        Assert.Equal((false, "Calculator \"Mortgage\" is shown on the site and comes back HIDDEN."), (hid.Shown, hid.Notes.Single()));
        var hiddenByBoth = Back(siteShows: false, boxShows: false);
        Assert.False(hiddenByBoth.Shown);
        Assert.Empty(hiddenByBoth.Notes);

        // Hidden at the hand-over, shown by the designer.
        var hiddenThen = new[] { HandoverHostSection.From(Section(10, WebsiteBlockTypes.Calculator, "Mortgage", 0, visible: false), 1, "home") };
        var siteHidden = new[] { SitePage(1, "home", true, Section(10, WebsiteBlockTypes.Calculator, "Mortgage", 0, visible: false)) };
        var shown = Plan(siteHidden, hiddenThen, PackagePage("home", true, 0, Box(1, 0, true))).Pages.Single();
        Assert.Equal((true, "Calculator \"Mortgage\" is HIDDEN on the site and comes back shown."), (shown.Page.Blocks.Single().IsVisible, shown.BoxNotes.Single()));
        // The same when the site no longer has it: one sentence, and not "is hidden on the site".
        var gone = Plan(new[] { SitePage(1, "home", true, Section(5, WebsiteBlockTypes.Hero, "Welcome", 0)) }, hiddenThen, PackagePage("home", true, 0, Box(1, 0, true))).Pages.Single();
        Assert.Equal("Calculator \"Mortgage\" is not on the site any more as it was handed over: it comes back with its box, as it was then, but SHOWN though it was hidden.", gone.BoxNotes.Single());
    }

    private static WebsiteContentBlock Section(int id, string type, string heading, int sort, string settings = "{}", bool visible = true) =>
        new() { Id = id, BlockType = type, Heading = heading, SortOrder = sort, SettingsJson = settings, IsVisible = visible };

    private static WebsitePage SitePage(int id, string slug, bool home, params WebsiteContentBlock[] sections)
    {
        // A fresh copy of each section: a test may build more than one site from the same ones.
        var page = new WebsitePage { Id = id, AgentWebsiteId = 9, Title = slug, Slug = slug, NavigationLabel = slug, IsHomePage = home, SortOrder = id };
        page.Blocks = sections.Select(s => { var copy = HandoverHostSection.Copy(s, s.SortOrder); copy.Id = s.Id; copy.WebsitePageId = id; return copy; }).ToList();
        return page;
    }

    private static BuilderBlock Box(int number, int sort, bool visible = true) =>
        new(BuilderContract.HostSection, "", "", "", "", "", "", "", sort, visible, "{}", 0, 0, number);

    private static BuilderBlock Words(int sort, string heading) => new(WebsiteBlockTypes.Text, heading, "", "Some words.", "", "", "", "", sort, true, "{}", 0, 0);

    private static BuilderPage PackagePage(string slug, bool home, int sort, params BuilderBlock[] blocks) => new(slug, null, slug, slug, home, sort, blocks);

    // The plan for a return of a hand-over that carried every page of the site.
    private static SiteImportPlan Plan(IReadOnlyList<WebsitePage> site, IReadOnlyList<HandoverHostSection> hosts, params BuilderPage[] pages) => Plan(site, hosts, pages, null);

    private static SiteImportPlan Plan(IReadOnlyList<WebsitePage> site, IReadOnlyList<HandoverHostSection> hosts, BuilderPage[] pages, IReadOnlySet<int>? keepPages) =>
        SiteImportPlan.Build(
            new BuilderTemplate("sample", "modern", "h_0123456789abcdef", string.Empty, string.Empty, pages) { ContractVersion = BuilderContract.VersionWithHostSections },
            new Dictionary<string, string>(), new AgentWebsite { Id = 9 }, site, new HashSet<int>(),
            handedOverPageIds: site.Select(p => p.Id).ToHashSet(), keepPageIds: keepPages,
            handedOverRowBySlug: site.ToDictionary(p => p.Slug, p => p.Id, StringComparer.OrdinalIgnoreCase),
            carriedTypes: BuilderContract.BlockTypes.ToHashSet(StringComparer.Ordinal), hostSections: hosts);

    // ---------------- helpers ----------------

    private sealed record SeededWithHosts(AgentWebsite Site, int HomeId, int AboutId, int CalculatorId, int GalleryId, int MapId, string GalleryPicture);

    // The fitting site of 572 plus three sections the builder does not carry, each with every field
    // filled: a calculator on Home, between the hero and the blog; on About a gallery with a photo,
    // and a hidden map.
    private static async Task<SeededWithHosts> SeedWithHostSectionsAsync(IPRODbContext db, BuilderImport571Tests.MemoryBlob blob)
    {
        var seeded = await SeedFittingSiteAsync(db, blob);
        var picture = await blob.UploadAsync(new MemoryStream(Png(7)), "inside.png", "website-gallery", "image/png", false);
        var chart = await blob.UploadAsync(new MemoryStream(Png(8)), "rates-chart.png", "website-media", "image/png", false);
        var aboutId = await db.WebsitePages.Where(p => p.AgentWebsiteId == seeded.Site.Id && p.Slug == "about").Select(p => p.Id).SingleAsync();
        var blog = await db.WebsiteContentBlocks.SingleAsync(b => b.WebsitePageId == seeded.HomeId && b.BlockType == WebsiteBlockTypes.Blog);
        blog.SortOrder = 2;
        var calculator = new WebsiteContentBlock
        {
            WebsitePageId = seeded.HomeId, BlockType = WebsiteBlockTypes.Calculator, Heading = "Mortgage payment", Subheading = "What would it cost?", Body = "Rates change weekly.",
            ImageUrl = chart, ButtonText = "Ask us", ButtonUrl = "/contact", SortOrder = 1, SettingsJson = "{\"Type\":\"zq-only-in-ipro\"}"
        };
        var gallery = new WebsiteContentBlock
        {
            WebsitePageId = aboutId, BlockType = WebsiteBlockTypes.Gallery, Heading = "Our shop", Subheading = "Inside and out", LayoutVariant = "masonry", SortOrder = 1,
            SettingsJson = new WebsiteGallerySettings { Images = { new WebsiteGalleryImage { Url = picture } } }.ToJson()
        };
        var map = new WebsiteContentBlock { WebsitePageId = aboutId, BlockType = WebsiteBlockTypes.Maps, Heading = "Find us", Body = "12 Main Street", SortOrder = 2, IsVisible = false };
        // Every text column of the calculator says something, the ones named above and any added to a
        // section later: the round trip compares them all.
        foreach (var column in Columns.Where(p => p.PropertyType == typeof(string) && string.IsNullOrEmpty((string?)p.GetValue(calculator)))) column.SetValue(calculator, $"the {column.Name}");
        db.AddRange(calculator, gallery, map);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new SeededWithHosts(seeded.Site, seeded.HomeId, aboutId, calculator.Id, gallery.Id, map.Id, picture);
    }

    private static async Task<int> UploadAsync(IPRODbContext db, BuilderImport571Tests.MemoryBlob blob, RecordingAudit audit, int agentId, byte[] package)
    {
        var uploaded = Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Upload(agentId, Upload(package, "return.zip")));
        db.ChangeTracker.Clear();
        return (int)uploaded.RouteValues!["id"]!;
    }

    private static async Task<BuilderImportReviewViewModel> ReviewAsync(IPRODbContext db, BuilderImport571Tests.MemoryBlob blob, RecordingAudit audit, int importId, string? sections = null)
    {
        var review = (BuilderImportReviewViewModel)((ViewResult)await Controller(db, blob, audit).Review(importId, null, null, null, sections)).Model!;
        db.ChangeTracker.Clear();
        return review;
    }

    // Every section of the site that the builder does not carry, in page and section order:
    // "page|kind|" and then EVERY column of its own (Columns, by name), so that a column added to a
    // section later is compared too.
    private static async Task<List<string>> HostSectionsOnSiteAsync(IPRODbContext db, int siteId) =>
        (await db.WebsiteContentBlocks.AsNoTracking()
            .Where(b => b.WebsitePage.AgentWebsiteId == siteId)
            .OrderBy(b => b.WebsitePage.Slug).ThenBy(b => b.SortOrder)
            .Select(b => new { b.WebsitePage.Slug, Block = b })
            .ToListAsync())
        .Where(x => !BuilderContract.BlockTypes.Contains(x.Block.BlockType))
        .Select(x => $"{x.Slug}|{x.Block.BlockType}|" + string.Join("|", Columns.Select(p => $"{p.Name}={p.GetValue(x.Block)}")))
        .ToList();

    private static int BoxId(JsonNode? block) =>
        block!["type"]!.GetValue<string>() == "HostSection" ? block["settings"]!["hostSectionId"]!.GetValue<int>() : 0;

    private static IEnumerable<string> Types(JsonObject template, string slug) =>
        Page(template, slug)["blocks"]!.AsArray().Select(b => b!["type"]!.GetValue<string>());

    private static Dictionary<string, byte[]> Entries(byte[] zip)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            files[entry.FullName] = copy.ToArray();
        }
        return files;
    }
}
