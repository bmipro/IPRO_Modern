using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IPRO.Billing;
using IPRO.Business.Services;
using IPRO.Business.SiteImport;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// 663 (2026-11-20, the owner: "yes go ahead with 665 and 663"): the audit findings the public history still
// names as open (DOCS/AUDIT_RECONCILIATION_2026-09-17.md, "Still open today"), each read against today's code.
// The ones still open are fixed here; 640's limits are in PublicForms578Tests and RateLimitByAction582Tests, the
// drip figures in DripRecoveryTests.
public class AuditLeftovers663Tests
{
    // ---- S3 (08-20 L12): article text is cleaned when a public page draws it --------------------------------

    [Fact]
    public void Article_text_is_cleaned_as_a_public_page_draws_it()
    {
        var html = PublicArticleHtml.Clean("<p>Tips</p><form action=\"https://x.example\"><input name=\"card\"></form><script>steal()</script><p onclick=\"x()\">More</p>").ToString();
        Assert.Contains("<p>Tips</p>", html);
        Assert.DoesNotContain("<form", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, PublicArticleHtml.Clean(null).ToString());

        foreach (var design in new[] { "_ClassicManagedPage", "_EditorialManagedPage", "_ModernManagedPage" })
        {
            var view = Read($@"src\IPRO.Web\Views\PublicWebsite\{design}.cshtml");
            Assert.DoesNotContain("@Html.Raw(articleContent.Content)", view);
            Assert.DoesNotContain("@Html.Raw(post.Content)", view);
            Assert.Contains("@IPRO.Web.Infrastructure.PublicArticleHtml.Clean(articleContent.Content)", view);
            Assert.Contains("@IPRO.Web.Infrastructure.PublicArticleHtml.Clean(post.Content)", view);
        }
    }

    // ---- S5 (08-20 L2/L3): every kind of telemetry is scrubbed, by one scrub both apps share --------------------

    [Fact]
    public void Tokens_are_scrubbed_from_log_lines_outgoing_calls_and_errors_in_both_apps()
    {
        var trace = new TraceTelemetry("Reset link https://app.iproadvisers.com/Account/ResetPassword?token=SECRET123&email=x sent");
        new IPRO.Web.Infrastructure.SensitiveDataTelemetryInitializer().Initialize(trace);
        Assert.DoesNotContain("SECRET123", trace.Message);
        Assert.Contains("email=x", trace.Message);

        var call = new DependencyTelemetry { Name = "GET /invoice/INVTOKEN9", Data = "https://app.iproadvisers.com/invoice/INVTOKEN9?s=SIG1" };
        new IPRO.Web.Infrastructure.SensitiveDataTelemetryInitializer().Initialize(call);
        Assert.DoesNotContain("INVTOKEN9", call.Data + call.Name);
        Assert.DoesNotContain("SIG1", call.Data);

        var error = new ExceptionTelemetry(new InvalidOperationException("boom")) { Message = "Failed on /t/c/news/TRACKTOKEN7?secret=HOOKSECRET" };
        error.Properties["Url"] = "https://app.iproadvisers.com/Preview/Show?firstName=Jane";
        new IPRO.Web.Infrastructure.SensitiveDataTelemetryInitializer().Initialize(error);
        Assert.DoesNotContain("TRACKTOKEN7", error.Message);
        Assert.DoesNotContain("HOOKSECRET", error.Message);
        Assert.DoesNotContain("Jane", error.Properties["Url"]);

        // SuperAdmin's copy had only "token" and "subscription_id"; it is the same scrub now.
        var admin = new RequestTelemetry { Url = new Uri("https://admin.iproadvisers.com/Preview?secret=ABC&key=DEF&page=2") };
        new IPRO.Admin.Infrastructure.SensitiveDataTelemetryInitializer().Initialize(admin);
        Assert.DoesNotContain("ABC", admin.Url.ToString());
        Assert.DoesNotContain("DEF", admin.Url.ToString());
        Assert.Contains("page=2", admin.Url.ToString());
    }

    // ---- S8 (08-14 JOBS-11): the SendGrid notice's time is looked at ------------------------------------------

