using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using IPRO.Admin.Controllers;
using IPRO.Admin.Models;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.Business.SiteImport;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPRO.IntegrationTests;

// 572: the whole trip. iPro hands a customer's site to the Template Builder, the builder sends it
// back, and iPro brings the return in: held to the hand-over it answers, its unchanged pictures
// linked instead of stored again, a page removed in the builder removed, a page made in iPro since
// then kept. And the fixes two independent audits of 571 asked for (2026-10-12): one "bring it in"
// wins however many arrive, a picture deleted meanwhile stops the write instead of leaving a dead
// picture, and the kept copy of a replaced site protects the pictures it names.
public class SiteHandoverReturn572Tests
{
    [Fact]
    public async Task A_site_goes_to_the_builder_and_its_return_is_brought_in()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedFittingSiteAsync(db, blob);
        var site = seeded.Site;
        var limit = AgentStorage.LimitBytes(50);

        // ---- out ----
        var handover = await SiteHandoverService.BuildAsync(db, blob, site, SiteHandoverService.AllowedBlockTypes(true, true, true), limit);
        Assert.True(handover.CanHandOver, string.Join("\n", handover.Blockers));
        var record = await SiteHandoverService.RecordAsync(db, site, handover, "owner");
        db.ChangeTracker.Clear();
        Assert.Equal(handover.HandoverId, record.HandoverId);
        Assert.Equal(3, record.PageCount);
        Assert.Equal(2, record.ImageCount);                       // the logo and the hero picture
        Assert.Equal(64, record.SessionTokenSha256.Length);
        Assert.NotEqual(handover.SessionToken, record.SessionTokenSha256);   // the token itself is not kept
        Assert.Equal(new[] { seeded.ArticleId }, SiteHandoverService.List<int>(record.ArticleIdsJson));
        Assert.Equal(new[] { seeded.FormId }, SiteHandoverService.List<int>(record.FormIdsJson));
        // The hero picture is a row of the media library: it now carries its content hash.
        var hero = await db.WebsiteMediaAssets.AsNoTracking().SingleAsync(a => a.AgentWebsiteId == site.Id);
        Assert.Equal(64, hero.ContentSha256.Length);

        // Meanwhile a page is made in iPro. It was never in the builder.
        db.Add(new WebsitePage { AgentWebsiteId = site.Id, Title = "News", Slug = "news", NavigationLabel = "News", SortOrder = 9,
            Blocks = { new WebsiteContentBlock { BlockType = WebsiteBlockTypes.Text, Heading = "News", SortOrder = 0 } } });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // ---- what the builder sends back ----
        var returned = Return(handover.Zip, template =>
        {
            Block(template, "home", 0)["heading"] = "Edited in the builder";
            // "About" is removed there, a "Services" page with a new picture is added.
            var pages = template["pages"]!.AsArray();
            pages.Remove(pages.Single(p => p!["slug"]!.GetValue<string>() == "about"));
            pages.Add(new JsonObject
            {
                ["slug"] = "services", ["parentSlug"] = null, ["title"] = "Services", ["navigationLabel"] = "Services", ["isHomePage"] = false, ["sortOrder"] = 5,
                ["blocks"] = new JsonArray(NewBlock("Text", 0, body: "What we do.", imageRef: "assets/new-photo.png"))
            });
            template["assets"]!.AsArray().Add(new JsonObject { ["ref"] = "assets/new-photo.png", ["type"] = "image/png" });
        }, addFiles: new[] { ("assets/new-photo.png", Png(42)) }, renameUnchanged: true);

        var context = await BuilderImportService.ContextAsync(db, site, limit, BuilderPackageReader.PeekHandoverId(returned));
        Assert.Equal(handover.HandoverId, context.HandoverId);
        var package = BuilderPackageReader.Read(returned, context);
        Assert.True(package.IsValid, string.Join("\n", package.Problems.Select(p => p.Message)));
        var import = await BuilderImportService.StoreAsync(db, blob, site, package, "return.zip", "owner");
        Assert.Equal(handover.HandoverId, import.HandoverId);
        Assert.Equal(1, import.NewImageCount);   // only the new photo: the two delivered pictures are recognized by content, renamed or not
        var stored = BuilderImportService.StoredImages(import);
        Assert.Equal(seeded.LogoUrl, stored.Single(i => i.Url.Contains("/agent-logos/")).Url);
        Assert.Equal(0, stored.Single(i => i.Url.Contains("/agent-logos/")).MediaAssetId);   // a logo is not a row of the library
        Assert.Equal(hero.Id, stored.Single(i => i.Url == hero.BlobUrl).MediaAssetId);
        db.ChangeTracker.Clear();

        // ---- the plan ----
        import = await db.WebsiteBuilderImports.SingleAsync();
        var planned = await BuilderImportService.PlanAsync(db, import);
        Assert.NotNull(planned!.Handover);
        var plan = planned.Plan!;
        Assert.Empty(plan.Problems);
        var about = plan.Pages.Single(p => p.Page.Slug == "about");
        Assert.Equal(PageChange.Removed, about.Change);
        Assert.True(about.RemovedInBuilder);
        Assert.Equal(PageChange.Kept, plan.Pages.Single(p => p.Page.Slug == "news").Change);       // made after the hand-over
        Assert.Equal(PageChange.Added, plan.Pages.Single(p => p.Page.Slug == "services").Change);
        Assert.Equal(PageChange.Replaced, plan.Pages.Single(p => p.Page.IsHomePage).Change);
        Assert.Equal(LogoChange.Unchanged, plan.Logo);
        // The operator may keep what the builder removed.
        var keeping = await BuilderImportService.PlanAsync(db, import, keepPageIds: new HashSet<int> { about.Page.Id });
        Assert.Equal(PageChange.Kept, keeping!.Plan!.Pages.Single(p => p.Page.Slug == "about").Change);

