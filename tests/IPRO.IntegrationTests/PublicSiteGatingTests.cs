using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// M13 + M2 (launch runway Phase 2, Wave A, 2026-08-27).
//
// M13: the cancel dialog has always promised "your site will go offline at the end of the billing
// period" (DOCS/22) and it never did -- IsAccessGatedAsync's call sites were the portal, the
// Billing page and the layout; the public website was not one of them. A lapsed agent's site
// stayed up forever, collecting leads, with no billing reason to come back. The fix gates
// FindWebsiteForHostAsync -- the single funnel behind page render, robots, sitemap, leads, custom
// forms and testimonials -- so a gated site is indistinguishable from one that does not exist.
//
// M2: RebuildRequestMeeting carries the same RemoveRange destruction as RebuildResources sixty
// lines above it, but sat on plain AdminAccess with a confirm that claimed "edits intact".
public class PublicSiteGatingTests
{
    // ------------------------------------------------------------------------- M13: offline --

    [Fact]
    public async Task M13_a_lapsed_agents_public_site_is_offline()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        // Never on a trial, no billing row: IsAccessGatedAsync gates this agent immediately --
        // the register's lapsed shape without needing TrialSettings.
        var seed = await SeedPublishedSiteAsync(db, billingStatus: null);

        var controller = NewController(db, seed.Host);
        var result = await controller.Index();