    [Fact]
    public void A_SendGrid_notice_older_or_newer_than_ten_minutes_is_refused()
    {
        var now = DateTimeOffset.UtcNow;
        string At(long seconds) => seconds.ToString(CultureInfo.InvariantCulture);
        Assert.True(NewsletterController.SendGridTimestampIsFresh(At(now.ToUnixTimeSeconds()), now));
        Assert.True(NewsletterController.SendGridTimestampIsFresh(At(now.ToUnixTimeSeconds() - 590), now));
        Assert.False(NewsletterController.SendGridTimestampIsFresh(At(now.ToUnixTimeSeconds() - 601), now));
        Assert.False(NewsletterController.SendGridTimestampIsFresh(At(now.ToUnixTimeSeconds() + 601), now));
        Assert.False(NewsletterController.SendGridTimestampIsFresh("1756000000", now));
        Assert.False(NewsletterController.SendGridTimestampIsFresh("soon", now));
        Assert.False(NewsletterController.SendGridTimestampIsFresh("-5", now));
    }

    // ---- C1 (08-14 BILLING-12): a PENDING capture is not paid ---------------------------------------------------

    [Theory]
    [InlineData("{\"status\":\"COMPLETED\",\"purchase_units\":[{\"payments\":{\"captures\":[{\"status\":\"COMPLETED\"}]}}]}", "Completed")]
    [InlineData("{\"status\":\"COMPLETED\",\"purchase_units\":[{\"payments\":{\"captures\":[{\"status\":\"PENDING\",\"status_details\":{\"reason\":\"ECHECK\"}}]}}]}", "Pending")]
    [InlineData("{\"status\":\"COMPLETED\",\"purchase_units\":[{\"payments\":{\"captures\":[{\"status\":\"DECLINED\"}]}}]}", "Refused")]
    [InlineData("{\"status\":\"COMPLETED\"}", "Completed")]
    [InlineData("{}", "Refused")]
    [InlineData("not json", "Refused")]
    public void Only_a_completed_capture_counts_as_paid(string answer, string expected) =>
        Assert.Equal(expected, PayPalBillingService.ReadOrderCapture(answer).ToString());

    // ---- S7 (08-14 WEB-L-1): the website editor asks about the package on every action --------------------------

    [Fact]
    public async Task Every_website_editor_action_asks_whether_the_package_includes_the_website()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var without = await SeedAsync(db, website: false, blog: false);
        var with = await SeedAsync(db, website: true, blog: false);

