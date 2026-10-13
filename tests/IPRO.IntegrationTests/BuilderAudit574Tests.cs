using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using IPRO.Admin.Models;
using IPRO.Business.Services;
using IPRO.Business.SiteImport;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Utility;
using IPRO.Web.Controllers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using H = IPRO.IntegrationTests.SiteHandoverReturn572Tests;
using MemoryBlob = IPRO.IntegrationTests.BuilderImport571Tests.MemoryBlob;

namespace IPRO.IntegrationTests;

// 574 (2026-10-13). Three independent audits read 571 and 572 before anything of them could go to
// production: security, functionality, code quality. Each test here is one of their findings, checked
// against the code first and then fixed.
public class BuilderAudit574Tests
{
    // ------------------------------------------------ what changed in iPro after a hand-over

    [Fact]
    public async Task What_the_customer_changed_after_the_hand_over_is_said_before_a_return_replaces_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new MemoryBlob();
        var site = (await H.SeedFittingSiteAsync(db, blob)).Site;
        var handover = await HandOverAsync(db, blob, site);

        // In iPro, after the hand-over: a section's words change, a section is added to another page,
        // and the logo is replaced. The page editor stamps the section it saves and never the page, and
        // an added section stamps nothing: a page's own time could not see any of this.
        var times = await db.WebsitePages.AsNoTracking().Where(p => p.AgentWebsiteId == site.Id).ToDictionaryAsync(p => p.Slug, p => p.UpdatedAt);
        var aboutBlock = await db.WebsiteContentBlocks.SingleAsync(b => b.WebsitePage.AgentWebsiteId == site.Id && b.WebsitePage.Slug == "about");
        aboutBlock.Body = "Baking since 1998, and now on Sundays.";
        var contact = await db.WebsitePages.SingleAsync(p => p.AgentWebsiteId == site.Id && p.Slug == "contact");
        db.Add(new WebsiteContentBlock { WebsitePageId = contact.Id, BlockType = WebsiteBlockTypes.Text, Heading = "Parking", SortOrder = 5 });
        var newLogo = await blob.UploadAsync(new MemoryStream(H.Png(7)), "new-logo.png", "agent-logos", "image/png", false);
        (await db.AgentWebsites.SingleAsync(w => w.Id == site.Id)).LogoUrl = newLogo;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(times, await db.WebsitePages.AsNoTracking().Where(p => p.AgentWebsiteId == site.Id).ToDictionaryAsync(p => p.Slug, p => p.UpdatedAt));

        // The builder removed "about" and touched nothing else.
        var import = await StoreAsync(db, blob, site, H.Return(handover.Zip, template =>
        {
            var pages = template["pages"]!.AsArray();
            pages.Remove(pages.Single(p => p!["slug"]!.GetValue<string>() == "about"));
        }));
        var plan = (await BuilderImportService.PlanAsync(db, import))!.Plan!;
        PlannedPage Of(string slug) => plan.Pages.Single(p => p.Page.Slug == slug);

        Assert.False(Of("home").ChangedSinceHandover);
        Assert.Equal(PageChange.Replaced, Of("contact").Change);
        Assert.True(Of("contact").ChangedSinceHandover);
        Assert.True(Of("about").RemovedInBuilder);
        Assert.True(Of("about").ChangedSinceHandover);
        Assert.Equal(LogoChange.Replaced, plan.Logo);
        Assert.True(plan.LogoChangedSinceHandover);