        // Pre-fix: the site rendered ("Index") exactly as if the agent were paying.
        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("NotFound", view.ViewName);
        Assert.Equal(StatusCodes.Status404NotFound, controller.Response.StatusCode);
    }

    [Fact]
    public async Task M13_an_active_agents_site_stays_online()
    {
        // The pin in the other direction: the gate must not take paying customers offline.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedPublishedSiteAsync(db, billingStatus: BillingStatus.Active);

        var controller = NewController(db, seed.Host);
        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Index", view.ViewName);
    }

    [Fact]
    public async Task M13_a_cancelled_but_paid_through_site_stays_online_until_the_promise_expires()
    {
        // DOCS/22's exact promise: offline at the END of the paid period, not at the moment of
        // cancelling. Both halves in one test, each with a fresh controller so the 2-minute
        // verdict cache cannot leak the first answer into the second.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedPublishedSiteAsync(db, billingStatus: BillingStatus.Cancelled,
            paidThrough: DateTime.UtcNow.AddDays(10));

        var during = await NewController(db, seed.Host).Index();
        Assert.Equal("Index", Assert.IsType<ViewResult>(during).ViewName);

        var billing = await db.Billings.SingleAsync(b => b.Id == seed.BillingId);
        billing.PaidThroughAt = DateTime.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Pre-fix: still "Index" -- the site never went offline at all.
        var after = await NewController(db, seed.Host).Index();
        Assert.Equal("NotFound", Assert.IsType<ViewResult>(after).ViewName);
    }

    [Fact]
    public async Task M13_a_gated_site_stops_collecting_leads()
    {
        // Offline means offline: a dead site must not keep harvesting visitor PII into an
        // account nobody is paying for.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedPublishedSiteAsync(db, billingStatus: null);

        var controller = NewController(db, seed.Host);
        var result = await controller.SubmitLead(new IPRO.Web.Models.WebsiteLeadFormViewModel
        {
            FirstName = "Visitor", LastName = "One", Email = "visitor@example.test",
            FormStartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30
        });

        Assert.IsType<NotFoundResult>(result);
        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.WebsiteLeads.CountAsync(l => l.AgentUserId == seed.AgentId));
    }

    [Fact]
    public async Task M13_the_verdict_is_cached_so_the_hot_path_pays_once()
    {
        // Host resolution runs on every public page view -- the hottest path in the product. The
        // gate must not add its queries to every one of them.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedPublishedSiteAsync(db, billingStatus: BillingStatus.Active);

        var counting = new CountingEntitlements(new PackageEntitlementService(new UnitOfWork(db), db));
        var controller = NewController(db, seed.Host, counting);

        await controller.Index();
        await controller.Index();

        Assert.Equal(1, counting.GateCalls);
    }

    // ------------------------------------------------------------------- M2: the sibling gate --

    [Fact]
    public void M2_rebuild_request_meeting_requires_superadmin_like_its_sibling()
    {
        // The wiring pin, for BOTH siblings so neither silently loses its gate again.
        foreach (var action in new[] { "RebuildRequestMeeting", "RebuildResources" })
        {
            var method = typeof(IPRO.Admin.Controllers.AgentsController).GetMethod(action);
            Assert.NotNull(method);
            var authorize = method!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: false)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .FirstOrDefault();
            // Pre-fix: RebuildRequestMeeting had no Authorize attribute of its own -- it ran on
            // the class-level AdminAccess policy, i.e. any authenticated admin.
            Assert.True(authorize != null && authorize.Policy == "SuperAdmin",
                $"{action} must carry [Authorize(Policy=\"SuperAdmin\")] -- it deletes the agent's page blocks");
        }
    }

    [Fact]
    public void M2_the_confirm_names_the_loss_and_support_admins_see_a_disabled_button()
    {
        // Source-walk (the M8 pattern): the Razor is where the honesty lives, and nothing else
        // pins it. The confirm must say what is destroyed, and the non-SuperAdmin branch must
        // DISABLE the button, not hide it -- the owner's standing rule, learned the hard way.
        var path = FindRepoFile(@"src\IPRO.Admin\Views\Agents\Details.cshtml");
        var razor = System.IO.File.ReadAllText(path);

        var start = razor.IndexOf("asp-action=\"RebuildRequestMeeting\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "the Rebuild Request Meeting form is gone from Details.cshtml");
        var end = razor.IndexOf("</form>", start, StringComparison.Ordinal);
        var block = razor[start..end];

        Assert.Contains("DELETES", block);                                   // names the loss
        Assert.Contains("cannot be recovered", block);                       // and its permanence
        Assert.Contains("AdminRoles.SuperAdmin", block);                     // role branch exists
        Assert.Contains("disabled", block);                                  // disable, not hide
    }

    // ------------------------------------------------------------------------------ plumbing --

    private sealed record Seed(int AgentId, int WebsiteId, int? BillingId, string Host);

    private static async Task<Seed> SeedPublishedSiteAsync(
        IPRODbContext db, BillingStatus? billingStatus, DateTime? paidThrough = null)
    {
        var rule = new BillingRule { PackageName = $"PS-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        var host = $"ps-{Guid.NewGuid():N}"[..14] + ".example.test";
        var agent = new AgentUser
        {
            UserName = $"ps-{Guid.NewGuid():N}"[..20],
            Email = $"ps-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Public", LastName = "Site",
            DomainName = $"ps-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario",
            TrialEndsAt = null    // never on a trial: no billing row = gated immediately
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        agent.PackageId = rule.Id;

        int? billingId = null;
        if (billingStatus.HasValue)
        {
            var billing = new IPRO.Entities.Billing
            {
                AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 60m,
                Status = billingStatus.Value, Period = BillingPeriod.Monthly,
                StartDate = DateTime.UtcNow.AddDays(-40),
                NextBillingDate = DateTime.UtcNow.AddDays(20),
                PaidThroughAt = billingStatus == BillingStatus.Active ? null : paidThrough,
                CancelledAt = billingStatus == BillingStatus.Active ? null : DateTime.UtcNow.AddDays(-1)
            };
            db.Add(billing);
            await db.SaveChangesAsync();
            billingId = billing.Id;
        }

        var template = new WebsiteTemplate { TemplateKey = $"tk-{Guid.NewGuid():N}"[..16], Name = "Test", BusinessType = "All" };
        db.Add(template);
        await db.SaveChangesAsync();

        var website = new AgentWebsite
        {
            AgentUserId = agent.Id,
            TemplateId = template.Id,
            IsPublished = true,
            CustomDomain = host
        };
        db.Add(website);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seed(agent.Id, website.Id, billingId, host);
    }

    private static PublicWebsiteController NewController(
        IPRODbContext db, string host, IPackageEntitlementService? entitlements = null)
    {
        var controller = new PublicWebsiteController(
            db,
            entitlements ?? new PackageEntitlementService(new UnitOfWork(db), db),
            new NullEmail(),
            NullLogger<PublicWebsiteController>.Instance,
            new ConfigurationBuilder().Build(),
            DataProtectionProvider.Create($"m13-tests-{Guid.NewGuid():N}"),
            new NullBlob(),
            new MemoryCache(new MemoryCacheOptions()),
            new NullConsent());
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
            context, new NullTempData());
        return controller;
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "IPRO.sln")))
            dir = System.IO.Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return System.IO.Path.Combine(dir!, relative);
    }

    private sealed class CountingEntitlements : IPackageEntitlementService
    {
        private readonly IPackageEntitlementService _inner;
        public int GateCalls;
        public CountingEntitlements(IPackageEntitlementService inner) { _inner = inner; }
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => _inner.HasAccessAsync(agentId, featureCode);
        public Task<PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) => _inner.GetAccessAsync(agentId, featureCode);
        public Task<bool> IsAccessGatedAsync(int agentId) { GateCalls++; return _inner.IsAccessGatedAsync(agentId); }
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode) => _inner.HasAccessBulkAsync(agentIds, featureCode);
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
        public Task<string> UploadAsync(System.IO.Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate) => Task.FromResult("https://blob.example.test/x");
        public Task<bool> DeleteAsync(string blobUrl) => Task.FromResult(true);
        public Task<System.IO.Stream?> DownloadAsync(string blobUrl) => Task.FromResult<System.IO.Stream?>(null);
        public Task<List<string>> ListAsync(string containerName) => Task.FromResult(new List<string>());
        public string GetPublicUrl(string containerName, string fileName) => $"https://blob.example.test/{containerName}/{fileName}";
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => Task.CompletedTask;
    }

    private sealed class NullConsent : IEmailConsentService
    {
        public bool IsSuppressed(Client client, EmailChannel channel, bool designSurvivesOptOut = false) => false;
        public Task<SuppressionResult> SuppressAllAsync(Client client, string source) => throw new NotSupportedException();
        public Task ResubscribeAsync(Client client) => throw new NotSupportedException();
        public Task<int> CancelSuppressedDripEnrollmentsAsync(int batchLimit = 500) => Task.FromResult(0);
        public Task<string> GetOrCreateTokenAsync(Client client) => Task.FromResult("tok");
        public string BuildPreferencesUrl(string token) => $"https://example.test/prefs/{token}";
    }

    private sealed class NullTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