        Assert.True(await GateStopsAsync(db, without.AgentId));
        Assert.False(await GateStopsAsync(db, with.AgentId));
    }

    [Fact]
    public async Task A_copied_page_leaves_out_the_blocks_the_package_does_not_include()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAsync(db, website: true, blog: false);

        var controller = PagesController(db, seed.AgentId);
        var result = Assert.IsType<RedirectToActionResult>(await controller.Duplicate(seed.PageId));
        var copyId = (int)result.RouteValues!["id"]!;
        var copied = await db.WebsiteContentBlocks.Where(b => b.WebsitePageId == copyId).Select(b => b.BlockType).ToListAsync();
        Assert.Equal(new[] { WebsiteBlockTypes.Text }, copied);
        Assert.Contains("without the blocks your package does not include: Blog", controller.TempData["Warning"] as string);

        Assert.Equal(PackageFeatureCodes.ManagedBlog, WebsitePagesController.BlockFeature(WebsiteBlockTypes.Blog));
        Assert.Equal(PackageFeatureCodes.PollSurveys, WebsitePagesController.BlockFeature(WebsiteBlockTypes.PollVote));
        Assert.Null(WebsitePagesController.BlockFeature(WebsiteBlockTypes.Text));
        // The same lines AddBlock asks about, one by one.
        var controllerSource = Read(@"src\IPRO.Web\Controllers\WebsitePagesController.cs");
        var addBlock = controllerSource[controllerSource.IndexOf("public async Task<IActionResult> AddBlock(", StringComparison.Ordinal)..];
        addBlock = addBlock[..addBlock.IndexOf("_db.WebsiteContentBlocks.Add(NewBlock(", StringComparison.Ordinal)];
        foreach (Match code in Regex.Matches(addBlock, @"PackageFeatureCodes\.([A-Za-z]+)"))
            Assert.Contains("PackageFeatureCodes." + code.Groups[1].Value, controllerSource[controllerSource.IndexOf("internal static string? BlockFeature(", StringComparison.Ordinal)..]);
    }

    // ---- 640 (572's note): the importer's pixel cap, the builder's own -------------------------------------------

    [Fact]
    public void A_picture_too_large_in_pixels_is_refused_whatever_its_file_size()
    {
        Assert.True(ImageDimensions.TryRead(Png(13_000, 100), ".png", out var w, out var h));
        Assert.Equal((13_000, 100), (w, h));
        Assert.False(ImageDimensions.WithinCap(w, h));
        Assert.True(ImageDimensions.TryRead(Png(10_000, 5_000), ".png", out w, out h));
        Assert.False(ImageDimensions.WithinCap(w, h));                       // 50 million pixels
        Assert.True(ImageDimensions.TryRead(Png(4_000, 3_000), ".png", out w, out h));
        Assert.True(ImageDimensions.WithinCap(w, h));
        Assert.True(ImageDimensions.TryRead(Jpeg(2_400, 1_600), ".jpg", out w, out h));
        Assert.Equal((2_400, 1_600), (w, h));
        Assert.True(ImageDimensions.TryRead(Gif(640, 480), ".gif", out w, out h));
        Assert.Equal((640, 480), (w, h));
        Assert.False(ImageDimensions.TryRead(new byte[] { 1, 2, 3 }, ".png", out _, out _));

        var reader = Read(@"src\IPRO.Business\SiteImport\BuilderPackageReader.cs");
        Assert.Contains("ImageDimensions.TryRead(bytes, Path.GetExtension(file), out var width, out var height) && !ImageDimensions.WithinCap(width, height)", reader);
    }

    // ---- the small ones ------------------------------------------------------------------------------------------

    [Fact]
    public void No_page_relies_on_an_inline_handler_the_browser_drops()
    {
        // 07-24 M-6 residual: the campaign page's "Resume failed" asked its question through onsubmit=, which CSP
        // drops; the shared js-confirm-submit asks it now.
        var handler = new Regex(@"\son(?:submit|click|change|input|load|keyup|keydown)\s*=\s*""", RegexOptions.IgnoreCase);
        foreach (var app in new[] { "IPRO.Web", "IPRO.Admin" })
            foreach (var view in Directory.GetFiles(Path.Combine(Root(), "src", app, "Views"), "*.cshtml", SearchOption.AllDirectories))
                foreach (var line in File.ReadAllLines(view).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)))   // a script's comment may name one
                    Assert.False(handler.IsMatch(line), $"{Path.GetFileName(view)} has an inline event handler: {line.Trim()}");
        Assert.Contains("js-confirm-submit\" type=\"submit\"", Read(@"src\IPRO.Web\Views\Campaigns\Details.cshtml"));
    }

    [Fact]
    public void The_article_columns_have_their_own_start_up_step_and_the_counter_is_raised_in_the_database()
    {
        var repair = Read(@"src\IPRO.DataAccess\StartupSchemaRepair.cs");
        var drip = repair[repair.IndexOf("public static async Task EnsureDripCampaignEnrollmentSchemaAsync", StringComparison.Ordinal)..];
        drip = drip[..drip.IndexOf("public static async Task EnsureArticleSchemaAsync", StringComparison.Ordinal)];
        Assert.DoesNotContain("\"Articles\"", drip);
        foreach (var app in new[] { "IPRO.Web", "IPRO.Admin" })
            Assert.Contains("await StartupGuard.RunStepAsync(\"StartupSchemaRepair.EnsureArticleSchemaAsync\", () => StartupSchemaRepair.EnsureArticleSchemaAsync(db), db, app.Logger);",
                Read($@"src\{app}\Program.cs"));

        var dyk = Read(@"src\IPRO.Scheduler\DidYouKnowEmailDispatchJob.cs");
        Assert.Contains(".SetProperty(q => q.SendAttempts, q => q.SendAttempts + 1)", dyk);
        Assert.DoesNotContain("var attempts = item.SendAttempts + 1;", dyk);

#pragma warning disable CS0618
        Assert.Equal(3, (int)RefundStatus.ConvertedToCredit);   // kept, never set, its number unchanged
#pragma warning restore CS0618
        Assert.Equal(4, (int)RefundStatus.Waived);
    }

    // ---- harness -------------------------------------------------------------------------------------------------

    private sealed record Seed(int AgentId, int PageId);

    private static async Task<Seed> SeedAsync(IPRODbContext db, bool website, bool blog)
    {
        var rule = new BillingRule { PackageName = $"A663-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        await db.SaveChangesAsync();
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.InstantWebsite, FeatureName = "Website", IsIncluded = website });
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.ManagedBlog, FeatureName = "Blog", IsIncluded = blog });
        var agent = new AgentUser
        {
            UserName = $"a663-{Guid.NewGuid():N}"[..20], Email = $"a663-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Audit", LastName = "Left", DomainName = $"a663-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario", PackageId = rule.Id, TrialEndsAt = null
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        db.Add(new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 60m, Status = BillingStatus.Active, Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddDays(-10), NextBillingDate = DateTime.UtcNow.AddDays(20)
        });
        var template = new WebsiteTemplate { TemplateKey = $"tk-{Guid.NewGuid():N}"[..16], Name = "Test", BusinessType = "All" };
        db.Add(template);
        await db.SaveChangesAsync();
        var site = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, IsPublished = true };
        db.Add(site);
        await db.SaveChangesAsync();
        var page = new WebsitePage { AgentWebsiteId = site.Id, Title = "Tips", Slug = "tips", NavigationLabel = "Tips", SortOrder = 1 };
        db.Add(page);
        await db.SaveChangesAsync();
        db.AddRange(
            new WebsiteContentBlock { WebsitePageId = page.Id, BlockType = WebsiteBlockTypes.Text, Heading = "Hello", SortOrder = 0, IsVisible = true },
            new WebsiteContentBlock { WebsitePageId = page.Id, BlockType = WebsiteBlockTypes.Blog, Heading = "Blog", SortOrder = 1, IsVisible = true });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seed(agent.Id, page.Id);
    }

    private static async Task<bool> GateStopsAsync(IPRODbContext db, int agentId)
    {
        var controller = PagesController(db, agentId);
        var actionContext = new ActionContext(controller.HttpContext, new RouteData(), new ActionDescriptor());
        var executing = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller);
        var ran = false;
        await controller.OnActionExecutionAsync(executing, () =>
        {
            ran = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), controller));
        });
        if (ran) return false;
        var redirect = Assert.IsType<RedirectToActionResult>(executing.Result);
        Assert.Equal(("Index", "Billing"), (redirect.ActionName, redirect.ControllerName));
        return true;
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

    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }

    private static byte[] Jpeg(int width, int height)
    {
        // SOI, an APP0 segment, then SOF0 with the size.
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08 };
        bytes.AddRange(new[] { (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width });
        bytes.AddRange(new byte[12]);
        return bytes.ToArray();
    }

    private static byte[] Gif(int width, int height) =>
        new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0, 0, 0, 0x3B };

    private sealed class NullBlob : IPRO.Utility.IBlobStorageService
    {
        public Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate) => Task.FromResult("https://blob.example.test/x");
        public Task<bool> DeleteAsync(string blobUrl) => Task.FromResult(true);
        public Task<Stream?> DownloadAsync(string blobUrl) => Task.FromResult<Stream?>(null);
        public Task<List<string>> ListAsync(string containerName) => Task.FromResult(new List<string>());
        public string GetPublicUrl(string containerName, string fileName) => $"https://blob.example.test/{containerName}/{fileName}";
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => Task.CompletedTask;
    }

    private sealed class NullTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative)).Replace("\r\n", "\n");
}
