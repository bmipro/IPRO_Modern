using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.Business.SiteImport;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Scheduler;
using IPRO.Utility;
using IPRO.Web.Controllers;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 585: the new builder (Beta) for iPro's customers. The doors answer only the builder's server (the key
// the two share); a pass works once and for ten minutes; a session stops the moment the Beta is switched
// off, the account closed, or the package no longer includes the website; the builder's server learns
// where the site's newest hand-over stands, gets a fresh one, and can only send back a package of it,
// never a second one after its changes went onto the website; what an adviser sends waits for iPro's
// staff, or for the adviser's own review where a SuperAdmin allows it, which keeps what they did not
// remove; a look writes nothing to the site; the previous site can be put back; and deleting an adviser
// asks the builder to erase their draft.
public class BuilderBeta585Tests
{
    [Fact]
    public async Task A_pass_works_once_and_only_while_the_beta_is_on()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seeded = await SeedAsync(db, new BuilderImport571Tests.MemoryBlob());
        var now = DateTime.UtcNow;

        var beta = await BuilderBeta.SetAsync(db, seeded.Site.AgentUserId, enabled: true, adviserPublishes: false, "owner");
        Assert.Matches("^t_[0-9a-f]{32}$", beta.Tenant);
        var tenant = beta.Tenant;
        Assert.Equal(tenant, (await BuilderBeta.SetAsync(db, seeded.Site.AgentUserId, true, true, "owner")).Tenant);

        var pass = await BuilderBeta.IssuePassAsync(db, seeded.Site, startFresh: false, now);
        Assert.True(BuilderBeta.LooksLikeToken(pass));
        Assert.DoesNotContain(pass, (await db.WebsiteBuilderSessions.AsNoTracking().SingleAsync()).TokenSha256);
        var (granted, _) = await BuilderBeta.RedeemPassAsync(db, pass, now);
        Assert.NotNull(granted);
        Assert.False(granted!.Session.StartFresh);
        Assert.Equal(new BuilderBeta.CurrentHandover("", BuilderBeta.HandoverStates.Open, null), granted.Current);
        Assert.Equal(now.Add(BuilderBeta.SessionLifetime), granted.Session.ExpiresAt, TimeSpan.FromSeconds(1));
        // The visit the builder's 90 days, and the card's "Continue my draft", count from.
        Assert.Equal(now, (await BuilderBeta.FindAsync(db, seeded.Site.AgentUserId))!.LastOpenedAt!.Value, TimeSpan.FromSeconds(1));
        var (again, refusal) = await BuilderBeta.RedeemPassAsync(db, pass, now);
        Assert.Null(again);
        Assert.Contains("already used or has expired", refusal);

        var late = await BuilderBeta.IssuePassAsync(db, seeded.Site, false, now);
        Assert.Null((await BuilderBeta.RedeemPassAsync(db, late, now.AddMinutes(11))).Granted);

        // A pass issued before the Beta was switched off stays spent, also if it is switched on again
        // within its ten minutes.
        var off = await BuilderBeta.IssuePassAsync(db, seeded.Site, false, now);
        await BuilderBeta.SetAsync(db, seeded.Site.AgentUserId, enabled: false, adviserPublishes: false, "owner");
        await BuilderBeta.SetAsync(db, seeded.Site.AgentUserId, enabled: true, adviserPublishes: false, "owner");
        var (none, why) = await BuilderBeta.RedeemPassAsync(db, off, now);
        Assert.Null(none);
        Assert.Contains("already used or has expired", why);
        Assert.Null((await BuilderBeta.RedeemPassAsync(db, "not-a-pass", now)).Granted);

        // And the Beta itself is asked: off, nothing opens.
        var unasked = await BuilderBeta.IssuePassAsync(db, seeded.Site, false, now);
        await db.AgentBuilderBetas.Where(b => b.AgentUserId == seeded.Site.AgentUserId).ExecuteUpdateAsync(b => b.SetProperty(x => x.Enabled, false));
        Assert.Contains("not switched on", (await BuilderBeta.RedeemPassAsync(db, unasked, now)).Refusal);
    }

    [Fact]
    public async Task A_session_stops_when_the_beta_is_switched_off_or_the_account_closed()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seeded = await SeedAsync(db, new BuilderImport571Tests.MemoryBlob());
        var token = await SessionAsync(db, seeded.Site);
        var now = DateTime.UtcNow;
        Assert.NotNull((await BuilderBeta.FindSessionAsync(db, token, now)).Session);
        Assert.Null((await BuilderBeta.FindSessionAsync(db, token, now.AddHours(13))).Session);

        await BuilderBeta.SetAsync(db, seeded.Site.AgentUserId, enabled: false, adviserPublishes: false, "owner");
        db.ChangeTracker.Clear();
        Assert.Null((await BuilderBeta.FindSessionAsync(db, token, now)).Session);
        await BuilderBeta.SetAsync(db, seeded.Site.AgentUserId, enabled: true, adviserPublishes: false, "owner");
        db.ChangeTracker.Clear();
        Assert.Null((await BuilderBeta.FindSessionAsync(db, token, now)).Session);

        var second = await SessionAsync(db, seeded.Site);
        await db.AgentUsers.Where(a => a.Id == seeded.Site.AgentUserId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, false));
        Assert.Null((await BuilderBeta.FindSessionAsync(db, second, DateTime.UtcNow)).Session);
    }

    [Fact]
    public async Task The_builder_trades_a_pass_fetches_the_site_and_sends_it_back_for_iPros_staff()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var email = new RecordingEmail();
        var seeded = await SeedAsync(db, blob);
        await BuilderBeta.SetAsync(db, seeded.Site.AgentUserId, enabled: true, adviserPublishes: false, "owner");
        var pass = await BuilderBeta.IssuePassAsync(db, seeded.Site, false, DateTime.UtcNow);

        // 1. The pass, traded for a session: the builder learns whose site it is and how it looks.
        var opened = Body(await Api(db, blob, email, pass).Open());
        var token = opened["sessionToken"]!.GetValue<string>();
        Assert.Equal((await BuilderBeta.FindAsync(db, seeded.Site.AgentUserId))!.Tenant, opened["tenant"]!.GetValue<string>());
        Assert.False(opened["startFresh"]!.GetValue<bool>());
        // No hand-over yet: the builder opens the site afresh.
        Assert.Equal("", opened["current"]!["handoverId"]!.GetValue<string>());
        Assert.Equal("open", opened["current"]!["state"]!.GetValue<string>());
        Assert.Equal("staff", opened["publishing"]!.GetValue<string>());
        Assert.Equal("Sample Bakery", opened["adviser"]!.GetValue<string>());
        Assert.Equal("https://portal.example.test/portal/Website", opened["returnTo"]!.GetValue<string>());
        Assert.Matches("^#[0-9a-fA-F]{6}$", opened["look"]!["accent"]!.GetValue<string>());
        // 613: what the builder's iPro bar shows: the site, the address its clients see (no custom domain
        // here, so the free one), the portal's default colour, and Help at the builder's section of the guide.
        var free = await db.AgentUsers.Where(a => a.Id == seeded.Site.AgentUserId).Select(a => a.DomainName).SingleAsync();
        Assert.Equal("Sample Bakery", opened["site"]!.GetValue<string>());
        Assert.Equal(free, opened["address"]!.GetValue<string>());
        Assert.Equal("#1457d9", opened["accent"]!.GetValue<string>());
        Assert.Equal("https://portal.example.test/portal/Support/Article/website-builder#try-the-new-builder-beta", opened["help"]!.GetValue<string>());
        // 614: and what the builder needs to draw the page in the site's own design.
        var owner = await db.AgentUsers.AsNoTracking().SingleAsync(a => a.Id == seeded.Site.AgentUserId);
        var siteLook = opened["look"]!;
        Assert.Equal("modern-professional", siteLook["design"]!.GetValue<string>());
        Assert.Equal("#07142f", siteLook["vars"]!["--site-theme-deep"]!.GetValue<string>());
        Assert.Matches("^#[0-9a-f]{6}$", siteLook["vars"]!["--site-theme-ink"]!.GetValue<string>());
        Assert.Equal("82px", siteLook["vars"]!["--section-space"]!.GetValue<string>());
        Assert.Matches("^header-[a-z]+ hero-[a-z-]+ hero-style-[a-z]+ buttons-[a-z]+$", siteLook["bodyClass"]!.GetValue<string>());
        Assert.Equal("style-standard logo-left logo-size-medium", siteLook["headerClass"]!.GetValue<string>());
        Assert.Equal(owner.CompanyName, siteLook["company"]!.GetValue<string>());
        Assert.Equal(BuilderBeta.Monogram(owner), siteLook["monogram"]!.GetValue<string>());
        Assert.Equal($"{owner.FirstName} {owner.LastName}".Trim(), siteLook["contact"]!["name"]!.GetValue<string>());
        Assert.Equal("Sample Bakery", siteLook["copyright"]!.GetValue<string>());
        Assert.IsType<UnauthorizedObjectResult>(await Api(db, blob, email, pass).Open());

        // 2. The site as it is now, as a hand-over made for the customer.
        var file = Assert.IsType<FileContentResult>(await Api(db, blob, email, token).Handover());
        var handover = await db.WebsiteBuilderHandovers.AsNoTracking().SingleAsync();
        Assert.Equal(BuilderBeta.CustomerIssuer, handover.IssuedBy);
        Assert.Equal(handover.HandoverId, (await db.WebsiteBuilderSessions.AsNoTracking().SingleAsync(s => s.Kind == WebsiteBuilderSessionKinds.Session)).HandoverId);
        Assert.IsType<JsonResult>(await Api(db, blob, email, token).Check());

        // 3. A package of anything but this hand-over is not this session's to send.
        var designer = BuilderImport571Tests.Zip(BuilderImport571Tests.Designer());
        var refused = Assert.IsType<UnprocessableEntityObjectResult>(await Api(db, blob, email, token, designer).Return());
        Assert.Contains("earlier visit", JsonSerializer.Serialize(refused.Value));

        // 4. The return waits for iPro's staff, who hear of it; nothing on the site changes.
        var first = SiteHandoverReturn572Tests.Return(file.FileContents, t => SiteHandoverReturn572Tests.Block(t, "about", 0)["heading"] = "About the bakery");
        var sent = Assert.IsType<ObjectResult>(await Api(db, blob, email, token, first).Return());
        Assert.Equal(202, sent.StatusCode);
        Assert.Equal("staff", Body(sent)["publishing"]!.GetValue<string>());
        var waiting = await db.WebsiteBuilderImports.AsNoTracking().SingleAsync();
        Assert.Equal(WebsiteBuilderImportSources.Customer, waiting.Source);
        Assert.Equal(WebsiteBuilderImportStatus.Pending, waiting.Status);
        Assert.Equal("About us", await db.WebsiteContentBlocks.Where(b => b.WebsitePage.Slug == "about").Select(b => b.Heading).SingleAsync());
        Assert.Contains(email.Sent, m => m.To == "staff@example.test" && m.Subject.Contains("Sample Bakery"));

        // 5. Sending again replaces what was waiting; a look is dropped by the next send.
        var look = Body(await Api(db, blob, email, token, first).Preview("about"));
        Assert.Contains("/PublicWebsite/PreviewBuilderImport?id=", look["previewUrl"]!.GetValue<string>());
        Assert.EndsWith("&page=about", look["previewUrl"]!.GetValue<string>());
        // A look is stored as one, with its key, in the same write: never a SuperAdmin's upload for a moment.
        var stored = await db.WebsiteBuilderImports.AsNoTracking().OrderBy(i => i.Id).LastAsync();
        Assert.Equal((WebsiteBuilderImportStatus.Preview, WebsiteBuilderImportSources.Customer), (stored.Status, stored.Source));
        Assert.Contains($"key={stored.PreviewKey}", look["previewUrl"]!.GetValue<string>());
        var second = SiteHandoverReturn572Tests.Return(file.FileContents, t => SiteHandoverReturn572Tests.Block(t, "about", 0)["heading"] = "Our story");
        Assert.Equal(202, Assert.IsType<ObjectResult>(await Api(db, blob, email, token, second).Return()).StatusCode);
        var rows = await db.WebsiteBuilderImports.AsNoTracking().OrderBy(i => i.Id).ToListAsync();
        Assert.Equal(new[] { WebsiteBuilderImportStatus.Discarded, WebsiteBuilderImportStatus.Discarded, WebsiteBuilderImportStatus.Pending },
            rows.Select(r => r.Status).ToArray());
        Assert.Contains("Our story", rows[2].TemplateJson);

        // 6. Switched off: the builder's next question is answered no.
        await BuilderBeta.SetAsync(db, seeded.Site.AgentUserId, enabled: false, adviserPublishes: false, "owner");
        db.ChangeTracker.Clear();
        Assert.IsType<UnauthorizedObjectResult>(await Api(db, blob, email, token).Check());
        Assert.IsType<UnauthorizedObjectResult>(await Api(db, blob, email, token, second).Return());
    }

    [Fact]
    public async Task An_adviser_who_publishes_reviews_publishes_keeps_what_they_added_and_can_put_it_back()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var email = new RecordingEmail();
        var seeded = await SeedAsync(db, blob);
        await BuilderBeta.SetAsync(db, seeded.Site.AgentUserId, enabled: true, adviserPublishes: true, "owner");
        var token = await SessionAsync(db, seeded.Site);
        var file = Assert.IsType<FileContentResult>(await Api(db, blob, email, token).Handover());

        // While the draft is open, the adviser adds a section to About in the page editor.
        var aboutId = await db.WebsitePages.Where(p => p.AgentWebsiteId == seeded.Site.Id && p.Slug == "about").Select(p => p.Id).SingleAsync();
        db.WebsiteContentBlocks.Add(new WebsiteContentBlock { WebsitePageId = aboutId, BlockType = WebsiteBlockTypes.Text, Heading = "Opening hours", Body = "Every day.", SortOrder = 1 });
        await db.SaveChangesAsync();
        var state = await BuilderBeta.StateAsync(db, seeded.Site, DateTime.UtcNow);
        Assert.NotNull(state.OpenDraft);

        var sent = Assert.IsType<ObjectResult>(await Api(db, blob, email, token,
            SiteHandoverReturn572Tests.Return(file.FileContents, t => SiteHandoverReturn572Tests.Block(t, "about", 0)["heading"] = "About the bakery")).Return());
        var answer = Body(sent);
        Assert.Equal("adviser", answer["publishing"]!.GetValue<string>());
        var importId = await db.WebsiteBuilderImports.Select(i => i.Id).SingleAsync();
        Assert.Equal($"https://portal.example.test/portal/WebsiteBuilder/Review/{importId}", answer["reviewUrl"]!.GetValue<string>());
        Assert.Empty(email.Sent);

        // Another adviser, with the Beta on and publishing their own, reaches nothing of it. (The seed
        // gives every adviser one address; the first one's is moved aside.)
        await db.AgentUsers.Where(a => a.Id == seeded.Site.AgentUserId).ExecuteUpdateAsync(a => a.SetProperty(x => x.Email, "first-adviser@example.com"));
        var other = await SeedAsync(db, new BuilderImport571Tests.MemoryBlob());
        await BuilderBeta.SetAsync(db, other.Site.AgentUserId, enabled: true, adviserPublishes: true, "owner");
        Assert.IsType<NotFoundResult>(await Portal(db, blob, other.Site.AgentUserId).Review(importId));
        Assert.IsType<NotFoundResult>(await Portal(db, blob, other.Site.AgentUserId).Publish(importId, "x"));
        Assert.IsType<NotFoundResult>(await Portal(db, blob, other.Site.AgentUserId).Discard(importId));

        var review = Assert.IsType<ViewResult>(await Portal(db, blob, seeded.Site.AgentUserId).Review(importId));
        var model = Assert.IsType<WebsiteBuilderReviewViewModel>(review.Model);
        var html = await RenderPortalAsync("~/Views/WebsiteBuilder/Review.cshtml", model);
        Assert.Contains($"action=\"/portal/WebsiteBuilder/Publish/{importId}\"", html);
        Assert.Contains($"name=\"plan\" value=\"{model.Plan.Fingerprint}\"", html);
        Assert.Contains("You also changed this page in the page editor after you opened the builder.", html);
        Assert.Contains("Kept from the page editor, at the end of the page:", html);
        Assert.Contains("Opening hours", html);
        var about = model.Plan.Pages.Single(p => p.Page.Slug == "about");
        Assert.True(about.ChangedSinceHandover);
        Assert.Contains(about.SectionsGoing, g => g.Title == "Opening hours" && g.Kept);

        Assert.IsType<RedirectToActionResult>(await Portal(db, blob, seeded.Site.AgentUserId).Publish(importId, model.Plan.Fingerprint));
        db.ChangeTracker.Clear();
        var headings = await db.WebsiteContentBlocks.Where(b => b.WebsitePageId == aboutId).OrderBy(b => b.SortOrder).Select(b => b.Heading).ToListAsync();
        Assert.Equal(new[] { "About the bakery", "Opening hours" }, headings);
        state = await BuilderBeta.StateAsync(db, seeded.Site, DateTime.UtcNow);
        Assert.Null(state.OpenDraft);
        Assert.NotNull(state.PutBack);
        Assert.True(state.HasDraft);
        Assert.Equal(BuilderBeta.HandoverStates.Published, (await BuilderBeta.CurrentAsync(db, seeded.Site.Id)).State);

        // The builder still open on that draft cannot send it a second time: the changes are on the site.
        var twice = Assert.IsType<UnprocessableEntityObjectResult>(await Api(db, blob, email, token,
            SiteHandoverReturn572Tests.Return(file.FileContents, t => SiteHandoverReturn572Tests.Block(t, "about", 0)["heading"] = "Again")).Return());
        Assert.Contains("already went onto your website", JsonSerializer.Serialize(twice.Value));
        Assert.IsType<UnprocessableEntityObjectResult>(await Api(db, blob, email, token, SiteHandoverReturn572Tests.Return(file.FileContents, _ => { })).Preview(null));

        // Put back, also with the Beta switched off since.
        await BuilderBeta.SetAsync(db, seeded.Site.AgentUserId, enabled: false, adviserPublishes: false, "owner");
        Assert.IsType<RedirectToActionResult>(await Portal(db, blob, seeded.Site.AgentUserId).PutBack());
        db.ChangeTracker.Clear();
        Assert.Equal(WebsiteBuilderImportStatus.Restored, (await db.WebsiteBuilderImports.AsNoTracking().SingleAsync()).Status);
        var putBack = await BuilderBeta.CurrentAsync(db, seeded.Site.Id);
        Assert.Equal(BuilderBeta.HandoverStates.PutBack, putBack.State);
        Assert.NotNull(putBack.At);
        Assert.Equal(new[] { "About us", "Opening hours" },
            await db.WebsiteContentBlocks.Where(b => b.WebsitePage.AgentWebsiteId == seeded.Site.Id && b.WebsitePage.Slug == "about").OrderBy(b => b.SortOrder).Select(b => b.Heading).ToListAsync());
    }

    [Fact]
    public async Task What_waits_for_iPros_staff_is_not_the_advisers_to_review()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedAsync(db, blob);
        await BuilderBeta.SetAsync(db, seeded.Site.AgentUserId, enabled: true, adviserPublishes: false, "owner");
        var token = await SessionAsync(db, seeded.Site);
        var file = Assert.IsType<FileContentResult>(await Api(db, blob, new RecordingEmail(), token).Handover());
        await Api(db, blob, new RecordingEmail(), token, SiteHandoverReturn572Tests.Return(file.FileContents, _ => { })).Return();
        var importId = await db.WebsiteBuilderImports.Select(i => i.Id).SingleAsync();
        Assert.IsType<NotFoundResult>(await Portal(db, blob, seeded.Site.AgentUserId).Review(importId));
        Assert.IsType<NotFoundResult>(await Portal(db, blob, seeded.Site.AgentUserId).Publish(importId, "x"));
    }

    [Fact]
    public async Task Deleting_an_adviser_asks_the_builder_to_erase_their_draft_until_it_has()
    {
        // The ledger guard as production has it: without it the adviser's retained billing rows would
        // cascade away and the eraser rightly refuses to commit.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: true);
        await using var db = testDb.CreateContext();
        var seeded = await SeedAsync(db, new BuilderImport571Tests.MemoryBlob());
        var tenant = (await BuilderBeta.SetAsync(db, seeded.Site.AgentUserId, true, false, "owner")).Tenant;
        await SessionAsync(db, seeded.Site);

        await AgentDataEraser.EraseAsync(db, seeded.Site.AgentUserId);
        db.ChangeTracker.Clear();
        Assert.False(await db.AgentBuilderBetas.AnyAsync());
        Assert.False(await db.WebsiteBuilderSessions.AnyAsync());
        Assert.Equal(tenant, (await db.BuilderTenantErasures.SingleAsync()).Tenant);

        var builder = new FakeBuilder { Status = HttpStatusCode.ServiceUnavailable };
        await Job(db, builder).RunAsync();
        db.ChangeTracker.Clear();
        var owed = await db.BuilderTenantErasures.SingleAsync();
        Assert.Equal(1, owed.Attempts);
        Assert.Contains("503", owed.LastError);

        builder.Status = HttpStatusCode.OK;
        await Job(db, builder).RunAsync();
        db.ChangeTracker.Clear();
        Assert.False(await db.BuilderTenantErasures.AnyAsync());
        Assert.Equal("service-key-for-tests-0123456789abcdef", builder.LastKey);
        Assert.Contains(tenant, builder.LastBody);
    }

    [Fact]
    public void Every_starter_banner_is_known_by_its_content()
    {
        var folder = Path.Combine(RepoRoot(), "src", "IPRO.Web", "wwwroot", "images", "starter-banners");
        Assert.Equal(WebsiteStarterBannerCatalog.All.Count, WebsiteStarterBannerHashes.FileBySha256.Count);
        foreach (var banner in WebsiteStarterBannerCatalog.All)
        {
            var sha = BuilderPackageReader.Hex(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, banner.FileName))));
            Assert.Equal(banner.Url, WebsiteStarterBannerHashes.UrlFor(sha));
        }
    }

    [Fact]
    public async Task A_starter_banner_picked_in_the_builder_is_linked_not_stored_again()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedAsync(db, blob);
        var token = await SessionAsync(db, seeded.Site);
        var file = Assert.IsType<FileContentResult>(await Api(db, blob, new RecordingEmail(), token).Handover());
        var banner = WebsiteStarterBannerCatalog.All.First(b => b.FileName == "blue-sky.jpg");
        var bytes = File.ReadAllBytes(Path.Combine(RepoRoot(), "src", "IPRO.Web", "wwwroot", "images", "starter-banners", banner.FileName));
        var mediaBefore = await db.WebsiteMediaAssets.CountAsync();
        var blobsBefore = blob.Files.Count;

        var sent = SiteHandoverReturn572Tests.Return(file.FileContents, t =>
        {
            SiteHandoverReturn572Tests.Block(t, "about", 0)["imageRef"] = "assets/blue-sky.jpg";
            t["assets"]!.AsArray().Add(new JsonObject { ["ref"] = "assets/blue-sky.jpg", ["type"] = "image/jpeg" });
        }, new[] { ("assets/blue-sky.jpg", bytes) });
        Assert.Equal(202, Assert.IsType<ObjectResult>(await Api(db, blob, new RecordingEmail(), token, sent).Return()).StatusCode);

        var import = await db.WebsiteBuilderImports.AsNoTracking().SingleAsync();
        Assert.Equal(0, import.NewImageCount);
        Assert.Equal(mediaBefore, await db.WebsiteMediaAssets.CountAsync());
        Assert.Equal(blobsBefore, blob.Files.Count);
        Assert.Contains(BuilderImportService.StoredImages(import), i => i.Ref == "assets/blue-sky.jpg" && i.Url == banner.Url && !i.Created);
    }

    [Fact]
    public async Task The_My_Website_card_says_where_the_adviser_is()
    {
        var beta = new AgentBuilderBeta { AgentUserId = 7, Enabled = true, Tenant = "t_" + new string('a', 32) };
        var draft = new WebsiteBuilderHandover { HandoverId = "h_0123456789abcdef", IssuedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc) };
        var waiting = new WebsiteBuilderImport { Id = 41, CreatedAt = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc), Source = WebsiteBuilderImportSources.Customer };
        var applied = new WebsiteBuilderImport { Id = 42, AppliedAt = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc), Source = WebsiteBuilderImportSources.Customer };
        Task<string> Card(BuilderBeta.State state, bool available = true, params string[] changed) =>
            RenderPortalAsync("~/Views/Website/_BuilderBetaCard.cshtml", new WebsiteBuilderCardViewModel(state, available, changed, BuilderBeta.KeepDays));

        var fresh = await Card(new BuilderBeta.State(beta, null, null, null));
        Assert.Contains("Try the new builder (Beta)", fresh);
        Assert.Contains("action=\"/portal/WebsiteBuilder/Open\"", fresh);
        Assert.DoesNotContain("Continue my draft", fresh);
        Assert.Contains("made for a computer", fresh);
        Assert.Contains("90 days", fresh);

        var open = await Card(new BuilderBeta.State(beta, draft, null, null) { HasDraft = true });
        Assert.Contains("Continue my draft", open);
        Assert.Contains("name=\"startFresh\" value=\"true\"", open);
        Assert.Contains("The draft you have is kept there, under Versions.", open);
        Assert.Contains("October 1, 2026", open);
        Assert.Contains("a section you add in the page editor meanwhile is kept", open);
        Assert.DoesNotContain("Your draft still has the changes you put back", open);

        // 612: a package put back after the draft was made from the site with it on.
        var afterPutBack = await Card(new BuilderBeta.State(beta, draft, null, null) { HasDraft = true, PutBackSinceDraft = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc) });
        Assert.Contains("Your draft still has the changes you put back on October 4, 2026.", afterPutBack);
        Assert.Contains("use <strong>Start again from my live site</strong>", afterPutBack);

        // A draft whose changes went onto the website: still the adviser's in the builder, offered as such.
        var kept = await Card(new BuilderBeta.State(beta, null, null, applied) { HasDraft = true });
        Assert.Contains("Continue my draft", kept);
        Assert.DoesNotContain("Try the new builder", kept);

        var staff = await Card(new BuilderBeta.State(beta, draft, waiting, null) { HasDraft = true });
        Assert.Contains("iPro's team is checking it and will publish it", staff);
        Assert.DoesNotContain("Review and publish", staff);
        Assert.Contains("What you sent and is waiting is withdrawn.", staff);

        beta.AdviserPublishes = true;
        var own = await Card(new BuilderBeta.State(beta, draft, waiting, null) { HasDraft = true });
        Assert.Contains("href=\"/portal/WebsiteBuilder/Review/41\"", own);
        Assert.Contains("action=\"/portal/WebsiteBuilder/Discard/41\"", own);

        var putBack = await Card(new BuilderBeta.State(beta, null, null, applied), true, "About", "Contact");
        Assert.Contains("action=\"/portal/WebsiteBuilder/PutBack\"", putBack);
        Assert.Contains("since October 3, 2026", putBack);
        Assert.Contains("what you changed since on: About, Contact", putBack);
        Assert.Contains("its pages, its logo and its design", putBack);

        beta.Enabled = false;
        var off = await Card(new BuilderBeta.State(beta, null, null, applied));
        Assert.Contains("Put my previous site back", off);
        Assert.DoesNotContain("Try the new builder", off);
        // Switched off while something waits: it still says so, and that iPro's team has it.
        var offWaiting = await Card(new BuilderBeta.State(beta, null, waiting, null));
        Assert.Contains("You sent your site from the builder on October 2, 2026. iPro's team is checking it", offWaiting);
        Assert.DoesNotContain("Review and publish", offWaiting);
        Assert.DoesNotContain("/portal/WebsiteBuilder/Open", offWaiting);

        beta.Enabled = true;
        var unavailable = await Card(new BuilderBeta.State(beta, null, null, null), available: false);
        Assert.Contains("not available here yet", unavailable);
        Assert.DoesNotContain("/portal/WebsiteBuilder/Open", unavailable);
    }

    [Fact]
    public async Task The_page_that_carries_the_pass_posts_it_and_puts_it_in_no_address()
    {
        var page = await RenderPortalAsync("~/Views/WebsiteBuilder/Open.cshtml", new WebsiteBuilderOpenViewModel("https://builder.example.test/open", "PASS"));
        Assert.Contains("<form id=\"openBuilderForm\" method=\"post\" action=\"https://builder.example.test/open\">", page);
        Assert.Contains("<input type=\"hidden\" name=\"pass\" value=\"PASS\" />", page);
        Assert.Contains("document.getElementById(\"openBuilderForm\").submit();", page);
        Assert.DoesNotContain("?pass=", page);
    }

    [Fact]
    public async Task The_doors_answer_only_the_builders_server()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedAsync(db, blob);
        await BuilderBeta.SetAsync(db, seeded.Site.AgentUserId, enabled: true, adviserPublishes: false, "owner");
        var pass = await BuilderBeta.IssuePassAsync(db, seeded.Site, false, DateTime.UtcNow);
        var token = await SessionAsync(db, seeded.Site);

        foreach (var key in new[] { null, "", "wrong-key-wrong-key-wrong-key-wrong-key" })
        {
            Assert.IsType<NotFoundResult>(await Api(db, blob, new RecordingEmail(), pass, key: key).Open());
            Assert.IsType<NotFoundResult>(await Api(db, blob, new RecordingEmail(), token, key: key).Check());
            Assert.IsType<NotFoundResult>(await Api(db, blob, new RecordingEmail(), token, key: key).Handover());
            Assert.IsType<NotFoundResult>(await Api(db, blob, new RecordingEmail(), token, Array.Empty<byte>(), key: key).Return());
            Assert.IsType<NotFoundResult>(await Api(db, blob, new RecordingEmail(), token, Array.Empty<byte>(), key: key).Preview(null));
        }
        // Without a key of iPro's own, no caller has one.
        Assert.IsType<NotFoundResult>(await Api(db, blob, new RecordingEmail(), pass, configuredKey: "short").Open());
        // None of that spent the pass.
        Assert.IsType<JsonResult>(await Api(db, blob, new RecordingEmail(), pass).Open());
    }

    [Fact]
    public async Task A_session_ends_when_the_package_no_longer_includes_the_website()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedAsync(db, blob);
        var token = await SessionAsync(db, seeded.Site);
        Assert.IsType<JsonResult>(await Api(db, blob, new RecordingEmail(), token).Check());

        var lapsed = Assert.IsType<UnauthorizedObjectResult>(await Api(db, blob, new RecordingEmail(), token, entitlements: new Lapsed()).Check());
        Assert.Contains("needs attention under Billing", JsonSerializer.Serialize(lapsed.Value));
        Assert.IsType<UnauthorizedObjectResult>(await Api(db, blob, new RecordingEmail(), token, entitlements: new Lapsed()).Handover());
        var noWebsite = Assert.IsType<UnauthorizedObjectResult>(await Api(db, blob, new RecordingEmail(), token, entitlements: new Lapsed(gated: false)).Check());
        Assert.Contains("does not include a website", JsonSerializer.Serialize(noWebsite.Value));

        // A pass traded while it is so opens nothing, and the session made for it is ended at once.
        var pass = await BuilderBeta.IssuePassAsync(db, seeded.Site, false, DateTime.UtcNow);
        Assert.IsType<UnauthorizedObjectResult>(await Api(db, blob, new RecordingEmail(), pass, entitlements: new Lapsed()).Open());
        Assert.Equal(1, await db.WebsiteBuilderSessions.CountAsync(s => s.Kind == WebsiteBuilderSessionKinds.Session && s.EndedAt != null));
    }

    [Fact]
    public async Task Starting_again_withdraws_what_was_sent_of_the_draft_before()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedAsync(db, blob);
        var token = await SessionAsync(db, seeded.Site);
        var file = Assert.IsType<FileContentResult>(await Api(db, blob, new RecordingEmail(), token).Handover());
        Assert.Equal(202, Assert.IsType<ObjectResult>(await Api(db, blob, new RecordingEmail(), token,
            SiteHandoverReturn572Tests.Return(file.FileContents, t => SiteHandoverReturn572Tests.Block(t, "about", 0)["heading"] = "Sent first")).Return()).StatusCode);
        Assert.NotNull((await BuilderBeta.StateAsync(db, seeded.Site, DateTime.UtcNow)).Waiting);

        // "Start again from my live site": a new hand-over, and what waited of the old one is withdrawn.
        var again = await BuilderBeta.IssuePassAsync(db, seeded.Site, startFresh: true, DateTime.UtcNow);
        var fresh = Body(await Api(db, blob, new RecordingEmail(), again).Open())["sessionToken"]!.GetValue<string>();
        Assert.IsType<FileContentResult>(await Api(db, blob, new RecordingEmail(), fresh).Handover());
        db.ChangeTracker.Clear();
        Assert.Equal(WebsiteBuilderImportStatus.Discarded, (await db.WebsiteBuilderImports.AsNoTracking().SingleAsync()).Status);
        Assert.Null((await BuilderBeta.StateAsync(db, seeded.Site, DateTime.UtcNow)).Waiting);
        // The newest hand-over is open; the old session can send nothing of the old one.
        Assert.Equal(BuilderBeta.HandoverStates.Open, (await BuilderBeta.CurrentAsync(db, seeded.Site.Id)).State);
        var old = Assert.IsType<UnprocessableEntityObjectResult>(await Api(db, blob, new RecordingEmail(), token,
            SiteHandoverReturn572Tests.Return(file.FileContents, _ => { })).Return());
        Assert.Contains("This site was handed over as", JsonSerializer.Serialize(old.Value));
    }

    // 612 (the owner's staging try): package A went onto the site, the builder opened the site afresh with A
    // on it (the open draft), then A was put back. The card hears of it; a put-back the draft was not made
    // over (one put back before the draft, or a package that went on after it) says nothing.
    [Fact]
    public async Task The_card_knows_when_the_open_draft_still_has_changes_that_were_put_back()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seeded = await SeedAsync(db, new BuilderImport571Tests.MemoryBlob());
        var site = seeded.Site;
        var now = DateTime.UtcNow;
        await BuilderBeta.SetAsync(db, site.AgentUserId, enabled: true, adviserPublishes: false, "owner");
        await db.AgentBuilderBetas.Where(b => b.AgentUserId == site.AgentUserId).ExecuteUpdateAsync(s => s.SetProperty(b => b.LastOpenedAt, now));
        var drafted = now.AddHours(-2);
        db.WebsiteBuilderHandovers.Add(new WebsiteBuilderHandover
        {
            AgentUserId = site.AgentUserId, AgentWebsiteId = site.Id, HandoverId = "h_draftdraftdraft01", IssuedAt = drafted,
            IssuedBy = BuilderBeta.CustomerIssuer, SessionExpiresAt = drafted.AddHours(1)
        });
        WebsiteBuilderImport Import(string handover, DateTime applied, DateTime? closed, string status) => new()
        {
            AgentUserId = site.AgentUserId, AgentWebsiteId = site.Id, HandoverId = handover, Status = status,
            AppliedAt = applied, ClosedAt = closed, Source = WebsiteBuilderImportSources.Customer, CreatedAt = applied
        };
        db.WebsiteBuilderImports.Add(Import("h_earlierearlier01", drafted.AddDays(-3), drafted.AddDays(-2), WebsiteBuilderImportStatus.Restored));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Null((await BuilderBeta.StateAsync(db, site, now)).PutBackSinceDraft);

        var putBackAt = drafted.AddHours(1);
        db.WebsiteBuilderImports.Add(Import("h_packageapackage1", drafted.AddHours(-1), putBackAt, WebsiteBuilderImportStatus.Restored));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var state = await BuilderBeta.StateAsync(db, site, now);
        Assert.Equal("h_draftdraftdraft01", state.OpenDraft?.HandoverId);
        Assert.NotNull(state.PutBackSinceDraft);
        Assert.True(Math.Abs((state.PutBackSinceDraft!.Value - putBackAt).TotalSeconds) < 1);
    }

    // 613: the builder's bar is drawn in the adviser's own portal colour, one of the portal's six; anything
    // else in the column (an old value, a typing slip) gives the portal's default rather than reaching a page.
    [Theory]
    [InlineData("#D9541F", "#d9541f")]
    [InlineData("#5b2f9e", "#5b2f9e")]
    [InlineData("#123456", "#1457d9")]
    [InlineData("red;}", "#1457d9")]
    [InlineData(null, "#1457d9")]
    public async Task The_builder_gets_the_advisers_own_portal_colour(string? stored, string sent)
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedAsync(db, blob);
        await db.AgentUsers.Where(a => a.Id == seeded.Site.AgentUserId).ExecuteUpdateAsync(s => s.SetProperty(a => a.PortalAccentColor, stored));
        await BuilderBeta.SetAsync(db, seeded.Site.AgentUserId, enabled: true, adviserPublishes: false, "owner");
        var pass = await BuilderBeta.IssuePassAsync(db, seeded.Site, false, DateTime.UtcNow);

        var opened = Body(await Api(db, blob, new RecordingEmail(), pass).Open());

        Assert.Equal(sent, opened["accent"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_look_writes_nothing_to_the_site_and_shows_what_publishing_keeps()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedAsync(db, blob);
        var token = await SessionAsync(db, seeded.Site);
        var file = Assert.IsType<FileContentResult>(await Api(db, blob, new RecordingEmail(), token).Handover());
        // Meanwhile, a section added in the page editor: publishing keeps it, so the look shows it.
        var aboutId = await db.WebsitePages.Where(p => p.AgentWebsiteId == seeded.Site.Id && p.Slug == "about").Select(p => p.Id).SingleAsync();
        var added = new WebsiteContentBlock { WebsitePageId = aboutId, BlockType = WebsiteBlockTypes.Text, Heading = "Opening hours", Body = "Every day.", SortOrder = 1 };
        db.WebsiteContentBlocks.Add(added);
        await db.SaveChangesAsync();
        var media = await db.WebsiteMediaAssets.CountAsync();
        var files = blob.Files.Count;

        var withPicture = SiteHandoverReturn572Tests.Return(file.FileContents, t =>
        {
            SiteHandoverReturn572Tests.Block(t, "about", 0)["imageRef"] = "assets/new-look.png";
            t["assets"]!.AsArray().Add(new JsonObject { ["ref"] = "assets/new-look.png", ["type"] = "image/png" });
        }, new[] { ("assets/new-look.png", SiteHandoverReturn572Tests.Png(77)) });
        var look = Body(await Api(db, blob, new RecordingEmail(), token, withPicture).Preview("about"))["previewUrl"]!.GetValue<string>();
        Assert.Contains($"&sections={added.Id}&page=about", look);
        // Its new picture is a file of its own, not a row of the media library.
        Assert.Equal(media, await db.WebsiteMediaAssets.CountAsync());
        Assert.Equal(files + 1, blob.Files.Count);

        // Dropped (the next look, a send, or its hour over), its file goes with it.
        var importId = await db.WebsiteBuilderImports.Select(i => i.Id).SingleAsync();
        await BuilderImportService.DropStalePreviewsAsync(db, blob, DateTime.UtcNow.AddHours(2));
        Assert.Equal(WebsiteBuilderImportStatus.Discarded, (await db.WebsiteBuilderImports.AsNoTracking().SingleAsync(i => i.Id == importId)).Status);
        Assert.Equal(files, blob.Files.Count);
    }

    [Fact]
    public async Task A_picture_two_sends_share_leaves_with_the_last_of_them()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedAsync(db, blob);
        var token = await SessionAsync(db, seeded.Site);
        var file = Assert.IsType<FileContentResult>(await Api(db, blob, new RecordingEmail(), token).Handover());
        var media = await db.WebsiteMediaAssets.CountAsync();
        byte[] Sent(string heading) => SiteHandoverReturn572Tests.Return(file.FileContents, t =>
        {
            SiteHandoverReturn572Tests.Block(t, "about", 0)["heading"] = heading;
            SiteHandoverReturn572Tests.Block(t, "about", 0)["imageRef"] = "assets/shared.png";
            t["assets"]!.AsArray().Add(new JsonObject { ["ref"] = "assets/shared.png", ["type"] = "image/png" });
        }, new[] { ("assets/shared.png", SiteHandoverReturn572Tests.Png(91)) });

        // The first send stores the picture; the second links it, and the first is dropped by it.
        await Api(db, blob, new RecordingEmail(), token, Sent("One")).Return();
        await Api(db, blob, new RecordingEmail(), token, Sent("Two")).Return();
        Assert.Equal(media + 1, await db.WebsiteMediaAssets.CountAsync());
        var second = await db.WebsiteBuilderImports.SingleAsync(i => i.Status == WebsiteBuilderImportStatus.Pending);
        Assert.Contains(BuilderImportService.StoredImages(second), i => i.Ref == "assets/shared.png" && i.Created);

        // Dropping the second, the last that uses it, takes the picture back out of the media library.
        Assert.True(await BuilderImportService.DiscardAsync(db, blob, second));
        Assert.Equal(media, await db.WebsiteMediaAssets.CountAsync());
    }

    [Fact]
    public async Task The_tables_the_repair_creates_are_the_tables_the_model_expects()
    {
        // The test database is built from the model; production's is built by the start-up repair. (The
        // ledger guard as production has it, so the eraser at the end may commit.)
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: true);
        await using var db = testDb.CreateContext();
        foreach (var table in new[] { "AgentBuilderBeta", "WebsiteBuilderSessions", "BuilderTenantErasures" })
            await db.Database.ExecuteSqlRawAsync($"DROP TABLE `{table}`");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE `WebsiteBuilderImports` DROP COLUMN `Source`");
        await StartupSchemaRepair.EnsureWebsiteBuilderImportSchemaAsync(db);
        await StartupSchemaRepair.EnsureWebsiteBuilderImportSchemaAsync(db);   // and it can run again
        // A table made before the last visit was kept gains it.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE `AgentBuilderBeta` DROP COLUMN `LastOpenedAt`");
        await StartupSchemaRepair.EnsureWebsiteBuilderImportSchemaAsync(db);

        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedAsync(db, blob);
        var token = await SessionAsync(db, seeded.Site);
        var file = Assert.IsType<FileContentResult>(await Api(db, blob, new RecordingEmail(), token).Handover());
        Assert.Equal(202, Assert.IsType<ObjectResult>(await Api(db, blob, new RecordingEmail(), token, SiteHandoverReturn572Tests.Return(file.FileContents, _ => { })).Return()).StatusCode);
        Assert.Equal(WebsiteBuilderImportSources.Customer, (await db.WebsiteBuilderImports.AsNoTracking().SingleAsync()).Source);
        await AgentDataEraser.EraseAsync(db, seeded.Site.AgentUserId);
        Assert.Single(await db.BuilderTenantErasures.ToListAsync());
    }

    // 585 (the owner's bench walk-through, 2026-10-19): SuperAdmin listed each "See it on my site" the
    // customer had made, once a newer look or the send dropped it, as a send of theirs that was dropped.
    [Fact]
    public async Task SuperAdmin_lists_what_the_customer_sent_and_none_of_their_looks()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedAsync(db, blob);
        var token = await SessionAsync(db, seeded.Site);
        var file = Assert.IsType<FileContentResult>(await Api(db, blob, new RecordingEmail(), token).Handover());
        byte[] Edited() => SiteHandoverReturn572Tests.Return(file.FileContents, t => SiteHandoverReturn572Tests.Block(t, "about", 0)["heading"] = "Ours");

        // Two looks, then two sends: each look is dropped by what came next, and the first send by the second.
        await Api(db, blob, new RecordingEmail(), token, Edited()).Preview("home");
        await Api(db, blob, new RecordingEmail(), token, Edited()).Preview("about");
        await Api(db, blob, new RecordingEmail(), token, Edited()).Return();
        await Api(db, blob, new RecordingEmail(), token, Edited()).Return();
        Assert.Equal(3, await db.WebsiteBuilderImports.CountAsync(i => i.Status == WebsiteBuilderImportStatus.Discarded));

        var (admin, _) = Admin(db, blob, new RecordingEmail());
        var model = Assert.IsType<IPRO.Admin.Models.BuilderImportIndexViewModel>(Assert.IsType<ViewResult>(await admin.Index(seeded.Site.AgentUserId)).Model);
        Assert.Equal(new[] { WebsiteBuilderImportStatus.Pending, WebsiteBuilderImportStatus.Discarded }, model.Imports.Select(i => i.Status));
        Assert.All(model.Imports, i => Assert.Equal(WebsiteBuilderImportSources.SentFileName, i.FileName));
        // The first send was replaced by the customer's own newer send, not dropped by iPro's staff: said so.
        Assert.True(model.ReplacedBySend(model.Imports[1]));
        Assert.False(model.ReplacedBySend(model.Imports[0]));
        var html = await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Index.cshtml", model);
        Assert.Contains("Replaced</span>", html);
        Assert.Contains("by the customer's newer send", html);
        Assert.DoesNotContain(">Dropped<", html);
    }

    [Fact]
    public async Task SuperAdmin_makes_no_hand_over_while_the_beta_is_on_and_tells_the_customer_what_it_drops()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var email = new RecordingEmail();
        var seeded = await SeedAsync(db, blob);
        var agentId = seeded.Site.AgentUserId;
        await BuilderBeta.SetAsync(db, agentId, enabled: true, adviserPublishes: false, "owner");

        // The customer's site is handed over to them, in the builder: not here.
        var (admin, temp) = Admin(db, blob, email);
        Assert.IsType<RedirectToActionResult>(await admin.Handover(agentId));
        Assert.Contains("has the new builder (Beta) on", (string)temp["Error"]!);
        Assert.False(await db.WebsiteBuilderHandovers.AnyAsync());

        var token = await SessionAsync(db, seeded.Site);
        var file = Assert.IsType<FileContentResult>(await Api(db, blob, email, token).Handover());
        await Api(db, blob, email, token, SiteHandoverReturn572Tests.Return(file.FileContents, _ => { })).Return();
        var importId = await db.WebsiteBuilderImports.Select(i => i.Id).SingleAsync();

        // Switched off with something waiting: the screen says it is still here, for review.
        (admin, temp) = Admin(db, blob, email);
        Assert.IsType<RedirectToActionResult>(await admin.SetBeta(agentId, enabled: false, adviserPublishes: false));
        Assert.Contains("within five minutes", (string)temp["Success"]!);
        Assert.Contains("still waiting below", (string)temp["Success"]!);

        // Dropped: the customer hears that it will not be published.
        (admin, temp) = Admin(db, blob, email);
        Assert.IsType<RedirectToActionResult>(await admin.Discard(importId));
        Assert.Contains("The customer was told by email.", (string)temp["Success"]!);
        Assert.Contains(email.Sent, m => m.To == "handover@example.com" && m.Subject == "Your website changes were not published");
    }

    [Fact]
    public void The_customer_is_told_live_only_when_the_website_is_published()
    {
        var agent = new AgentUser { FirstName = "Bake", Email = "handover@example.com" };
        Assert.Equal("Your website changes are live", BuilderBetaNotice.Published(agent, Configuration(), sitePublished: true).Subject);
        var unpublished = BuilderBetaNotice.Published(agent, Configuration(), sitePublished: false);
        Assert.Equal("Your website changes are in place", unpublished.Subject);
        Assert.Contains("not published yet", unpublished.Text);
        var dropped = BuilderBetaNotice.Dropped(agent, new WebsiteBuilderImport { CreatedAt = new DateTime(2026, 10, 17, 15, 0, 0, DateTimeKind.Utc) }, Configuration());
        Assert.Contains("did not publish the site you sent from the new website builder on October 17, 2026", dropped.Text);
        Assert.Contains("Your draft is still in the builder", dropped.Text);
    }

    [Fact]
    public async Task Only_the_newest_of_what_a_customer_sent_goes_in_and_never_twice_from_one_draft()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedAsync(db, blob);
        var token = await SessionAsync(db, seeded.Site);
        var file = Assert.IsType<FileContentResult>(await Api(db, blob, new RecordingEmail(), token).Handover());
        await Api(db, blob, new RecordingEmail(), token, SiteHandoverReturn572Tests.Return(file.FileContents, t => SiteHandoverReturn572Tests.Block(t, "about", 0)["heading"] = "First")).Return();
        var first = await db.WebsiteBuilderImports.SingleAsync();
        // Two sends at once can both wait (the later one's tidy-up missed the earlier): as if so.
        await Api(db, blob, new RecordingEmail(), token, SiteHandoverReturn572Tests.Return(file.FileContents, t => SiteHandoverReturn572Tests.Block(t, "about", 0)["heading"] = "Second")).Return();
        await db.WebsiteBuilderImports.Where(i => i.Id == first.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, WebsiteBuilderImportStatus.Pending));
        db.ChangeTracker.Clear();

        var older = await db.WebsiteBuilderImports.SingleAsync(i => i.Id == first.Id);
        var refused = await BuilderImportService.ApplyAsync(db, older, new HashSet<int>(), null, "owner");
        Assert.False(refused.Ok);
        Assert.Contains("sent a newer version after this one", refused.Problems.Single());
        db.ChangeTracker.Clear();
        Assert.Equal(WebsiteBuilderImportStatus.Pending, (await db.WebsiteBuilderImports.AsNoTracking().SingleAsync(i => i.Id == first.Id)).Status);

        var newer = await db.WebsiteBuilderImports.SingleAsync(i => i.Id != first.Id);
        Assert.True((await BuilderImportService.ApplyAsync(db, newer, new HashSet<int>(), null, "owner")).Ok);
        db.ChangeTracker.Clear();
        // The older one of the same draft can no longer go in after it.
        var again = await BuilderImportService.ApplyAsync(db, await db.WebsiteBuilderImports.SingleAsync(i => i.Id == first.Id), new HashSet<int>(), null, "owner");
        Assert.False(again.Ok);
        Assert.Contains("Changes from this same draft already went onto the website", again.Problems.Single());
    }

    [Fact]
    public async Task The_builder_opens_only_from_iPros_own_address_and_only_with_its_key()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SeedAsync(db, blob);
        await BuilderBeta.SetAsync(db, seeded.Site.AgentUserId, enabled: true, adviserPublishes: false, "owner");

        // On an adviser's own domain (which answers /portal too) no pass is issued: they are sent home.
        var elsewhere = Portal(db, blob, seeded.Site.AgentUserId);
        elsewhere.ControllerContext.HttpContext.Request.Host = new HostString("www.someadviser.example");
        Assert.IsType<RedirectToActionResult>(await elsewhere.Open());
        Assert.Contains("Sign in at portal.example.test", (string)elsewhere.TempData["Error"]!);
        Assert.False(await db.WebsiteBuilderSessions.AnyAsync());

        var home = Portal(db, blob, seeded.Site.AgentUserId);
        home.ControllerContext.HttpContext.Request.Host = new HostString("portal.example.test");
        Assert.IsType<ViewResult>(await home.Open());
        Assert.Single(await db.WebsiteBuilderSessions.ToListAsync());

        // Without the key the two servers share, the builder is not offered at all.
        Assert.Null(WebsiteBuilderController.BuilderBaseUrl(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Builder:BaseUrl"] = "https://builder.example.test", ["Builder:ServiceKey"] = "too-short"
        }).Build()));
        Assert.Equal("https://builder.example.test", WebsiteBuilderController.BuilderBaseUrl(Configuration()));
    }

    // ---------------- helpers ----------------

    private static (IPRO.Admin.Controllers.BuilderImportsController Controller, Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataDictionary TempData) Admin(
        IPRODbContext db, IBlobStorageService blob, IEmailService email)
    {
        var services = new ServiceCollection().AddSingleton(blob).AddSingleton(email).BuildServiceProvider();
        var controller = new IPRO.Admin.Controllers.BuilderImportsController(db, new PackageEntitlementService(new UnitOfWork(db), db),
            new SiteHandoverReturn572Tests.RecordingAudit(), Configuration(), services);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Name, "owner"), new Claim("Role", "SuperAdmin")
            }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        var temp = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NoTempData());
        controller.TempData = temp;
        return (controller, temp);
    }

    // The fitting site of 572, its package including the website.
    private static async Task<SiteHandoverReturn572Tests.Seeded> SeedAsync(IPRODbContext db, BuilderImport571Tests.MemoryBlob blob)
    {
        var seeded = await SiteHandoverReturn572Tests.SeedFittingSiteAsync(db, blob);
        var package = await db.AgentUsers.Where(a => a.Id == seeded.Site.AgentUserId).Select(a => a.PackageId).SingleAsync();
        db.Add(new PackageFeature { BillingRuleId = package, FeatureCode = PackageFeatureCodes.InstantWebsite, FeatureName = "Website", IsIncluded = true });
        await db.SaveChangesAsync();
        return seeded;
    }

    // A view of the portal (IPRO.Web) rendered from its compiled form, without a layout, as the
    // SuperAdmin screens are in BuilderImportScreens571Tests.
    internal static async Task<string> RenderPortalAsync<TModel>(string viewPath, TModel model)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions
        {
            ApplicationName = typeof(WebsiteBuilderController).Assembly.GetName().Name,
            ContentRootPath = Path.Combine(RepoRoot(), "src", "IPRO.Web"),
            EnvironmentName = "Production"
        });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(WebsiteBuilderController).Assembly);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.SetEndpoint(new Microsoft.AspNetCore.Http.Endpoint(_ => Task.CompletedTask, new Microsoft.AspNetCore.Http.EndpointMetadataCollection(), "render"));
        var actionContext = new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var engine = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Mvc.Razor.IRazorViewEngine>();
        var found = engine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        Assert.True(found.Success, $"{viewPath} was not found among the portal's compiled views");
        var viewData = new Microsoft.AspNetCore.Mvc.ViewFeatures.ViewDataDictionary<TModel>(
            new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary()) { Model = model };
        await using var writer = new StringWriter();
        await found.View!.RenderAsync(new Microsoft.AspNetCore.Mvc.Rendering.ViewContext(actionContext, found.View, viewData,
            new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(http, new NoTempData()), writer, new Microsoft.AspNetCore.Mvc.ViewFeatures.HtmlHelperOptions()));
        return writer.ToString();
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static async Task<string> SessionAsync(IPRODbContext db, AgentWebsite site)
    {
        if (!await BuilderBeta.IsOnAsync(db, site.AgentUserId)) await BuilderBeta.SetAsync(db, site.AgentUserId, true, false, "owner");
        var pass = await BuilderBeta.IssuePassAsync(db, site, false, DateTime.UtcNow);
        return (await BuilderBeta.RedeemPassAsync(db, pass, DateTime.UtcNow)).Granted!.Token;
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["App:BaseUrl"] = "https://portal.example.test/",
        ["Support:NotificationEmail"] = "staff@example.test",
        ["Builder:BaseUrl"] = "https://builder.example.test",
        ["Builder:ServiceKey"] = "service-key-for-tests-0123456789abcdef"
    }).Build();

    private const string ServiceKey = "service-key-for-tests-0123456789abcdef";

    // The builder's server: the key the two share (key: what it sends; configuredKey: iPro's own).
    private static BuilderApiController Api(IPRODbContext db, IBlobStorageService blob, IEmailService email, string bearer, byte[]? body = null,
        string? key = ServiceKey, string? configuredKey = null, IPRO.Business.Interfaces.IPackageEntitlementService? entitlements = null)
    {
        var services = new ServiceCollection().AddSingleton(blob).AddSingleton(email).BuildServiceProvider();
        var configuration = configuredKey == null ? Configuration()
            : new ConfigurationBuilder().AddConfiguration(Configuration()).AddInMemoryCollection(new Dictionary<string, string?> { ["Builder:ServiceKey"] = configuredKey }).Build();
        var controller = new BuilderApiController(db, entitlements ?? new PackageEntitlementService(new UnitOfWork(db), db), configuration, services,
            new Environment(), NullLogger<BuilderApiController>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer " + bearer;
        if (key != null) context.Request.Headers[BuilderApiController.ServiceKeyHeader] = key;
        if (body != null)
        {
            context.Request.Body = new MemoryStream(body);
            context.Request.ContentLength = body.Length;
        }
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private static WebsiteBuilderController Portal(IPRODbContext db, IBlobStorageService blob, int agentId)
    {
        var services = new ServiceCollection().AddSingleton(blob).BuildServiceProvider();
        // The website feature is the package's business, not this test's: granted.
        var controller = new WebsiteBuilderController(db, new GrantAll(), Configuration(), services, NullLogger<WebsiteBuilderController>.Instance);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NoTempData());
        return controller;
    }

    private static BuilderBetaJob Job(IPRODbContext db, FakeBuilder builder) =>
        new(db, new BuilderImport571Tests.MemoryBlob(), new OneClient(builder), Configuration(), NullLogger<BuilderBetaJob>.Instance);

    private static JsonObject Body(IActionResult result) => JsonSerializer.SerializeToNode(result switch
    {
        JsonResult json => json.Value,
        ObjectResult obj => obj.Value,
        _ => throw new InvalidOperationException(result.GetType().Name)
    })!.AsObject();

    private sealed class Environment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "ipro-585-webroot");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "IPRO.Web";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Production";
    }

    private sealed class FakeBuilder : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? LastKey { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://builder.example.test/internal/erase", request.RequestUri!.ToString());
            LastKey = request.Headers.GetValues("X-Ipro-Service-Key").Single();
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Status);
        }
    }

    private sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingEmail : IEmailService
    {
        public List<(string To, string Subject)> Sent { get; } = new();
        public Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        { Sent.Add((toEmail, subject)); return Task.FromResult(true); }
        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        { Sent.Add((toEmail, subject)); return Task.FromResult(EmailSendResult.Sent("msg-1")); }
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
    }

    private sealed class GrantAll : IPRO.Business.Interfaces.IPackageEntitlementService
    {
        public Task<IPRO.Business.Interfaces.PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) =>
            Task.FromResult(new IPRO.Business.Interfaces.PackageFeatureAccess { FeatureCode = featureCode, IsIncluded = true });
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => Task.FromResult(true);
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode) => throw new NotSupportedException();
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(false);
    }

    // An account whose billing lapsed (gated), or whose package no longer has the website.
    private sealed class Lapsed(bool gated = true) : IPRO.Business.Interfaces.IPackageEntitlementService
    {
        public Task<IPRO.Business.Interfaces.PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) =>
            Task.FromResult(new IPRO.Business.Interfaces.PackageFeatureAccess { FeatureCode = featureCode, IsIncluded = featureCode != PackageFeatureCodes.InstantWebsite });
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => Task.FromResult(featureCode != PackageFeatureCodes.InstantWebsite);
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode) => throw new NotSupportedException();
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(gated);
    }

    private sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