        // And the screen says each of them in red, and carries the plan it shows back with its button.
        var view = (ViewResult)await H.Controller(db, blob, new H.RecordingAudit()).Review(import.Id, null, null);
        var html = await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Review.cshtml", (BuilderImportReviewViewModel)view.Model!);
        Assert.Equal(2, Count(html, "Changed in iPro after the hand-over."));
        Assert.Contains("What the customer changed on this page since then is replaced", html);
        Assert.Contains("removing it loses that work", html);
        Assert.Contains("The logo was changed in iPro after the hand-over.", html);
        Assert.Contains("invoices and documents print it", html);
        Assert.Contains($"name=\"plan\" value=\"{plan.Fingerprint}\"", html);
    }

    [Fact]
    public async Task A_page_put_back_or_renamed_since_the_hand_over_is_still_the_page_that_went_to_the_builder()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new MemoryBlob();
        var site = (await H.SeedFittingSiteAsync(db, blob)).Site;
        var handover = await HandOverAsync(db, blob, site);
        byte[] WithoutAbout() => H.Return(handover.Zip, template =>
        {
            var pages = template["pages"]!.AsArray();
            pages.Remove(pages.Single(p => p!["slug"]!.GetValue<string>() == "about"));
        });

        // A return goes in and is put back: "about" is on the site again, on a new row.
        var aboutBefore = (await db.WebsitePages.AsNoTracking().SingleAsync(p => p.AgentWebsiteId == site.Id && p.Slug == "about")).Id;
        var first = await StoreAsync(db, blob, site, WithoutAbout());
        Assert.True((await BuilderImportService.ApplyAsync(db, first, new HashSet<int>(), null, "owner")).Ok);
        db.ChangeTracker.Clear();
        Assert.True((await BuilderImportService.RestoreAsync(db, await db.WebsiteBuilderImports.SingleAsync(i => i.Id == first.Id), blob)).Ok);
        db.ChangeTracker.Clear();
        Assert.NotEqual(aboutBefore, (await db.WebsitePages.AsNoTracking().SingleAsync(p => p.AgentWebsiteId == site.Id && p.Slug == "about")).Id);

        // The corrected return of the same hand-over: "about" is still the page that went and was
        // removed there (not "a page made in iPro since", kept by default), and nothing reads as
        // changed, because what the site says is what was handed over.
        var again = await StoreAsync(db, blob, site, WithoutAbout());
        var plan = (await BuilderImportService.PlanAsync(db, again))!.Plan!;
        var about = plan.Pages.Single(p => p.Page.Slug == "about");
        Assert.Equal(PageChange.Removed, about.Change);
        Assert.True(about.RemovedInBuilder);
        Assert.DoesNotContain(plan.Pages, p => p.ChangedSinceHandover);
        Assert.False(plan.LogoChangedSinceHandover);

        // A page given another address in iPro is replaced on its own row; before, the package's page
        // was added beside it and the renamed row deleted with its search settings.
        var contact = await db.WebsitePages.SingleAsync(p => p.AgentWebsiteId == site.Id && p.Slug == "contact");
        var contactId = contact.Id;
        contact.Slug = "contact-us";
        contact.MetaTitle = "Reach the bakery";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var third = await StoreAsync(db, blob, site, H.Return(handover.Zip, _ => { }));
        var renamed = (await BuilderImportService.PlanAsync(db, third))!.Plan!;
        var item = renamed.Pages.Single(p => p.Page.Slug == "contact");
        Assert.Equal(PageChange.Replaced, item.Change);
        Assert.Equal(contactId, item.Page.Id);
        Assert.Equal("Reach the bakery", item.Page.MetaTitle);
        Assert.True(item.ChangedSinceHandover);
        Assert.Contains("contact-us", item.Page.OldAddresses);
        Assert.DoesNotContain(renamed.Pages, p => p.Change == PageChange.Added || p.Page.Slug == "contact-us");
    }

    // ------------------------------------------------ what is written is what was read

    [Fact]
    public async Task Bring_it_in_refuses_when_the_site_changed_after_the_review_and_keeps_its_copy_whoever_calls()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new MemoryBlob();
        var site = (await H.SeedFittingSiteAsync(db, blob)).Site;
        var handover = await HandOverAsync(db, blob, site);
        var import = await StoreAsync(db, blob, site, H.Return(handover.Zip, template => H.Block(template, "home", 0)["heading"] = "Edited in the builder"));
        var audit = new H.RecordingAudit();
        var reviewed = await BuilderImportScreens571Tests.ReviewedAsync(db, import.Id);
        db.ChangeTracker.Clear();

        // After the review was opened the customer adds a map to a page the package replaces. The
        // screen that was read said nothing would be lost there.
        var about = await db.WebsitePages.SingleAsync(p => p.AgentWebsiteId == site.Id && p.Slug == "about");
        db.Add(new WebsiteContentBlock { WebsitePageId = about.Id, BlockType = WebsiteBlockTypes.Maps, Heading = "Find us", SortOrder = 4 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var refused = Assert.IsType<RedirectToActionResult>(await H.Controller(db, blob, audit).Apply(import.Id, null, null, null, plan: reviewed));
        Assert.Equal("Review", refused.ActionName);
        db.ChangeTracker.Clear();
        Assert.Equal(WebsiteBuilderImportStatus.Pending, (await db.WebsiteBuilderImports.AsNoTracking().SingleAsync(i => i.Id == import.Id)).Status);
        Assert.Equal("Fresh every morning", (await db.WebsiteContentBlocks.AsNoTracking().FirstAsync(b => b.WebsitePage.AgentWebsiteId == site.Id && b.BlockType == WebsiteBlockTypes.Hero)).Heading);
        Assert.Contains(audit.Entries, e => e.Action == "BuilderImportNotApplied" && e.Details.Contains("has changed since this review"));

        // A request that carries no plan at all is refused the same way.
        Assert.Equal("Review", Assert.IsType<RedirectToActionResult>(await H.Controller(db, blob, audit).Apply(import.Id, null, null)).ActionName);
        db.ChangeTracker.Clear();

        // Read again, it goes in.
        var applied = Assert.IsType<RedirectToActionResult>(await H.Controller(db, blob, audit).Apply(import.Id, null, null, null, plan: await BuilderImportScreens571Tests.ReviewedAsync(db, import.Id)));
        Assert.Equal("Index", applied.ActionName);
        db.ChangeTracker.Clear();

        // The copy of the previous site is kept and used whoever calls: a row read without tracking
        // used to bring a package in and keep no copy at all.
        var loose = await db.WebsiteBuilderImports.AsNoTracking().SingleAsync(i => i.Id == import.Id);
        Assert.True((await BuilderImportService.RestoreAsync(db, loose, blob)).Ok);
        db.ChangeTracker.Clear();
        var second = await StoreAsync(db, blob, site, H.Return(handover.Zip, template => H.Block(template, "home", 0)["heading"] = "Second try"));
        db.ChangeTracker.Clear();
        loose = await db.WebsiteBuilderImports.AsNoTracking().SingleAsync(i => i.Id == second.Id);
        Assert.True((await BuilderImportService.ApplyAsync(db, loose, new HashSet<int>(), null, "owner")).Ok);
        db.ChangeTracker.Clear();
        var stored = await db.WebsiteBuilderImports.AsNoTracking().SingleAsync(i => i.Id == second.Id);
        Assert.Equal(WebsiteBuilderImportStatus.Applied, stored.Status);
        Assert.Contains("Fresh every morning", stored.SnapshotJson);
        Assert.Equal("owner", stored.AppliedBy);
    }

    // ------------------------------------------------ pictures a hand-over or a kept copy still needs

    [Fact]
    public async Task A_picture_that_was_handed_over_is_kept_until_a_newer_hand_over_whatever_it_is_called()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new MemoryBlob();
        var seeded = await H.SeedFittingSiteAsync(db, blob);
        // A file name with what the default JSON writer escapes: an apostrophe, an accent, an ampersand.
        var odd = await blob.UploadAsync(new MemoryStream(H.Png(11)), "Bob's caf" + (char)0xE9 + " & co.png", "agent-logos", "image/png", false);
        (await db.AgentWebsites.SingleAsync(w => w.Id == seeded.Site.Id)).LogoUrl = odd;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        async Task<AgentWebsite> SiteAsync() => await db.AgentWebsites.AsNoTracking().Include(w => w.AgentUser).Include(w => w.Template).SingleAsync(w => w.Id == seeded.Site.Id);

        var handover = await HandOverAsync(db, blob, await SiteAsync());

        // The customer replaces the logo in My Website. Nothing on the site names the old file now; the
        // package out with the designer still carries it, and its return will link that very file.
        var other = await blob.UploadAsync(new MemoryStream(H.Png(12)), "plain.png", "agent-logos", "image/png", false);
        (await db.AgentWebsites.SingleAsync(w => w.Id == seeded.Site.Id)).LogoUrl = other;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.True(await BlobReferences.IsReferencedAsync(db, odd));

        // The return comes back and is brought in: the old logo is on the site again, and its file is there.
        var import = await StoreAsync(db, blob, await SiteAsync(), H.Return(handover.Zip, _ => { }));
        Assert.True((await BuilderImportService.ApplyAsync(db, import, new HashSet<int>(), null, "owner")).Ok);
        db.ChangeTracker.Clear();
        Assert.Equal(odd, (await SiteAsync()).LogoUrl);
        Assert.True(blob.Files.ContainsKey(odd));
        // The copy of the site as it was names the logo it replaced, as text the keep-check can find.
        Assert.Contains(other, (await db.WebsiteBuilderImports.AsNoTracking().SingleAsync(i => i.Id == import.Id)).SnapshotJson);
        Assert.Contains(odd, SiteSnapshot.Capture(new AgentWebsite { Id = 1, TemplateId = 1, LogoUrl = odd }, new List<WebsitePage>()).ToJson());

        // A newer hand-over lets go of the older one's list.
        (await db.AgentWebsites.SingleAsync(w => w.Id == seeded.Site.Id)).LogoUrl = other;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await HandOverAsync(db, blob, await SiteAsync());
        var lists = await db.WebsiteBuilderHandovers.AsNoTracking().Where(h => h.AgentWebsiteId == seeded.Site.Id).OrderBy(h => h.Id).Select(h => h.DeliveredJson).ToListAsync();
        Assert.Equal("[]", lists[0]);
        Assert.Contains(other, lists[1]);
        Assert.DoesNotContain(odd, lists[1]);
    }

    [Fact]
    public async Task Putting_the_site_back_takes_the_package_s_unused_pictures_out_of_the_library()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new MemoryBlob();
        var site = (await H.SeedFittingSiteAsync(db, blob)).Site;
        var handover = await HandOverAsync(db, blob, site);
        var import = await StoreAsync(db, blob, site, H.Return(handover.Zip, template =>
        {
            template["assets"]!.AsArray().Add(new JsonObject { ["ref"] = "assets/new-pic.png", ["type"] = "image/png" });
            H.Block(template, "about", 0)["imageRef"] = "assets/new-pic.png";
        }, addFiles: new[] { ("assets/new-pic.png", H.Png(42)) }));
        Assert.Equal(1, import.NewImageCount);
        var added = await db.WebsiteMediaAssets.AsNoTracking().Where(a => a.AgentWebsiteId == site.Id).OrderByDescending(a => a.Id).FirstAsync();
        Assert.True((await BuilderImportService.ApplyAsync(db, import, new HashSet<int>(), null, "owner")).Ok);
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.WebsiteMediaAssets.CountAsync(a => a.AgentWebsiteId == site.Id));

        Assert.True((await BuilderImportService.RestoreAsync(db, await db.WebsiteBuilderImports.SingleAsync(i => i.Id == import.Id), blob)).Ok);
        db.ChangeTracker.Clear();

        // The picture the package brought is gone with it; the site's own picture stays.
        var left = await db.WebsiteMediaAssets.AsNoTracking().Where(a => a.AgentWebsiteId == site.Id).ToListAsync();
        Assert.Equal("Shop Front.png", Assert.Single(left).OriginalFileName);
        Assert.Contains(added.BlobUrl, blob.Deleted);
        Assert.DoesNotContain(left[0].BlobUrl, blob.Deleted);
    }

    // ------------------------------------------------ what the hand-over fetches

    [Fact]
    public async Task The_hand_over_fetches_no_more_than_a_package_can_hold()
    {
        var fetched = new List<string>();
        byte[] Picture(int megabytes)
        {
            var bytes = new byte[megabytes * 1024 * 1024];
            H.Png((byte)fetched.Count).CopyTo(bytes, 0);
            bytes[^1] = (byte)fetched.Count;
            return bytes;
        }
        var site = new AgentWebsite { Id = 9, AgentUserId = 3, TemplateId = 1, SiteTitle = "Big" };
        WebsitePage Page(int i, string image) => new()
        {
            Id = 100 + i, AgentWebsiteId = 9, Title = i == 0 ? "Home" : "Page " + i, Slug = i == 0 ? "home" : "page-" + i, NavigationLabel = "P" + i, IsHomePage = i == 0, SortOrder = i,
            Blocks = { new WebsiteContentBlock { Id = 1000 + i, WebsitePageId = 100 + i, BlockType = WebsiteBlockTypes.Text, Heading = "Section " + i, SortOrder = 0, IsVisible = true, ImageUrl = image } }
        };

        // Twelve pages, a 7 MB picture on each: five fit, the sixth is one too many, and the other six
        // are never asked for. Before, all twelve were read into memory and the package refused last.
        var heavy = Enumerable.Range(0, 12).Select(i => Page(i, $"https://store.blob.core.windows.net/website-media/{i:D32}_p.png")).ToList();
        var refused = await SiteHandoverBuilder.BuildAsync(Input(site, heavy, new List<Article>()),
            url => { fetched.Add(url); return Task.FromResult<byte[]?>(Picture(7)); });
        Assert.False(refused.CanHandOver);
        Assert.Contains(refused.Blockers, b => b.Contains("more than a 40 MB package can hold"));
        Assert.Equal(6, fetched.Count);

        // Three hundred articles with a 1 MB picture each: six fill the catalogue's 6 MB, the seventh
        // shows it is full, and 293 are not downloaded to be thrown away.
        fetched.Clear();
        var articles = Enumerable.Range(1, 300).Select(i => new Article
        {
            Id = i, Title = "Article " + i, Summary = "", ImageUrl = $"https://store.blob.core.windows.net/article-media/{i:D32}_a.png",
            IsPublished = true, PublishedAt = DateTime.UtcNow.AddDays(-i), CreatedAt = DateTime.UtcNow.AddDays(-i)
        }).ToList();
        var light = new List<WebsitePage> { Page(0, string.Empty) };
        var made = await SiteHandoverBuilder.BuildAsync(Input(site, light, articles),
            url => { fetched.Add(url); return Task.FromResult<byte[]?>(Picture(1)); });
        Assert.True(made.CanHandOver, string.Join("\n", made.Blockers));
        Assert.Equal(7, fetched.Count);
        Assert.Equal(300, made.ArticleIds.Count);

        // A site past the contract's own size says so from its rows, before any picture.
        fetched.Clear();
        var many = Enumerable.Range(0, 31).Select(i => Page(i, $"https://store.blob.core.windows.net/website-media/{i:D32}_p.png")).ToList();
        var tooMany = await SiteHandoverBuilder.BuildAsync(Input(site, many, new List<Article>()), url => { fetched.Add(url); return Task.FromResult<byte[]?>(Picture(1)); });
        Assert.Contains(tooMany.Blockers, b => b.Contains("31 pages") && b.Contains("at most 30"));
        Assert.Empty(fetched);
    }

    [Fact]
    public async Task A_picture_over_the_limit_is_said_to_be_too_large_and_only_storage_s_own_address_is_read()
    {
        var blob = new MemoryBlob();
        var huge = "https://blob.example.test/website-media/" + new string('a', 32) + "_huge.png";
        var whole = "https://blob.example.test/website-media/" + new string('b', 32) + "_whole.png";
        blob.Files[huge] = new byte[9_000_000];
        blob.Files[whole] = new byte[BuilderContract.MaxAssetBytes];

        // One byte past the limit, so the builder says "larger than 8 MB". It used to stop a little
        // SHORT of the limit and hand a cut-off file on as a whole picture.
        Assert.Equal(BuilderContract.MaxAssetBytes + 1, (await SiteHandoverService.FetchImageAsync(blob, huge))!.LongLength);
        Assert.Equal(BuilderContract.MaxAssetBytes, (await SiteHandoverService.FetchImageAsync(blob, whole))!.LongLength);

        // An address that merely resolves to a file is not read: storage must say it is its own.
        Assert.Null(await SiteHandoverService.FetchImageAsync(new NotItsOwn(blob), whole));
        Assert.NotNull(await SiteHandoverService.FetchImageAsync(blob, whole));

        // A starter banner is one of the files the page editor offers, by its exact name.
        Assert.Equal("agreed.jpg", SiteHandoverService.StarterBannerFile("/images/starter-banners/agreed.jpg"));
        Assert.Null(SiteHandoverService.StarterBannerFile("/images/starter-banners/not-in-the-list.jpg"));
        Assert.Null(SiteHandoverService.StarterBannerFile("/images/starter-banners/../agreed.jpg"));
        Assert.Null(SiteHandoverService.StarterBannerFile("https://elsewhere.example.test/images/starter-banners/agreed.jpg"));
    }

    [Fact]
    public async Task A_site_with_a_starter_banner_and_a_sub_page_goes_out_comes_back_and_can_be_put_back()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new MemoryBlob();
        var seeded = await H.SeedFittingSiteAsync(db, blob);
        var site = seeded.Site;
        const string banner = "/images/starter-banners/agreed.jpg";

        // The hero takes one of the page editor's starter banners; "about" gets a sub-page.
        var hero = await db.WebsiteContentBlocks.SingleAsync(b => b.WebsitePage.AgentWebsiteId == site.Id && b.BlockType == WebsiteBlockTypes.Hero);
        hero.ImageUrl = banner;
        var aboutId = (await db.WebsitePages.AsNoTracking().SingleAsync(p => p.AgentWebsiteId == site.Id && p.Slug == "about")).Id;
        db.Add(new WebsitePage { AgentWebsiteId = site.Id, ParentPageId = aboutId, Title = "Our history", Slug = "history", NavigationLabel = "History", SortOrder = 0,
            Blocks = { new WebsiteContentBlock { BlockType = WebsiteBlockTypes.Text, Heading = "Since 1998", SortOrder = 0 } } });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Without a way to read the portal's file the site cannot go, and the reason is said.
        var allowed = SiteHandoverService.AllowedBlockTypes(true, true, true);
        var unread = await SiteHandoverService.BuildAsync(db, blob, site, allowed, AgentStorage.LimitBytes(50));
        Assert.Contains(unread.Blockers, b => b.Contains("could not be read"));
        var asked = new List<string>();
        var handover = await HandOverAsync(db, blob, site, file => { asked.Add(file); return Task.FromResult<byte[]?>(H.Png(9)); });
        Assert.Equal(new[] { "agreed.jpg" }, asked);
        Assert.Contains("\"parentSlug\": \"about\"", handover.TemplateJson);

        // Made in iPro after the hand-over: a three-level branch the builder never saw.
        var resources = new WebsitePage { AgentWebsiteId = site.Id, Title = "Resources", Slug = "resources", NavigationLabel = "Resources", SortOrder = 7,
            Blocks = { new WebsiteContentBlock { BlockType = WebsiteBlockTypes.Text, Heading = "Resources", SortOrder = 0 } } };
        db.Add(resources);
        await db.SaveChangesAsync();
        var guides = new WebsitePage { AgentWebsiteId = site.Id, ParentPageId = resources.Id, Title = "Guides", Slug = "guides", NavigationLabel = "Guides", SortOrder = 0,
            Blocks = { new WebsiteContentBlock { BlockType = WebsiteBlockTypes.Text, Heading = "Guides", SortOrder = 0 } } };
        db.Add(guides);
        await db.SaveChangesAsync();
        db.Add(new WebsitePage { AgentWebsiteId = site.Id, ParentPageId = guides.Id, Title = "Starter guide", Slug = "starter-guide", NavigationLabel = "Starter", SortOrder = 0,
            Blocks = { new WebsiteContentBlock { BlockType = WebsiteBlockTypes.Text, Heading = "Start here", SortOrder = 0 } } });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // The builder adds a second sub-page under "about".
        var import = await StoreAsync(db, blob, site, H.Return(handover.Zip, template => template["pages"]!.AsArray().Add(new JsonObject
        {
            ["slug"] = "team", ["parentSlug"] = "about", ["title"] = "Team", ["navigationLabel"] = "Team", ["isHomePage"] = false, ["sortOrder"] = 1,
            ["blocks"] = new JsonArray(H.NewBlock("Text", 0, body: "We are three."))
        })));
        Assert.Equal(0, import.NewImageCount);   // the banner came back unchanged and is not stored again

        async Task<Dictionary<string, string?>> ParentsAsync()
        {
            var rows = await db.WebsitePages.AsNoTracking().Where(p => p.AgentWebsiteId == site.Id).ToListAsync();
            var slug = rows.ToDictionary(r => r.Id, r => r.Slug);
            return rows.ToDictionary(r => r.Slug, r => r.ParentPageId.HasValue ? slug[r.ParentPageId.Value] : null);
        }
        var before = await ParentsAsync();

        Assert.True((await BuilderImportService.ApplyAsync(db, import, new HashSet<int>(), null, "owner")).Ok);
        db.ChangeTracker.Clear();
        var after = await ParentsAsync();
        Assert.Equal("about", after["history"]);
        Assert.Equal("about", after["team"]);
        Assert.Equal("resources", after["guides"]);
        Assert.Equal("guides", after["starter-guide"]);
        Assert.Null(after["resources"]);
        // The banner is still the portal's own file, not a copy in the customer's library.
        Assert.Equal(banner, (await db.WebsiteContentBlocks.AsNoTracking().SingleAsync(b => b.WebsitePage.AgentWebsiteId == site.Id && b.BlockType == WebsiteBlockTypes.Hero)).ImageUrl);

        Assert.True((await BuilderImportService.RestoreAsync(db, await db.WebsiteBuilderImports.SingleAsync(i => i.Id == import.Id), blob)).Ok);
        db.ChangeTracker.Clear();
        Assert.Equal(before, await ParentsAsync());
    }

    // ------------------------------------------------ the look

    [Fact]
    public async Task The_look_opens_only_with_its_key_and_only_while_the_package_waits()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new MemoryBlob();
        var site = (await H.SeedFittingSiteAsync(db, blob)).Site;
        var handover = await HandOverAsync(db, blob, site);
        var import = await StoreAsync(db, blob, site, H.Return(handover.Zip, template => H.Block(template, "home", 0)["heading"] = "Edited in the builder"));
        var key = await BuilderImportService.LookKeyAsync(db, import);
        db.ChangeTracker.Clear();
        PublicWebsiteController Portal() => new(
            db, new PackageEntitlementService(new UnitOfWork(db), db), null!, NullLogger<PublicWebsiteController>.Instance, new ConfigurationBuilder().Build(),
            DataProtectionProvider.Create($"look-574-{Guid.NewGuid():N}"), blob, new MemoryCache(new MemoryCacheOptions()), null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        Task<IActionResult> Look(int id, string? with) => Portal().PreviewBuilderImport(id, with, null, null, null, null);

        var open = Portal();
        var shown = Assert.IsType<ViewResult>(await open.PreviewBuilderImport(import.Id, key, null, null, null, null));
        Assert.NotNull(shown.Model);
        Assert.Equal("no-store", open.Response.Headers.CacheControl.ToString());
        Assert.Equal("noindex, nofollow", open.Response.Headers["X-Robots-Tag"].ToString());

        var wrong = key[..47] + (key[47] == '0' ? '1' : '0');
        Assert.IsType<NotFoundResult>(await Look(import.Id, wrong));
        Assert.IsType<NotFoundResult>(await Look(import.Id, null));
        Assert.IsType<NotFoundResult>(await Look(import.Id, key.ToUpperInvariant()));
        Assert.IsType<NotFoundResult>(await Look(import.Id + 999, key));
        Assert.IsType<NotFoundResult>(await Look(0, key));

        // An hour is an hour.
        (await db.WebsiteBuilderImports.SingleAsync(i => i.Id == import.Id)).PreviewKeyExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.IsType<NotFoundResult>(await Look(import.Id, key));

        // Once the package is on the site, the site is the look.
        var tracked = await db.WebsiteBuilderImports.SingleAsync(i => i.Id == import.Id);
        var renewed = await BuilderImportService.LookKeyAsync(db, tracked);
        Assert.IsType<ViewResult>(await Look(import.Id, renewed));
        db.ChangeTracker.Clear();
        Assert.True((await BuilderImportService.ApplyAsync(db, await db.WebsiteBuilderImports.SingleAsync(i => i.Id == import.Id), new HashSet<int>(), null, "owner")).Ok);
        db.ChangeTracker.Clear();
        Assert.IsType<NotFoundResult>(await Look(import.Id, renewed));
    }

    // ------------------------------------------------ the door

    [Fact]
    public void A_damaged_package_or_a_broken_character_is_refused_and_never_an_error()
    {
        var context = BuilderImportContext.DesignerTemplate();
        var good = BuilderImport571Tests.Zip(BuilderImport571Tests.Designer());
        Assert.True(BuilderPackageReader.Read(good, context).IsValid);

        var noise = new byte[4096];
        new Random(574).NextBytes(noise);
        noise[0] = 0x50; noise[1] = 0x4B; noise[2] = 0x03; noise[3] = 0x04;
        var damaged = new Dictionary<string, byte[]>
        {
            ["cut in half"] = good[..(good.Length / 2)],
            ["cut near its end"] = good[..(good.Length - 30)],
            ["its first bytes only"] = good[..40],
            ["packed a way the reader does not know"] = Patched(good, method: 99, encrypted: false),
            ["marked as password-protected"] = Patched(good, method: null, encrypted: true),
            ["noise with a ZIP's first bytes"] = noise,
            ["nothing at all"] = Array.Empty<byte>()
        };
        foreach (var (what, bytes) in damaged)
        {
            var read = BuilderPackageReader.Read(bytes, context);
            // The reader may take a file that only CLAIMS a password (its contents are plain); the point there is that nothing throws.
            if (what != "marked as password-protected")
            {
                Assert.False(read.IsValid, what);
                Assert.NotEmpty(read.Problems);
            }
            _ = BuilderPackageReader.PeekHandoverId(bytes);   // must not throw either
        }

        // Half of a two-part character written as an escape: the JSON reader accepts the document
        // and cannot hand the text over. It used to end the upload with an error page.
        var (template, files) = BuilderImport571Tests.Designer();
        var json = template.ToJsonString();
        var at = json.IndexOf("\"heading\":\"", StringComparison.Ordinal) + "\"heading\":\"".Length;
        var broken = json[..at] + (char)92 + "ud83d" + json[at..];
        var refused = BuilderPackageReader.Read(BuilderImport571Tests.Zip(broken, files), context);
        Assert.False(refused.IsValid);
        Assert.Contains(refused.Problems, p => p.Message.Contains("broken character"));
        Assert.Null(BuilderPackageReader.PeekHandoverId(BuilderImport571Tests.Zip(json.Replace("\"handoverId\":\"\"", "\"handoverId\":\"" + (char)92 + "ud83d\""), files)));
    }

    [Fact]
    public async Task A_site_built_from_scratch_names_the_form_it_cannot_bring_back()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new MemoryBlob();
        var site = (await H.SeedFittingSiteAsync(db, blob)).Site;

        var import = await StoreAsync(db, blob, site, BuilderImport571Tests.Zip(BuilderImport571Tests.Designer()));
        var plan = (await BuilderImportService.PlanAsync(db, import))!.Plan!;

        // The customer's contact page has a custom form. A package built from scratch cannot hold one,
        // so replacing the page loses it: named in red, as a map or a gallery is.
        var contact = plan.Pages.Single(p => p.Page.Slug == "contact");
        Assert.Equal(PageChange.Replaced, contact.Change);
        Assert.Contains(WebsiteBlockTypes.Form, contact.SectionsBefore);
        Assert.Equal(new[] { WebsiteBlockTypes.Form }, contact.SectionsLost);
        // The same page in a return of a hand-over that includes forms loses nothing.
        var handover = await HandOverAsync(db, blob, site);
        var back = await StoreAsync(db, blob, site, H.Return(handover.Zip, _ => { }));
        Assert.Empty((await BuilderImportService.PlanAsync(db, back))!.Plan!.Pages.Single(p => p.Page.Slug == "contact").SectionsLost);
    }

    // ------------------------------------------------ the screens

    [Fact]
    public async Task Making_a_package_asks_first_and_every_audit_line_fits_its_column()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new MemoryBlob();
        var site = (await H.SeedFittingSiteAsync(db, blob)).Site;
        var audit = new H.RecordingAudit();
        async Task<string> IndexAsync()
        {
            db.ChangeTracker.Clear();
            var model = (BuilderImportIndexViewModel)((ViewResult)await H.Controller(db, blob, audit).Index(site.AgentUserId)).Model!;
            return await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Index.cshtml", model);
        }

        // Every press records a new hand-over and closes the one before: the button asks.
        var first = await IndexAsync();
        var button = first.IndexOf("Make the package</button>", StringComparison.Ordinal);
        Assert.True(button > 0);
        Assert.Contains("js-confirm-submit", first[Math.Max(0, button - 1200)..button]);
        Assert.Contains("Only the newest package made for a site can come back, so make one and keep that file.", first);
        Assert.IsType<FileContentResult>(await H.Controller(db, blob, audit).Handover(site.AgentUserId));
        Assert.Contains("Make a NEW package of this site? A package was already made on", await IndexAsync());

        // A refused upload with a very long file name, and a first problem as long as a problem gets.
        await H.Controller(db, blob, audit).Upload(site.AgentUserId, H.Upload(new byte[] { 1, 2, 3 }, new string('n', 300) + ".zip"));
        Assert.Contains(audit.Entries, e => e.Action == "BuilderImportRefused");
        Assert.Contains(audit.Entries, e => e.Action == "BuilderHandoverMade");
        Assert.All(audit.Entries, e => Assert.True(e.Details.Length <= 500, $"{e.Action}: {e.Details.Length} characters"));
    }

    // ------------------------------------------------ helpers

    private static SiteHandoverBuilder.Input Input(AgentWebsite site, IReadOnlyList<WebsitePage> pages, IReadOnlyList<Article> articles) =>
        new(site, "Big Site", "Generic", "modern-professional", pages, SiteHandoverService.AllowedBlockTypes(true, true, false), articles, new List<HandoverForm>(), long.MaxValue, DateTime.UtcNow);

    private static async Task<SiteHandover> HandOverAsync(IPRODbContext db, MemoryBlob blob, AgentWebsite site, Func<string, Task<byte[]?>>? banner = null)
    {
        var handover = await SiteHandoverService.BuildAsync(db, blob, site, SiteHandoverService.AllowedBlockTypes(true, true, true), AgentStorage.LimitBytes(50), banner);
        Assert.True(handover.CanHandOver, string.Join("\n", handover.Blockers));
        await SiteHandoverService.RecordAsync(db, site, handover, "owner");
        db.ChangeTracker.Clear();
        return handover;
    }

    private static async Task<WebsiteBuilderImport> StoreAsync(IPRODbContext db, MemoryBlob blob, AgentWebsite site, byte[] zip)
    {
        var context = await BuilderImportService.ContextAsync(db, site, AgentStorage.LimitBytes(50), BuilderPackageReader.PeekHandoverId(zip));
        var package = BuilderPackageReader.Read(zip, context);
        Assert.True(package.IsValid, string.Join("\n", package.Problems.Select(p => $"{p.Where}: {p.Message}")));
        var stored = await BuilderImportService.StoreAsync(db, blob, site, package, "package.zip", "owner");
        db.ChangeTracker.Clear();
        return await db.WebsiteBuilderImports.SingleAsync(i => i.Id == stored.Id);
    }

    // Every entry of a ZIP given an unknown packing method, or the "encrypted" mark, in both of its headers.
    private static byte[] Patched(byte[] zip, int? method, bool encrypted)
    {
        var bytes = (byte[])zip.Clone();
        for (var i = 0; i + 12 < bytes.Length; i++)
        {
            if (bytes[i] != 0x50 || bytes[i + 1] != 0x4B) continue;
            int flags, packing;
            if (bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04) { flags = i + 6; packing = i + 8; }
            else if (bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02) { flags = i + 8; packing = i + 10; }
            else continue;
            if (encrypted) bytes[flags] |= 0x01;
            if (method.HasValue) { bytes[packing] = (byte)method.Value; bytes[packing + 1] = 0; }
        }
        return bytes;
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    // Storage that reads a file by any address that resolves to it, and says an address is not its own.
    private sealed class NotItsOwn : IBlobStorageService
    {
        private readonly IBlobStorageService _inner;
        public NotItsOwn(IBlobStorageService inner) => _inner = inner;
        public bool IsOwnAddress(string? blobUrl) => false;
        public Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate) => _inner.UploadAsync(fileStream, fileName, containerName, contentType, isPrivate);
        public Task<bool> DeleteAsync(string blobUrl) => _inner.DeleteAsync(blobUrl);
        public Task<Stream?> DownloadAsync(string blobUrl) => _inner.DownloadAsync(blobUrl);
        public Task<List<string>> ListAsync(string containerName) => _inner.ListAsync(containerName);
        public string GetPublicUrl(string containerName, string fileName) => _inner.GetPublicUrl(containerName, fileName);
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => _inner.EnsureContainerAccessAsync(containerName, isPrivate);
    }
}
