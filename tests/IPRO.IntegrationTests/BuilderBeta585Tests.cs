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

// 585: the new builder (Beta) for iPro's customers. A pass works once and for ten minutes; a session
// stops the moment the Beta is switched off or the account closed; the builder's server gets a fresh
// hand-over and can only send back a package of it; what an adviser sends waits for iPro's staff, or
// for the adviser's own review where a SuperAdmin allows it, which keeps what they did not remove; the
// previous site can be put back; and deleting an adviser asks the builder to erase their draft.
public class BuilderBeta585Tests
{
    [Fact]
    public async Task A_pass_works_once_and_only_while_the_beta_is_on()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seeded = await SiteHandoverReturn572Tests.SeedFittingSiteAsync(db, new BuilderImport571Tests.MemoryBlob());
        var now = DateTime.UtcNow;

        var beta = await BuilderBeta.SetAsync(db, seeded.Site.AgentUserId, enabled: true, adviserPublishes: false, "owner");
        Assert.Matches("^t_[0-9a-f]{32}$", beta.Tenant);
        var tenant = beta.Tenant;
        Assert.Equal(tenant, (await BuilderBeta.SetAsync(db, seeded.Site.AgentUserId, true, true, "owner")).Tenant);

        var pass = await BuilderBeta.IssuePassAsync(db, seeded.Site, startFresh: false, continueHandoverId: "", now);
        Assert.True(BuilderBeta.LooksLikeToken(pass));
        Assert.DoesNotContain(pass, (await db.WebsiteBuilderSessions.AsNoTracking().SingleAsync()).TokenSha256);
        var (granted, _) = await BuilderBeta.RedeemPassAsync(db, pass, now);
        Assert.NotNull(granted);
        Assert.True(granted!.Session.StartFresh);
        Assert.Equal(now.Add(BuilderBeta.SessionLifetime), granted.Session.ExpiresAt, TimeSpan.FromSeconds(1));
        var (again, refusal) = await BuilderBeta.RedeemPassAsync(db, pass, now);
        Assert.Null(again);
        Assert.Contains("already used or has expired", refusal);

        var late = await BuilderBeta.IssuePassAsync(db, seeded.Site, false, "", now);
        Assert.Null((await BuilderBeta.RedeemPassAsync(db, late, now.AddMinutes(11))).Granted);

        var off = await BuilderBeta.IssuePassAsync(db, seeded.Site, false, "", now);
        await BuilderBeta.SetAsync(db, seeded.Site.AgentUserId, enabled: false, adviserPublishes: false, "owner");
        var (none, why) = await BuilderBeta.RedeemPassAsync(db, off, now);
        Assert.Null(none);
        Assert.Contains("not switched on", why);
        Assert.Null((await BuilderBeta.RedeemPassAsync(db, "not-a-pass", now)).Granted);
    }

    [Fact]
    public async Task A_session_stops_when_the_beta_is_switched_off_or_the_account_closed()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seeded = await SiteHandoverReturn572Tests.SeedFittingSiteAsync(db, new BuilderImport571Tests.MemoryBlob());
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
        var seeded = await SiteHandoverReturn572Tests.SeedFittingSiteAsync(db, blob);
        await BuilderBeta.SetAsync(db, seeded.Site.AgentUserId, enabled: true, adviserPublishes: false, "owner");
        var pass = await BuilderBeta.IssuePassAsync(db, seeded.Site, false, "", DateTime.UtcNow);

        // 1. The pass, traded for a session: the builder learns whose site it is and how it looks.
        var opened = Body(await Api(db, blob, email, pass).Open());
        var token = opened["sessionToken"]!.GetValue<string>();
        Assert.Equal((await BuilderBeta.FindAsync(db, seeded.Site.AgentUserId))!.Tenant, opened["tenant"]!.GetValue<string>());
        Assert.True(opened["startFresh"]!.GetValue<bool>());
        Assert.Equal("staff", opened["publishing"]!.GetValue<string>());
        Assert.Equal("Sample Bakery", opened["adviser"]!.GetValue<string>());
        Assert.Equal("https://portal.example.test/portal/Website", opened["returnTo"]!.GetValue<string>());
        Assert.Matches("^#[0-9a-fA-F]{6}$", opened["look"]!["accent"]!.GetValue<string>());
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
        var seeded = await SiteHandoverReturn572Tests.SeedFittingSiteAsync(db, blob);
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

        // Another adviser reaches nothing of it.
        Assert.IsType<NotFoundResult>(await Portal(db, blob, seeded.Site.AgentUserId + 999).Review(importId));

        var review = Assert.IsType<ViewResult>(await Portal(db, blob, seeded.Site.AgentUserId).Review(importId));
        var model = Assert.IsType<WebsiteBuilderReviewViewModel>(review.Model);
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

        // Put back, also with the Beta switched off since.
        await BuilderBeta.SetAsync(db, seeded.Site.AgentUserId, enabled: false, adviserPublishes: false, "owner");
        Assert.IsType<RedirectToActionResult>(await Portal(db, blob, seeded.Site.AgentUserId).PutBack());
        db.ChangeTracker.Clear();
        Assert.Equal(WebsiteBuilderImportStatus.Restored, (await db.WebsiteBuilderImports.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(new[] { "About us", "Opening hours" },
            await db.WebsiteContentBlocks.Where(b => b.WebsitePage.AgentWebsiteId == seeded.Site.Id && b.WebsitePage.Slug == "about").OrderBy(b => b.SortOrder).Select(b => b.Heading).ToListAsync());
    }

    [Fact]
    public async Task What_waits_for_iPros_staff_is_not_the_advisers_to_review()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SiteHandoverReturn572Tests.SeedFittingSiteAsync(db, blob);
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
        var seeded = await SiteHandoverReturn572Tests.SeedFittingSiteAsync(db, new BuilderImport571Tests.MemoryBlob());
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
        var seeded = await SiteHandoverReturn572Tests.SeedFittingSiteAsync(db, blob);
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

    // ---------------- helpers ----------------

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
        var pass = await BuilderBeta.IssuePassAsync(db, site, false, "", DateTime.UtcNow);
        return (await BuilderBeta.RedeemPassAsync(db, pass, DateTime.UtcNow)).Granted!.Token;
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["App:BaseUrl"] = "https://portal.example.test/",
        ["Support:NotificationEmail"] = "staff@example.test",
        ["Builder:BaseUrl"] = "https://builder.example.test",
        ["Builder:ServiceKey"] = "service-key-for-tests-0123456789abcdef"
    }).Build();

    private static BuilderApiController Api(IPRODbContext db, IBlobStorageService blob, IEmailService email, string bearer, byte[]? body = null)
    {
        var services = new ServiceCollection().AddSingleton(blob).AddSingleton(email).BuildServiceProvider();
        var controller = new BuilderApiController(db, new PackageEntitlementService(new UnitOfWork(db), db), Configuration(), services,
            new Environment(), NullLogger<BuilderApiController>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer " + bearer;
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

    private sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