        // ---- in ----
        Assert.True((await BuilderImportService.ApplyAsync(db, import, new HashSet<int>(), null, "owner")).Ok);
        db.ChangeTracker.Clear();
        var after = await db.WebsitePages.AsNoTracking().Where(p => p.AgentWebsiteId == site.Id).Include(p => p.Blocks).ToListAsync();
        Assert.Equal(new[] { "contact", "home", "news", "services" }, after.Select(p => p.Slug).OrderBy(s => s));
        var home = after.Single(p => p.IsHomePage);
        Assert.Equal(seeded.HomeId, home.Id);
        Assert.Equal("Home of the bakery", home.MetaTitle);                                  // what the contract does not carry stays
        var heroAfter = home.Blocks.OrderBy(b => b.SortOrder).First();
        Assert.Equal("Edited in the builder", heroAfter.Heading);
        Assert.Equal(hero.BlobUrl, heroAfter.ImageUrl);                                      // the same stored picture, not a copy
        Assert.Equal("tel:(416) 555-0199", heroAfter.ButtonUrl);                             // a phone number as people write it travels both ways
        Assert.Equal(seeded.FormId, WebsiteFormSettings.FromJson(after.Single(p => p.Slug == "contact").Blocks.Single(b => b.BlockType == WebsiteBlockTypes.Form).SettingsJson).WebsiteFormId);
        Assert.Equal(seeded.LogoUrl, (await db.AgentWebsites.AsNoTracking().SingleAsync(w => w.Id == site.Id)).LogoUrl);
        Assert.Equal(2, await db.WebsiteMediaAssets.CountAsync(a => a.AgentWebsiteId == site.Id));   // the hero and the new photo

