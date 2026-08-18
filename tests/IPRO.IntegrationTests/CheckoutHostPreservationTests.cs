using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.Billing;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// WEB-H-1 regression suite. The auth cookie is host-only, so PayPal's return_url/cancel_url must
// come back to the host the buyer's session lives on — a canonical return URL logged the buyer out
// between PayPal approval and capture: money moved at PayPal, nothing activated locally. These
// tests pin the allowlist, the custom-domain branch, both controller call sites, and — because the
// original defect was a hand-rolled second copy of the URL pair in AccountController — a source-
// level guard that fails if the literals ever appear outside PortalUrlHelper again.
public class CheckoutHostPreservationTests
{
    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:BaseUrl"] = "https://app.iproadvisers.com",
            ["App:PlatformDomains"] = "ipro-prod-web.azurewebsites.net,app.iproadvisers.com,www.iproadvisers.com,iproadvisers.com",
            ["App:TemporarySiteRootDomain"] = "247advisers.com"
        })
        .Build();

    private static HttpRequest RequestFor(string host, string scheme = "https")
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        return context.Request;
    }

    // ---------------------------------------------------------------- A. the allowlist, no DB --

    [Theory]
    [InlineData("bob.247advisers.com", "https://bob.247advisers.com")]           // temporary agent subdomain
    [InlineData("app.iproadvisers.com", "https://app.iproadvisers.com")]         // canonical
    [InlineData("www.iproadvisers.com", "https://www.iproadvisers.com")]         // platform domain, NOT rewritten
    [InlineData("ipro-prod-web.azurewebsites.net", "https://ipro-prod-web.azurewebsites.net")]
    public void A_host_we_serve_is_kept(string host, string expected)
    {
        Assert.Equal(expected, PortalUrlHelper.GetSessionBaseUrl(RequestFor(host), Config()));
    }

    [Fact]
    public void Localhost_keeps_its_port()
    {
        // The local dev env runs at http://localhost:5100. Composing from Host.Host instead of
        // Host.Value drops the port and breaks every local checkout — this is the test for it.
        Assert.Equal("http://localhost:5100",
            PortalUrlHelper.GetSessionBaseUrl(RequestFor("localhost:5100", scheme: "http"), Config()));
    }

    // AllowedHosts is "*" in appsettings, so this allowlist is the only thing standing between a
    // forged Host: header and PayPal's return_url. Unknown hosts must fall back to canonical.
    [Theory]
    [InlineData("evil.example.com")]
    [InlineData("247advisers.com.evil.com")]        // suffix confusion
    [InlineData("bob.247advisers.com.evil.com")]    // embedded real subdomain
    [InlineData("evil-247advisers.com")]            // missing dot boundary
    public void An_unknown_host_falls_back_to_canonical(string host)
    {
        Assert.Equal("https://app.iproadvisers.com",
            PortalUrlHelper.GetSessionBaseUrl(RequestFor(host), Config()));
    }

    [Fact]
    public void Host_matching_ignores_case_and_a_trailing_dot()
    {
        Assert.True(PortalUrlHelper.IsAppHost("BOB.247Advisers.com.", Config()));
    }

    [Fact]
    public void The_bare_apex_of_the_temporary_root_is_not_an_agent_host()
    {
        // Agents live at <name>.247advisers.com; the apex itself is nobody's session host.
        Assert.False(PortalUrlHelper.IsAppHost("247advisers.com", Config()));
    }

    // ------------------------------------------- B. bound custom domains, against real MySQL --

    [Theory]
    [InlineData("theirfirm.com")]       // DomainName
    [InlineData("www.theirfirm.com")]   // WwwDomain alias
    public async Task A_bound_custom_domain_is_kept(string host)
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedDomainAsync(db, azureBindingStatus: AgentDomainStatus.Bound, sslStatus: AgentDomainStatus.Bound);

        Assert.Equal($"https://{host}",
            await PortalUrlHelper.GetSessionBaseUrlAsync(RequestFor(host), Config(), db));
    }

    [Fact]
    public async Task An_unbound_custom_domain_falls_back_to_canonical()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedDomainAsync(db, azureBindingStatus: AgentDomainStatus.BindingPending, sslStatus: AgentDomainStatus.BindingPending);

        Assert.Equal("https://app.iproadvisers.com",
            await PortalUrlHelper.GetSessionBaseUrlAsync(RequestFor("theirfirm.com"), Config(), db));
    }

    [Fact]
    public async Task A_bound_domain_with_a_lagging_SslStatus_is_still_kept()
    {
        // Deliberate: the buyer's browser is ALREADY on this host over TLS, so a stale SslStatus
        // row must not divert the return URL to a host where the cookie does not exist — that would
        // silently reintroduce the exact WEB-H-1 defect. Someone "tightening" this check later is
        // who this test is for.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedDomainAsync(db, azureBindingStatus: AgentDomainStatus.Bound, sslStatus: AgentDomainStatus.BindingPending);

        Assert.Equal("https://theirfirm.com",
            await PortalUrlHelper.GetSessionBaseUrlAsync(RequestFor("theirfirm.com"), Config(), db));
    }

    [Fact]
    public async Task The_canonical_host_never_queries_the_database()
    {
        // A disposed context throws on any query; the config-allowlist path must short-circuit
        // before ever touching it.
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<IPRODbContext>()
            .UseMySql("Server=localhost;Database=never_used;User=none;Password=none",
                new Microsoft.EntityFrameworkCore.MySqlServerVersion(new Version(8, 0, 36)))
            .Options;
        var db = new IPRODbContext(options);
        await db.DisposeAsync();

        Assert.Equal("https://app.iproadvisers.com",
            await PortalUrlHelper.GetSessionBaseUrlAsync(RequestFor("app.iproadvisers.com"), Config(), db));
    }

    private static async Task SeedDomainAsync(IPRODbContext db, string azureBindingStatus, string sslStatus)
    {
        var rule = new BillingRule { PackageName = $"Pkg-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m };
        db.Add(rule);
        await db.SaveChangesAsync();

        var agent = new AgentUser
        {
            UserName = $"host-{Guid.NewGuid():N}"[..20],
            Email = "host.test@example.com",
            FirstName = "Host",
            LastName = "Test",
            DomainName = $"host-{Guid.NewGuid():N}"[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();

        var template = new WebsiteTemplate { Name = "T", TemplateKey = $"t-{Guid.NewGuid():N}"[..12], BusinessType = "Generic" };
        db.Add(template);
        await db.SaveChangesAsync();

        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, SiteTitle = "Host test site" };
        db.Add(website);
        await db.SaveChangesAsync();

        db.Add(new AgentDomain
        {
            AgentUserId = agent.Id,
            AgentWebsiteId = website.Id,
            DomainName = "theirfirm.com",
            RootDomain = "theirfirm.com",
            WwwDomain = "www.theirfirm.com",
            AzureBindingStatus = azureBindingStatus,
            SslStatus = sslStatus
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    // ------------------------------------------------------- C. the controller call sites --

    [Theory]
    [InlineData("bob.247advisers.com", "https://bob.247advisers.com")]
    [InlineData("evil.example.com", "https://app.iproadvisers.com")]   // injection, end to end
    public async Task Subscribe_returns_to_the_session_host(string host, string expectedOrigin)
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var billing = new RecordingBillingService();
        var controller = NewBillingController(billing, db, host);

        await controller.Subscribe(billingRuleId: 1, period: BillingPeriod.Monthly);

        Assert.Equal($"{expectedOrigin}/Billing/PayPalReturn", billing.CapturedReturnUrl);
        Assert.Equal($"{expectedOrigin}/Billing/Cancel", billing.CapturedCancelUrl);
    }

    [Fact]
    public async Task ResumePayment_returns_to_the_session_host()
    {
        // The call site most likely to be forgotten: the reported symptom was signup, but a resumed
        // payment started from /portal/Billing on an agent host dies identically without this.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var billing = new RecordingBillingService();
        var controller = NewBillingController(billing, db, "bob.247advisers.com");

        await controller.ResumePayment(invoiceId: 1);

        Assert.Equal("https://bob.247advisers.com/Billing/PayPalReturn", billing.CapturedReturnUrl);
        Assert.Equal("https://bob.247advisers.com/Billing/Cancel", billing.CapturedCancelUrl);
    }

    private static BillingController NewBillingController(RecordingBillingService billing, IPRODbContext db, string host)
    {
        var controller = new BillingController(
            billing,
            new UnitOfWork(db),
            Config(),
            new ThrowingEntitlements(),
            db,
            NullLogger<BillingController>.Instance);

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, "1") }, "test"))
        };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
            context, new NoopTempDataProvider());
        return controller;
    }

    // ------------------------------------------------ D. the sibling guard, at source level --

    [Fact]
    public void Only_PortalUrlHelper_may_carry_the_PayPal_return_or_cancel_literals()
    {
        // The original defect was not a wrong URL — it was a SECOND copy of the URL pair,
        // hand-rolled in AccountController, that nobody re-checked when the first was fixed.
        // This walks the source tree so a third copy fails the suite, not an audit.
        var srcRoot = FindSrcRoot();
        var offenders = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.EndsWith("PortalUrlHelper.cs", StringComparison.OrdinalIgnoreCase))
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains("/Billing/PayPalReturn") || text.Contains("/Billing/Cancel");
            })
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "The PayPal return/cancel URL literals exist outside PortalUrlHelper in: " +
            string.Join(", ", offenders) +
            ". Route them through PortalUrlHelper.BuildBillingActionUrlAsync instead — a second copy is how WEB-H-1 happened.");
    }

    [Fact]
    public void The_checkout_controllers_no_longer_use_the_canonical_base_url()
    {
        var srcRoot = FindSrcRoot();
        foreach (var file in new[]
                 {
                     Path.Combine(srcRoot, "IPRO.Web", "Controllers", "BillingController.cs"),
                     Path.Combine(srcRoot, "IPRO.Web", "Controllers", "GoogleCalendarController.cs")
                 })
        {
            Assert.False(File.ReadAllText(file).Contains("GetAgentPortalBaseUrl"),
                $"{Path.GetFileName(file)} calls GetAgentPortalBaseUrl — in-session URLs must use the session-host-aware methods.");
        }
    }

    private static string FindSrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src");
    }

    // ------------------------------------------------------------------------- test doubles --

    // Captures the URLs the controller hands to billing; everything else is unreachable in these tests.
    private sealed class RecordingBillingService : IBillingService
    {
        public string? CapturedReturnUrl;
        public string? CapturedCancelUrl;

        private BillingChangeResult Capture(string returnUrl, string cancelUrl)
        {
            CapturedReturnUrl = returnUrl;
            CapturedCancelUrl = cancelUrl;
            return new BillingChangeResult { Success = true, RequiresPayment = true, ApprovalUrl = "https://paypal.test/approve" };
        }

        public Task<BillingChangeResult> CreateSubscriptionAsync(int userId, int billingRuleId, BillingPeriod period, string returnUrl, string cancelUrl) =>
            Task.FromResult(Capture(returnUrl, cancelUrl));

        public Task<BillingChangeResult> ResumePaymentAsync(int userId, int invoiceId, string returnUrl, string cancelUrl) =>
            Task.FromResult(Capture(returnUrl, cancelUrl));

        public Task<IPRO.Entities.Billing?> GetActiveSubscriptionAsync(int userId) => Task.FromResult<IPRO.Entities.Billing?>(null);
        public Task<BillingChangeResult> CapturePaymentAsync(int userId, string orderId) => throw new NotSupportedException();
        public Task<SubscriptionChange?> GetPendingChangeAsync(int userId) => Task.FromResult<SubscriptionChange?>(null);
        public Task<BillingChangeResult> CancelScheduledChangeAsync(int userId) => throw new NotSupportedException();
        public Task<BillingIssue?> GetBillingIssueAsync(int userId) => Task.FromResult<BillingIssue?>(null);
        public Task<bool> CancelPendingPaymentAsync(int userId, int invoiceId) => throw new NotSupportedException();
        public Task<bool> CancelPendingPaymentByOrderAsync(int userId, string orderId) => throw new NotSupportedException();
        public Task<bool> CancelSubscriptionAsync(int userId) => throw new NotSupportedException();
        public Task<int> ProcessDueSubscriptionChangesAsync() => throw new NotSupportedException();
        public Task<int> NotifyBillingIssuesAsync() => throw new NotSupportedException();
        public Task<int> ReconcileDuplicateActiveSubscriptionsAsync() => throw new NotSupportedException();
        public Task<int> ReconcileActiveSubscriptionsWithPayPalAsync() => throw new NotSupportedException();
        public Task<bool> HandleWebhookAsync(string eventType, string payload, PayPalWebhookHeaders headers, decimal amount) => throw new NotSupportedException();
        public Task<PayPalPlanSyncResult> SyncPayPalPlansAsync(int billingRuleId) => throw new NotSupportedException();
        public Task<PayPalPlanSyncResult> SyncDailyTestPlanAsync(int billingRuleId) => throw new NotSupportedException();
        public Task<BillingChangeResult> EmailPaidInvoiceAsync(int invoiceId, bool force = false) => throw new NotSupportedException();
        public Task<List<Invoice>> GetInvoicesAsync(int userId) => Task.FromResult(new List<Invoice>());
        public Task<Invoice> GenerateInvoiceAsync(int userId, decimal amount, string description) => throw new NotSupportedException();
        public Task<List<BillingRule>> GetPackagesAsync() => Task.FromResult(new List<BillingRule>());
        public Task<PromotionCode?> ValidatePromotionCodeAsync(string? code, int billingRuleId, int? agentId = null) => Task.FromResult<PromotionCode?>(null);
    }

    private sealed class ThrowingEntitlements : IPRO.Business.Interfaces.IPackageEntitlementService
    {
        public Task<IPRO.Business.Interfaces.PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) => throw new NotSupportedException();
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => throw new NotSupportedException();
        public Task<System.Collections.Generic.Dictionary<int, bool>> HasAccessBulkAsync(System.Collections.Generic.IEnumerable<int> agentIds, string featureCode) => throw new NotSupportedException();
        public Task<bool> IsAccessGatedAsync(int agentId) => throw new NotSupportedException();
    }

    private sealed class NoopTempDataProvider : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