        // And back out again: the site as it was, the page made in iPro included.
        Assert.True((await BuilderImportService.RestoreAsync(db, await db.WebsiteBuilderImports.SingleAsync())).Ok);
        db.ChangeTracker.Clear();
        Assert.Equal(new[] { "about", "contact", "home", "news" },
            await db.WebsitePages.Where(p => p.AgentWebsiteId == site.Id).Select(p => p.Slug).OrderBy(s => s).ToListAsync());
        Assert.Null((await db.WebsiteBuilderImports.AsNoTracking().SingleAsync()).SnapshotJson);     // the kept copy has done its work
    }

    [Fact]
    public async Task Only_the_newest_hand_over_can_come_back_and_an_unknown_one_never()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedFittingSiteAsync(db, blob);
        var allowed = SiteHandoverService.AllowedBlockTypes(true, true, true);

        var first = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, allowed, 0);
        await SiteHandoverService.RecordAsync(db, seeded.Site, first, "owner");
        var second = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, allowed, 0);
        await SiteHandoverService.RecordAsync(db, seeded.Site, second, "owner");
        Assert.NotEqual(first.HandoverId, second.HandoverId);
        Assert.Equal(second.HandoverId, (await SiteHandoverService.LatestAsync(db, seeded.Site.Id))!.HandoverId);

        var old = Return(first.Zip, _ => { });
        var refused = BuilderPackageReader.Read(old, await BuilderImportService.ContextAsync(db, seeded.Site, 0, BuilderPackageReader.PeekHandoverId(old)));
        Assert.Contains(refused.Problems, p => p.Rule == "7" && p.Message.Contains($"This site was handed over as {second.HandoverId}"));

        var current = Return(second.Zip, _ => { });
        Assert.True(BuilderPackageReader.Read(current, await BuilderImportService.ContextAsync(db, seeded.Site, 0, BuilderPackageReader.PeekHandoverId(current))).IsValid);

        // A package that waits while a newer hand-over is made can no longer go in.
        var waiting = await BuilderImportService.StoreAsync(db, blob, seeded.Site,
            BuilderPackageReader.Read(current, await BuilderImportService.ContextAsync(db, seeded.Site, 0, second.HandoverId)), "return.zip", "owner");
        var third = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, allowed, 0);
        await SiteHandoverService.RecordAsync(db, seeded.Site, third, "owner");
        var stale = await BuilderImportService.PlanAsync(db, waiting);
        Assert.Null(stale!.Plan);
        Assert.Contains(stale.Package.Problems, p => p.Message.Contains($"This site was handed over as {third.HandoverId}"));
        Assert.False((await BuilderImportService.ApplyAsync(db, waiting, new HashSet<int>(), null, "owner")).Ok);
        Assert.Equal(WebsiteBuilderImportStatus.Pending, (await db.WebsiteBuilderImports.AsNoTracking().SingleAsync()).Status);   // refused, not consumed

        // A site that was never handed over takes no return at all; a peek at a file that is not a package says nothing.
        var other = await BuilderImport571Tests.SeedSiteAsync(db);
        var never = BuilderPackageReader.Read(current, await BuilderImportService.ContextAsync(db, other, 0, BuilderPackageReader.PeekHandoverId(current)));
        Assert.Contains(never.Problems, p => p.Message.Contains("has not handed this site over"));
        Assert.Null(BuilderPackageReader.PeekHandoverId(Encoding.ASCII.GetBytes("not a zip")));
        Assert.Equal("", BuilderPackageReader.PeekHandoverId(BuilderImport571Tests.Zip(BuilderImport571Tests.Designer())));
    }

    [Fact]
    public async Task An_article_or_form_withdrawn_since_the_hand_over_is_refused_at_the_door_and_again_at_the_review()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedFittingSiteAsync(db, blob);
        var handover = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        await SiteHandoverService.RecordAsync(db, seeded.Site, handover, "owner");
        var returned = Return(handover.Zip, template =>
            Page(template, "about")["blocks"]!.AsArray().Add(NewBlock("ArticleContent", 5, settings: new JsonObject { ["articleId"] = seeded.ArticleId })));

        // While the article is published the return passes and waits.
        var package = BuilderPackageReader.Read(returned, await BuilderImportService.ContextAsync(db, seeded.Site, 0, handover.HandoverId));
        Assert.True(package.IsValid, string.Join("\n", package.Problems.Select(p => p.Message)));
        var import = await BuilderImportService.StoreAsync(db, blob, seeded.Site, package, "return.zip", "owner");

        // The customer unpublishes it. The waiting package can no longer be brought in...
        (await db.Articles.SingleAsync(a => a.Id == seeded.ArticleId)).IsPublished = false;
        await db.SaveChangesAsync();
        var planned = await BuilderImportService.PlanAsync(db, import);
        Assert.Null(planned!.Plan);
        Assert.Contains(planned.Package.Problems, p => p.Message.Contains($"Article {seeded.ArticleId} is not one of this customer's published articles"));
        Assert.False((await BuilderImportService.ApplyAsync(db, import, new HashSet<int>(), null, "owner")).Ok);
        // ...and the same package uploaded now is refused at the door.
        var again = BuilderPackageReader.Read(returned, await BuilderImportService.ContextAsync(db, seeded.Site, 0, handover.HandoverId));
        Assert.Contains(again.Problems, p => p.Rule == "7" && p.Message.Contains("published articles"));

        // The same for a form that was switched off.
        (await db.WebsiteForms.SingleAsync(f => f.Id == seeded.FormId)).IsActive = false;
        await db.SaveChangesAsync();
        var plain = Return(handover.Zip, _ => { });
        Assert.Contains(BuilderPackageReader.Read(plain, await BuilderImportService.ContextAsync(db, seeded.Site, 0, handover.HandoverId)).Problems,
            p => p.Message.Contains($"Form {seeded.FormId} is not one of this customer's active forms"));
    }

    [Fact]
    public async Task One_bring_it_in_wins_however_many_arrive_and_the_kept_copy_is_the_real_previous_site()
    {
        // The audit's case: a double click. Both requests loaded the package while it was waiting.
        // If the second also ran, it would keep the ALREADY-IMPORTED site as "the previous site",
        // and the customer's real one could never be put back.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var one = testDb.CreateContext();
        await using var two = testDb.CreateContext();
        var site = await BuilderImport571Tests.SeedSiteAsync(one);
        var package = BuilderPackageReader.Read(BuilderImport571Tests.Zip(BuilderImport571Tests.Designer()), await BuilderImportService.ContextAsync(one, site, 0));
        var stored = await BuilderImportService.StoreAsync(one, new BuilderImport571Tests.MemoryBlob(), site, package, "site.zip", "owner");
        one.ChangeTracker.Clear();
        var first = await one.WebsiteBuilderImports.SingleAsync(i => i.Id == stored.Id);
        var second = await two.WebsiteBuilderImports.SingleAsync(i => i.Id == stored.Id);
        Assert.Equal(WebsiteBuilderImportStatus.Pending, second.Status);

        Assert.True((await BuilderImportService.ApplyAsync(one, first, new HashSet<int>(), null, "owner")).Ok);
        var late = await BuilderImportService.ApplyAsync(two, second, new HashSet<int>(), null, "owner");

        Assert.False(late.Ok);
        Assert.Contains("already been brought in", late.Problems.Single());
        one.ChangeTracker.Clear();
        var row = await one.WebsiteBuilderImports.AsNoTracking().SingleAsync();
        var kept = SiteSnapshot.FromJson(row.SnapshotJson)!;
        Assert.Equal(new[] { "home", "menu" }, kept.Pages.Select(p => p.Slug));          // the customer's own site, not the imported one
        Assert.Equal("Welcome", kept.Pages[0].Blocks[0].Heading);

        // The same for dropping and for putting back: of two, one does.
        Assert.False(await BuilderImportService.DiscardAsync(two, new BuilderImport571Tests.MemoryBlob(), second));
        Assert.True((await BuilderImportService.RestoreAsync(one, await one.WebsiteBuilderImports.SingleAsync())).Ok);
        Assert.False((await BuilderImportService.RestoreAsync(two, second)).Ok);
    }

    [Fact]
    public async Task A_picture_deleted_while_its_package_waits_stops_the_write_and_a_replaced_sites_pictures_are_protected()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await BuilderImport571Tests.SeedSiteAsync(db);
        var blob = new BuilderImport571Tests.MemoryBlob();
        // The customer's own picture, on the page the package is about to replace.
        const string own = "https://blob.example.test/website-media/00000000000000000000000000000001_own-photo.png";
        var heroBlock = await db.WebsiteContentBlocks.FirstAsync(b => b.WebsitePage.AgentWebsiteId == site.Id && b.BlockType == WebsiteBlockTypes.Hero);
        heroBlock.ImageUrl = own;
        db.Add(new WebsiteMediaAsset { AgentWebsiteId = site.Id, OriginalFileName = "own-photo.png", BlobUrl = own, ContentType = "image/png", FileSize = 10 });
        await db.SaveChangesAsync();

        var package = BuilderPackageReader.Read(BuilderImport571Tests.Zip(BuilderImport571Tests.Designer()), await BuilderImportService.ContextAsync(db, site, 0));
        var import = await BuilderImportService.StoreAsync(db, blob, site, package, "site.zip", "owner");

        // One of the package's pictures is deleted from the media library before it is brought in.
        var gone = BuilderImportService.StoredImages(import).First(i => i.Created);
        db.WebsiteMediaAssets.Remove(await db.WebsiteMediaAssets.SingleAsync(a => a.Id == gone.MediaAssetId));
        await db.SaveChangesAsync();
        var planned = await BuilderImportService.PlanAsync(db, import);
        Assert.Contains(planned!.Plan!.Problems, p => p.Contains("deleted from the media library since it was uploaded"));
        var refused = await BuilderImportService.ApplyAsync(db, import, new HashSet<int>(), null, "owner");
        Assert.False(refused.Ok);
        Assert.Equal(WebsiteBuilderImportStatus.Pending, (await db.WebsiteBuilderImports.AsNoTracking().SingleAsync()).Status);
        Assert.Equal("Welcome", (await db.WebsiteContentBlocks.AsNoTracking().FirstAsync(b => b.WebsitePage.AgentWebsiteId == site.Id && b.BlockType == WebsiteBlockTypes.Hero)).Heading);

        // Uploaded again, it goes in. The page it replaced showed the customer's own picture, which
        // is now named only by the kept copy of the site: that copy keeps the file from being deleted.
        Assert.True(await BuilderImportService.DiscardAsync(db, blob, import));
        var fresh = await BuilderImportService.StoreAsync(db, blob, site,
            BuilderPackageReader.Read(BuilderImport571Tests.Zip(BuilderImport571Tests.Designer()), await BuilderImportService.ContextAsync(db, site, 0)), "site.zip", "owner");
        Assert.False(await BlobReferences.IsReferencedAsync(db, own + ".not-there"));
        Assert.True((await BuilderImportService.ApplyAsync(db, fresh, new HashSet<int>(), null, "owner")).Ok);
        db.WebsiteMediaAssets.Remove(await db.WebsiteMediaAssets.SingleAsync(a => a.BlobUrl == own));   // tidied out of the library
        await db.SaveChangesAsync();
        Assert.True(await BlobReferences.IsReferencedAsync(db, own), "the kept copy of the replaced site still names the picture");
        Assert.Contains(("WebsiteBuilderImports", "SnapshotJson"), BlobReferences.ContentColumns);

        // Put back, the page shows it again; and the copy, its work done, no longer holds anything.
        Assert.True((await BuilderImportService.RestoreAsync(db, fresh)).Ok);
        db.ChangeTracker.Clear();
        Assert.Equal(own, (await db.WebsiteContentBlocks.AsNoTracking().FirstAsync(b => b.WebsitePage.AgentWebsiteId == site.Id && b.BlockType == WebsiteBlockTypes.Hero)).ImageUrl);
        Assert.Null((await db.WebsiteBuilderImports.AsNoTracking().SingleAsync(i => i.Id == fresh.Id)).SnapshotJson);
    }

    [Fact]
    public async Task The_home_page_remembers_the_address_it_had()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await BuilderImport571Tests.SeedSiteAsync(db);   // its home page is at "home" and already remembers "index.html"
        var (template, files) = BuilderImport571Tests.Designer();
        Page(template, "home")["slug"] = "welcome";
        var package = BuilderPackageReader.Read(BuilderImport571Tests.Zip(template, files), await BuilderImportService.ContextAsync(db, site, 0));
        var import = await BuilderImportService.StoreAsync(db, new BuilderImport571Tests.MemoryBlob(), site, package, "site.zip", "owner");

        Assert.True((await BuilderImportService.ApplyAsync(db, import, new HashSet<int>(), null, "owner")).Ok);
        db.ChangeTracker.Clear();

        var home = await db.WebsitePages.AsNoTracking().SingleAsync(p => p.AgentWebsiteId == site.Id && p.IsHomePage);
        Assert.Equal("welcome", home.Slug);
        Assert.Equal(new[] { "index.html", "home" }, home.OldAddresses.Split('\n'));   // links to /home still arrive
    }

    [Fact]
    public void The_real_packages_of_a_first_trip_through_both_products_still_agree()
    {
        // Kept from 2026-10-12: a hand-over made by iPro's exporter, opened, edited and exported by the
        // builder's own code (commit cd4e995), unchanged since. If either side drifts, this says so.
        var handoverZip = BuilderImport571Tests.Fixture("roundtrip-ipro-handover.zip");
        using var zip = new ZipArchive(new MemoryStream(handoverZip), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("handover.json")!.Open());
        var handover = JsonNode.Parse(reader.ReadToEnd())!;
        var delivered = zip.Entries.Where(e => e.FullName.StartsWith("assets/")).Select(e =>
        {
            using var s = e.Open(); using var m = new MemoryStream(); s.CopyTo(m);
            return BuilderPackageReader.Hex(System.Security.Cryptography.SHA256.HashData(m.ToArray()));
        }).ToHashSet();
        var context = new BuilderImportContext(
            handover["handoverId"]!.GetValue<string>(),
            handover["allowedBlockTypes"]!.AsArray().Select(t => t!.GetValue<string>()).ToHashSet(),
            handover["catalog"]!["articles"]!.AsArray().Select(a => a!["id"]!.GetValue<int>()).ToHashSet(),
            handover["catalog"]!["forms"]!.AsArray().Select(f => f!["id"]!.GetValue<int>()).ToHashSet(),
            delivered, RemainingBytes: 0);

        var returned = BuilderImport571Tests.Fixture("roundtrip-builder-return.zip");
        Assert.Equal(context.HandoverId, BuilderPackageReader.PeekHandoverId(returned));
        var back = BuilderPackageReader.Read(returned, context);

        Assert.True(back.IsValid, string.Join("\n", back.Problems.Select(p => $"[{p.Rule}] {p.Where}: {p.Message}")));
        Assert.Equal("Round trip: edited in the builder", back.Template!.Pages[0].Blocks[0].Heading);
        Assert.Equal(3, back.Images.Count);
        Assert.Empty(back.NewImages(delivered));   // the builder renames every picture; all three are recognized by content
        Assert.All(back.Images.Keys, name => Assert.StartsWith("assets/ipro-", name));
    }

    [Fact]
    public void A_button_link_has_no_control_character_no_backslash_and_a_space_only_in_a_phone_number()
    {
        // The audits' finding from both sides: iPro's rule let a tab through ("/\t/evil.example" is
        // "//evil.example" to a browser), and the builder refused a phone number written with a space,
        // which is how the bakery writes its own. One rule now, in the contract.
        foreach (var good in new[] { "", "/", "/contact", "https://example.com/book?x=1", "mailto:hello@example.com", "tel:(416) 555-0199", "tel:+1 416 555 0199" })
        {
            Assert.True(BuilderContract.IsAllowedButtonUrl(good), $"'{good}' was refused");
        }
        foreach (var bad in new[]
        {
            "/\t/evil.example", "/\n/evil.example", "/contact us", "/a\\b", "https://example.com/a b", "https://exam\tple.com", "mailto:a b@example.com",
            "tel:", "tel:   ", "tel:416\t555", "Tel:416-555-0199", "HTTPS://example.com", "//evil.example", "http://example.com", "javascript:alert(1)"
        })
        {
            Assert.False(BuilderContract.IsAllowedButtonUrl(bad), $"'{bad.Replace("\t", "<tab>").Replace("\n", "<line break>")}' was allowed");
        }
    }

    [Fact]
    public void What_a_package_says_about_itself_is_quoted_short()
    {
        // A refusal quotes the sender's own text; the first one also goes into the audit log.
        var (template, files) = BuilderImport571Tests.Designer();
        template["contractVersion"] = new string('9', 5000);
        var wrongVersion = BuilderPackageReader.Read(BuilderImport571Tests.Zip(template, files), BuilderImportContext.DesignerTemplate());
        Assert.True(wrongVersion.Problems.Single().Message.Length < 220);

        var (second, secondFiles) = BuilderImport571Tests.Designer();
        second["logoRef"] = "assets/" + new string('x', 4000) + ".png";
        var badLogo = BuilderPackageReader.Read(BuilderImport571Tests.Zip(second, secondFiles), BuilderImportContext.DesignerTemplate());
        Assert.All(badLogo.Problems, p => Assert.True(p.Message.Length < 400, p.Message[..80]));

        var stray = BuilderPackageReader.Read(
            BuilderImport571Tests.Zip(BuilderImport571Tests.Designer().Template.ToJsonString(), BuilderImport571Tests.Designer().Files.Append((new string('q', 3000) + ".txt", new byte[] { 1 }))),
            BuilderImportContext.DesignerTemplate());
        Assert.All(stray.Problems, p => Assert.True(p.Message.Length < 400, p.Message[..80]));
    }

    // ---------------- the screens ----------------

    [Fact]
    public async Task The_admin_makes_the_package_or_is_told_why_not_and_a_return_is_reviewed_as_a_return()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedFittingSiteAsync(db, blob);
        var audit = new RecordingAudit();

        // Made: a download, a record, an audit entry.
        var file = Assert.IsType<FileContentResult>(await Controller(db, blob, audit).Handover(seeded.Site.AgentUserId));
        Assert.Equal("application/zip", file.ContentType);
        Assert.StartsWith("ipro-handover-sample-bakery-", file.FileDownloadName);
        var record = await db.WebsiteBuilderHandovers.AsNoTracking().SingleAsync();
        Assert.Equal("owner", record.IssuedBy);
        Assert.Equal(record.PackageSha256, BuilderPackageReader.Hex(System.Security.Cryptography.SHA256.HashData(file.FileContents)));
        Assert.Contains(audit.Entries, e => e.Action == "BuilderHandoverMade" && e.Details.Contains(record.HandoverId));
        db.ChangeTracker.Clear();

        // The list shows it.
        var index = Assert.IsType<BuilderImportIndexViewModel>(Assert.IsType<ViewResult>(await Controller(db, blob, audit).Index(seeded.Site.AgentUserId)).Model);
        Assert.Equal(record.HandoverId, index.LatestHandover!.HandoverId);
        Assert.Empty(index.HandoverBlockers);

        // The return, uploaded: on to a review that knows what it is.
        var returned = Return(file.FileContents, template =>
        {
            var pages = template["pages"]!.AsArray();
            pages.Remove(pages.Single(p => p!["slug"]!.GetValue<string>() == "about"));
        });
        var uploaded = Assert.IsType<RedirectToActionResult>(await Controller(db, blob, audit).Upload(seeded.Site.AgentUserId, Upload(returned, "return.zip")));
        var importId = (int)uploaded.RouteValues!["id"]!;
        Assert.Contains(audit.Entries, e => e.Action == "BuilderImportStored" && e.Details.Contains("the return of hand-over " + record.HandoverId));
        db.ChangeTracker.Clear();
        var review = Assert.IsType<BuilderImportReviewViewModel>(Assert.IsType<ViewResult>(await Controller(db, blob, audit).Review(importId, null, null)).Model);
        Assert.Equal(record.HandoverId, review.Handover!.HandoverId);
        var about = review.Plan!.Pages.Single(p => p.Page.Slug == "about");
        Assert.True(about.RemovedInBuilder);
        db.ChangeTracker.Clear();

        // Keeping the page the builder removed travels in the review's address and the look's.
        var chosen = Assert.IsType<RedirectToActionResult>(Controller(db, blob, audit).Choose(importId, null, null, new[] { about.Page.Id }));
        Assert.Equal(about.Page.Id.ToString(), chosen.RouteValues!["keep"]);
        var keeping = Assert.IsType<BuilderImportReviewViewModel>(
            Assert.IsType<ViewResult>(await Controller(db, blob, audit).Review(importId, null, null, about.Page.Id.ToString())).Model);
        Assert.Equal(PageChange.Kept, keeping.Plan!.Pages.Single(p => p.Page.Slug == "about").Change);
        Assert.Contains($"&keep={about.Page.Id}&page=", keeping.LookAddress);
        db.ChangeTracker.Clear();

        // Both views render from their real markup with these models.
        var reviewHtml = await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Review.cshtml", review);
        Assert.Contains("This is the <strong>return</strong> of the site handed to the builder", reviewHtml);
        Assert.Contains("Removed in the builder", reviewHtml);
        Assert.Contains($"name=\"keep\" value=\"{about.Page.Id}\"", reviewHtml);
        var indexHtml = await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Index.cshtml", index);
        Assert.Contains("1. Hand the site to the builder", indexHtml);
        Assert.Contains("Last handed over", indexHtml);
        Assert.Contains("Make the package", indexHtml);

        // A site that does not fit: no file, the reasons on the list, nothing recorded.
        var other = await BuilderImport571Tests.SeedSiteAsync(db);   // it has a price list and a reviews section
        var refusedView = Assert.IsType<ViewResult>(await Controller(db, blob, audit).Handover(other.AgentUserId));
        var refused = Assert.IsType<BuilderImportIndexViewModel>(refusedView.Model);
        Assert.Contains(refused.HandoverBlockers, b => b.Contains("cannot carry yet: Price List / Menu"));
        Assert.Contains(refused.HandoverBlockers, b => b.Contains("Review Badge"));
        Assert.Equal(1, await db.WebsiteBuilderHandovers.CountAsync());
        Assert.Contains(audit.Entries, e => e.Action == "BuilderHandoverRefused");
        var refusedHtml = await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Index.cshtml", refused);
        Assert.Contains("This site cannot be handed to the builder yet", refusedHtml);

        var action = typeof(BuilderImportsController).GetMethod("Handover")!;
        Assert.NotNull(action.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }

    [Fact]
    public async Task The_hand_over_table_is_created_at_start_up_and_goes_with_the_adviser()
    {
        var repair = File.ReadAllText(Path.Combine(RepoRoot(), "src", "IPRO.DataAccess", "StartupSchemaRepair.cs"));
        Assert.Contains("CREATE TABLE IF NOT EXISTS `WebsiteBuilderHandovers`", repair);
        Assert.Contains("WebsiteBuilderHandovers", AgentDataEraser.CoveredTables);

        // The table the repair creates is the table the model expects: a hand-over is recorded on it.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE `WebsiteBuilderHandovers`");
        await StartupSchemaRepair.EnsureWebsiteBuilderImportSchemaAsync(db);
        await StartupSchemaRepair.EnsureWebsiteBuilderImportSchemaAsync(db);
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedFittingSiteAsync(db, blob);
        var handover = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        var record = await SiteHandoverService.RecordAsync(db, seeded.Site, handover, "owner");
        Assert.True(record.Id > 0);
        Assert.Equal(record.HandoverId, (await SiteHandoverService.FindAsync(db, seeded.Site.Id, record.HandoverId))!.HandoverId);

        // Erasing the adviser takes the record too.
        await AgentDataEraser.EraseAsync(db, seeded.Site.AgentUserId, eraseFinancialRecords: true);
        Assert.Equal(0, await db.WebsiteBuilderHandovers.CountAsync());
    }

    // ---------------- helpers ----------------

    private sealed record Seeded(AgentWebsite Site, int HomeId, int ArticleId, int FormId, string LogoUrl);

    // A customer's site made only of what the builder carries: a home page with a picture and a phone
    // button, an About page, a Contact page with the lead form and a custom form, a logo, one
    // published article and one active form.
    private static async Task<Seeded> SeedFittingSiteAsync(IPRODbContext db, BuilderImport571Tests.MemoryBlob blob)
    {
        var rule = new BillingRule { PackageName = $"Pkg-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"ho-{Guid.NewGuid():N}"[..20], Email = "handover@example.com", FirstName = "Bake", LastName = "House",
            CompanyName = "Sample Bakery", BusinessType = "Generic", DomainName = $"ho-{Guid.NewGuid():N}"[..24], PackageId = rule.Id
        };
        var template = await db.WebsiteTemplates.FirstOrDefaultAsync(t => t.TemplateKey == "modern-professional")
                       ?? new WebsiteTemplate { TemplateKey = "modern-professional", Name = "Modern", IsActive = true, IsDefault = true };
        db.Add(agent);
        if (template.Id == 0) db.Add(template);
        // The package includes the three paid sections (articles, the blog, custom forms).
        foreach (var code in new[] { PackageFeatureCodes.Newsletters, PackageFeatureCodes.ManagedBlog, PackageFeatureCodes.CustomForms })
        {
            db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = code, FeatureName = code, IsIncluded = true });
        }
        await db.SaveChangesAsync();
        // A paying customer: without a subscription the package's features are not looked up at all.
        db.Add(new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 60m, Status = BillingStatus.Active, Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddDays(-10), NextBillingDate = DateTime.UtcNow.AddDays(20)
        });
        await db.SaveChangesAsync();

        var logoUrl = await blob.UploadAsync(new MemoryStream(Png(1)), "logo.png", "agent-logos", "image/png", false);
        var heroUrl = await blob.UploadAsync(new MemoryStream(Png(2)), "Shop Front.png", "website-media", "image/png", false);
        var site = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, IsPublished = true, SiteTitle = "Sample Bakery", LogoUrl = logoUrl };
        db.Add(site);
        await db.SaveChangesAsync();
        db.Add(new WebsiteMediaAsset { AgentWebsiteId = site.Id, OriginalFileName = "Shop Front.png", BlobUrl = heroUrl, ContentType = "image/png", FileSize = Png(2).Length });

        var article = new Article { AgentUserId = agent.Id, Title = "Sourdough at home", Summary = "A starter in five days.", Content = "<p>Flour and water.</p>", IsPublished = true, PublishedAt = DateTime.UtcNow.AddDays(-3) };
        var draft = new Article { AgentUserId = agent.Id, Title = "Not ready", Summary = "", Content = "", IsPublished = false };
        var form = new WebsiteForm { AgentUserId = agent.Id, Title = "Order a cake", Description = "Tell us about the cake.", SubmitButtonText = "Send", IsActive = true };
        var retired = new WebsiteForm { AgentUserId = agent.Id, Title = "Old form", IsActive = false };
        db.AddRange(article, draft, form, retired);
        await db.SaveChangesAsync();
        var flavour = new WebsiteFormField { WebsiteFormId = form.Id, FieldType = WebsiteFormFieldTypes.Dropdown, Label = "Flavour", IsRequired = true, SortOrder = 0 };
        db.Add(flavour);
        await db.SaveChangesAsync();
        db.AddRange(new WebsiteFormFieldOption { WebsiteFormFieldId = flavour.Id, Text = "Chocolate", SortOrder = 0 }, new WebsiteFormFieldOption { WebsiteFormFieldId = flavour.Id, Text = "Lemon", SortOrder = 1 });

        var home = new WebsitePage { AgentWebsiteId = site.Id, Title = "Home", Slug = "home", NavigationLabel = "Home", IsHomePage = true, SortOrder = 0, MetaTitle = "Home of the bakery" };
        var about = new WebsitePage { AgentWebsiteId = site.Id, Title = "About", Slug = "about", NavigationLabel = "About", SortOrder = 1 };
        var contact = new WebsitePage { AgentWebsiteId = site.Id, Title = "Contact", Slug = "contact", NavigationLabel = "Contact", SortOrder = 2 };
        db.AddRange(home, about, contact);
        await db.SaveChangesAsync();
        db.AddRange(
            new WebsiteContentBlock { WebsitePageId = home.Id, BlockType = WebsiteBlockTypes.Hero, Heading = "Fresh every morning", ImageUrl = heroUrl, ButtonText = "Call us", ButtonUrl = "Tel:(416) 555-0199", SortOrder = 0, SettingsJson = new WebsiteHeroSettings { Layout = "full-image" }.ToJson() },
            new WebsiteContentBlock { WebsitePageId = home.Id, BlockType = WebsiteBlockTypes.Blog, Heading = "From the oven", SortOrder = 1, SettingsJson = new WebsiteBlogSettings { PostCount = 3 }.ToJson() },
            new WebsiteContentBlock { WebsitePageId = about.Id, BlockType = WebsiteBlockTypes.Text, Heading = "About us", Body = "Baking since 1998.\r\nStill at it.", SortOrder = 0 },
            new WebsiteContentBlock { WebsitePageId = contact.Id, BlockType = WebsiteBlockTypes.ContactForm, Heading = "Say hello", SortOrder = 0, SettingsJson = new WebsiteContactFormSettings { ShowPhoto = false }.ToJson() },
            new WebsiteContentBlock { WebsitePageId = contact.Id, BlockType = WebsiteBlockTypes.Form, Subheading = "Cakes", SortOrder = 1, SettingsJson = new WebsiteFormSettings { WebsiteFormId = form.Id }.ToJson() });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var loaded = await db.AgentWebsites.AsNoTracking().Include(w => w.AgentUser).Include(w => w.Template).SingleAsync(w => w.Id == site.Id);
        return new Seeded(loaded, home.Id, article.Id, form.Id, logoUrl);
    }

    // What the builder would send back for a hand-over package: its template.json (changed by
    // `edit`) and its site images, without handover.json and the catalogue. renameUnchanged gives
    // every delivered image the name the real builder gives it ("ipro-<sha256>").
    private static byte[] Return(byte[] handoverZip, Action<JsonObject> edit, (string Name, byte[] Bytes)[]? addFiles = null, bool renameUnchanged = false)
    {
        JsonObject template = null!;
        var files = new List<(string Name, byte[] Bytes)>();
        using (var zip = new ZipArchive(new MemoryStream(handoverZip), ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                using var stream = entry.Open();
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                if (entry.FullName == "template.json") template = JsonNode.Parse(Encoding.UTF8.GetString(copy.ToArray()))!.AsObject();
                else if (entry.FullName.StartsWith("assets/")) files.Add((entry.FullName, copy.ToArray()));
            }
        }
        if (renameUnchanged)
        {
            var renamed = files.ToDictionary(f => f.Name, f => "assets/ipro-" + BuilderPackageReader.Hex(System.Security.Cryptography.SHA256.HashData(f.Bytes)) + ".png");
            string To(string name) => name.Length > 0 && renamed.TryGetValue(name, out var to) ? to : name;
            template["logoRef"] = To(template["logoRef"]!.GetValue<string>());
            foreach (var block in template["pages"]!.AsArray().SelectMany(p => p!["blocks"]!.AsArray())) block!["imageRef"] = To(block["imageRef"]!.GetValue<string>());
            foreach (var asset in template["assets"]!.AsArray()) asset!["ref"] = To(asset["ref"]!.GetValue<string>());
            files = files.Select(f => (renamed[f.Name], f.Bytes)).ToList();
        }
        edit(template);
        // The builder leaves out an image nothing uses any more.
        var used = template["pages"]!.AsArray().SelectMany(p => p!["blocks"]!.AsArray()).Select(b => b!["imageRef"]!.GetValue<string>())
            .Append(template["logoRef"]!.GetValue<string>()).Where(r => r.Length > 0).ToHashSet();
        var assets = template["assets"]!.AsArray();
        foreach (var unused in assets.Where(a => !used.Contains(a!["ref"]!.GetValue<string>())).ToList()) assets.Remove(unused);
        files = files.Concat(addFiles ?? Array.Empty<(string, byte[])>()).Where(f => used.Contains(f.Name)).ToList();
        return BuilderImport571Tests.Zip(template.ToJsonString(), files);
    }

    private static JsonObject Page(JsonObject template, string slug) =>
        template["pages"]!.AsArray().Single(p => p!["slug"]!.GetValue<string>() == slug)!.AsObject();

    private static JsonObject Block(JsonObject template, string slug, int index) => Page(template, slug)["blocks"]!.AsArray()[index]!.AsObject();

    private static JsonObject NewBlock(string type, int sortOrder, string body = "", string imageRef = "", JsonObject? settings = null) => new()
    {
        ["type"] = type, ["heading"] = "", ["subheading"] = "", ["body"] = body, ["imageRef"] = imageRef, ["buttonText"] = "", ["buttonUrl"] = "",
        ["layoutVariant"] = "", ["sortOrder"] = sortOrder, ["isVisible"] = true, ["settings"] = settings ?? new JsonObject()
    };

    private static byte[] Png(byte seed) => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, seed, seed, 1, 2 };

    private static BuilderImportsController Controller(IPRODbContext db, IBlobStorageService blob, IAdminAuditLogService audit)
    {
        var services = new ServiceCollection().AddSingleton(blob).BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:PortalBaseUrl"] = "https://portal.example.test/" })
            .Build();
        var controller = new BuilderImportsController(db, new PackageEntitlementService(new UnitOfWork(db), db), audit, configuration, services);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Name, "owner"), new Claim("Role", "SuperAdmin")
            }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NullTempData());
        return controller;
    }

    private static IFormFile Upload(byte[] bytes, string name) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "package", name) { Headers = new HeaderDictionary(), ContentType = "application/zip" };

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private sealed class RecordingAudit : IAdminAuditLogService
    {
        public List<(string Action, string Details)> Entries { get; } = new();
        public Task LogAsync(int adminUserId, string adminUsername, string action, string details)
        {
            Entries.Add((action, details));
            return Task.CompletedTask;
        }
    }

    private sealed class NullTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
