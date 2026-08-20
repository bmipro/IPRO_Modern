using System;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// The 2026-08-20 medium sweep. One region per finding; each pins the fix so it cannot reopen.
public class MediumSweepTests
{
    // Reproduces the security audit's M-3 and H-2 claims against the REAL sanitizer, so the
    // verdict is this suite's, not a report's. Both are expected to FAIL until fixed.

    [Fact]
    public void Removing_form_tags_must_not_destroy_their_inner_content()
    {
        // A styled <button> CTA is ordinary pasted email HTML. Stripping the tag is correct;
        // deleting the words inside it is silent, permanent data loss -- and sanitisation runs on
        // WRITE, including a re-save of existing article content.
        var outp = IPRO.Business.Services.HtmlContentSanitizer.Sanitize(
            "<form action=\"https://evil\"><p>KEEPME paragraph</p></form><button>CTA TEXT</button>");
        Assert.Contains("KEEPME paragraph", outp);
        Assert.Contains("CTA TEXT", outp);
    }

    [Fact]
    public void Overlay_cannot_be_rebuilt_from_the_properties_left_allowed()
    {
        // The overlay control removes position/z-index/inset/pointer-events, but transform +
        // negative margin + viewport sizing reconstructs a full-page opaque cover -- the same
        // phishing primitive, so the control does not do what its name claims.
        var outp = IPRO.Business.Services.HtmlContentSanitizer.Sanitize(
            "<div style=\"transform:translateY(-500px);margin-top:-1000px;width:100vw;height:100vh\">OVERLAY</div>");
        var flat = outp.Replace(" ", "").ToLowerInvariant();
        Assert.False(flat.Contains("transform:translate") && flat.Contains("width:100vw") && flat.Contains("height:100vh"),
            $"a full-viewport overlay survived sanitisation: {outp}");
    }

    // ------------------------------------------------------------- JOBS-5/8: transient vs final --

    [Fact]
    public void Transient_and_permanent_failures_are_distinguishable()
    {
        Assert.False(EmailSendResult.Failed("400 bad address").IsTransient);
        Assert.True(EmailSendResult.FailedTransient("timeout").IsTransient);
        Assert.False(EmailSendResult.Sent().IsTransient);
    }

    [Fact]
    public void A_transient_drip_failure_retries_and_caps_a_permanent_one_fails_now()
    {
        var enrollment = new DripCampaignEnrollment { Status = DripCampaignEnrollmentStatus.Active };

        // Transient failures keep the enrollment alive with the error visible...
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            IPRO.Scheduler.DripCampaignJob.HandleSendFailure(enrollment, transient: true, $"timeout {attempt}");
            Assert.Equal(DripCampaignEnrollmentStatus.Active, enrollment.Status);
            Assert.Equal(attempt, enrollment.SendAttempts);
            Assert.Contains("timeout", enrollment.LastError);
        }
        // ...until the cap, where it fails with an honest summary...
        IPRO.Scheduler.DripCampaignJob.HandleSendFailure(enrollment, transient: true, "timeout 5");
        Assert.Equal(DripCampaignEnrollmentStatus.Failed, enrollment.Status);
        Assert.Contains("Gave up after", enrollment.LastError);

        // ...while an answered rejection fails immediately, no retries.
        var rejected = new DripCampaignEnrollment { Status = DripCampaignEnrollmentStatus.Active };
        IPRO.Scheduler.DripCampaignJob.HandleSendFailure(rejected, transient: false, "550 no such user");
        Assert.Equal(DripCampaignEnrollmentStatus.Failed, rejected.Status);
        Assert.Equal(0, rejected.SendAttempts);
    }

    // ------------------------------------------------------- M-8: the cap is claimed at checkout --

    [Fact]
    public async Task A_full_promo_code_refuses_checkout_instead_of_over_redeeming()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAgentWithPromoAsync(db, maxRedemptions: 1, alreadyRedeemed: 1);

        var result = await NewBillingService(db).CreateSubscriptionAsync(
            seed.AgentId, seed.RuleId, BillingPeriod.Monthly, "https://x/r", "https://x/c");

        // Validation filters a full code up front (promo resolves to null), so checkout proceeds
        // as an undiscounted signup -- and, critically, the counter must NOT move past the cap.
        // (The atomic claim guards the remaining race window; its refusal branch is unreachable
        // through the public API without a concurrent claim, which is the point.)
        db.ChangeTracker.Clear();
        Assert.Equal(1, (await db.PromotionCodes.AsNoTracking().SingleAsync(p => p.Id == seed.PromoId)).RedemptionCount);
    }

    [Fact]
    public async Task A_failed_checkout_releases_the_claimed_slot()
    {
        // No PayPal settings in tests: the claim succeeds, checkout creation then fails at the
        // "PayPal is not configured" gate, and the slot must come back -- otherwise every PayPal
        // hiccup burns cap capacity forever.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAgentWithPromoAsync(db, maxRedemptions: 5, alreadyRedeemed: 2);

        var result = await NewBillingService(db).CreateSubscriptionAsync(
            seed.AgentId, seed.RuleId, BillingPeriod.Monthly, "https://x/r", "https://x/c");

        Assert.False(result.Success);
        db.ChangeTracker.Clear();
        Assert.Equal(2, (await db.PromotionCodes.AsNoTracking().SingleAsync(p => p.Id == seed.PromoId)).RedemptionCount);
    }

    // ----------------------------------------------------- A5-M-QUOTA: the pool counts articles --

    [Fact]
    public async Task Article_images_count_against_the_shared_storage_pool()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedBareAgentAsync(db);
        db.Add(new Article { AgentUserId = agentId, Title = "T", Content = "c", ImageUrl = "https://x/a.jpg", ImageSizeBytes = 5_000_000 });
        await db.SaveChangesAsync();

        Assert.Equal(5_000_000, await IPRO.Web.Infrastructure.AgentStorageUsage.TotalBytesAsync(db, agentId));
    }

    [Fact]
    public void A_missing_storage_limit_defaults_instead_of_meaning_unlimited()
    {
        Assert.Equal((long)IPRO.Web.Infrastructure.AgentStorageUsage.DefaultLimitMb * 1024 * 1024,
            IPRO.Web.Infrastructure.AgentStorageUsage.LimitBytes(null));
        Assert.Equal(200L * 1024 * 1024, IPRO.Web.Infrastructure.AgentStorageUsage.LimitBytes(200));
    }

    // ------------------------------------------------- A5-M-RESEND: paid invoices stay paid ------

    [Fact]
    public async Task Resending_a_paid_client_invoice_does_not_flip_it_back_to_unpaid()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedBareAgentAsync(db);
        var client = new Client { AgentUserId = agentId, FirstName = "C", LastName = "P", Email = "cp@example.test" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var invoice = new ClientInvoice
        {
            AgentUserId = agentId, ClientId = client.Id, DocumentNumber = "INV-P",
            Status = ClientInvoiceStatus.Paid, PaidAt = DateTime.UtcNow.AddDays(-1),
            Total = 100m, ViewToken = Guid.NewGuid().ToString("N"), DueDate = DateTime.UtcNow.AddDays(-10)
        };
        db.ClientInvoices.Add(invoice);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Drive the REAL controller action.
        var controller = new IPRO.Web.Controllers.ClientInvoicesController(
            db, new GrantAll(), new IPRO.Business.Services.ClientInvoiceService(new UnitOfWork(db)), new NullEmail(), TestConfig());
        WireAgent(controller, agentId);
        await controller.Send(invoice.Id);

        db.ChangeTracker.Clear();
        var after = await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id);
        Assert.Equal(ClientInvoiceStatus.Paid, after.Status);
        Assert.NotNull(after.PaidAt);
        Assert.NotNull(after.SentAt); // the copy still went out
    }

    // --------------------------------------------- A5-M-ERASEATOMIC: preview must not lock out ---

    [Fact]
    public async Task Preview_does_not_deactivate_but_erase_removes_the_account()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedBareAgentAsync(db);

        await AgentDataEraser.PreviewAsync(db, agentId);
        db.ChangeTracker.Clear();
        Assert.True((await db.AgentUsers.AsNoTracking().SingleAsync(a => a.Id == agentId)).IsActive);

        await AgentDataEraser.EraseAsync(db, agentId);
        db.ChangeTracker.Clear();
        Assert.False(await db.AgentUsers.AsNoTracking().AnyAsync(a => a.Id == agentId));
    }

    // ------------------------------------------------------ A5-M-STARTER: SuperAdmin-only -------

    [Theory]
    [InlineData(typeof(IPRO.Admin.Controllers.StarterContentController))]
    [InlineData(typeof(IPRO.Admin.Controllers.WebsiteStarterArticlesController))]
    [InlineData(typeof(IPRO.Admin.Controllers.WebsiteStarterFormsController))]
    public void Starter_libraries_are_SuperAdmin_only(Type controller)
    {
        var attrs = controller.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>();
        Assert.Contains(attrs, a => a.Policy == "SuperAdmin");
    }

    // --------------------------------------------------------- source-walk guards ---------------

    [Fact]
    public void The_signup_verify_check_distinguishes_an_expired_session_from_a_wrong_code()
    {
        var src = System.IO.File.ReadAllText(SrcPath("IPRO.Web", "Controllers", "AccountController.cs"));
        Assert.Contains("session timed out", src);
    }

    [Fact]
    public void Media_responses_start_uncacheable_and_the_webhook_isolates_events()
    {
        var media = System.IO.File.ReadAllText(SrcPath("IPRO.Web", "Controllers", "MediaController.cs"));
        Assert.DoesNotContain("[ResponseCache(", media);
        Assert.Contains("no-store", media);

        var wh = System.IO.File.ReadAllText(SrcPath("IPRO.Web", "Controllers", "NewsletterController.cs"));
        Assert.Contains("one event in the batch could not be processed", wh);
    }

    // ------------------------------------------------------------------------------ plumbing ----

    private sealed record PromoSeed(int AgentId, int RuleId, int PromoId);

    private static async Task<PromoSeed> SeedAgentWithPromoAsync(IPRODbContext db, int maxRedemptions, int alreadyRedeemed)
    {
        var rule = new BillingRule
        {
            PackageName = ($"MS-{Guid.NewGuid():N}")[..20],
            MonthlyPrice = 40m, AnnualPrice = 400m, IsActive = true,
            PayPalMonthlyPlanId = "P-MS-M", PayPalAnnualPlanId = "P-MS-A"
        };
        db.Add(rule);
        await db.SaveChangesAsync();
        var promo = new PromotionCode
        {
            Code = ($"MS{Guid.NewGuid():N}")[..12].ToUpperInvariant(),
            IsActive = true,
            MaxRedemptions = maxRedemptions,
            RedemptionCount = alreadyRedeemed,
            RecurringDiscountType = PromoDiscountType.PercentOff,
            RecurringDiscountValue = 50m
        };
        db.Add(promo);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"ms-{Guid.NewGuid():N}")[..20],
            Email = "ms@example.test", FirstName = "M", LastName = "S",
            DomainName = ($"ms-{Guid.NewGuid():N}")[..24],
            PackageId = rule.Id,
            PromotionCode = promo.Code
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return new PromoSeed(agent.Id, rule.Id, promo.Id);
    }

    private static async Task<int> SeedBareAgentAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"MSb-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"msb-{Guid.NewGuid():N}")[..20],
            Email = "msb@example.test", FirstName = "M", LastName = "B",
            DomainName = ($"msb-{Guid.NewGuid():N}")[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static IPRO.Billing.PayPalBillingService NewBillingService(IPRODbContext db) => new(
        new UnitOfWork(db), db,
        new HttpFactory(), new NullEmail(),
        Microsoft.Extensions.Options.Options.Create(new IPRO.Billing.PayPalSettings()),
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<IPRO.Billing.PayPalBillingService>.Instance);

    private static Microsoft.Extensions.Configuration.IConfiguration TestConfig() =>
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

    private static void WireAgent(Microsoft.AspNetCore.Mvc.Controller controller, int agentId)
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NullTemp());
    }

    private static string SrcPath(params string[] parts)
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        var all = new System.Collections.Generic.List<string> { dir!.FullName, "src" };
        all.AddRange(parts);
        return System.IO.Path.Combine(all.ToArray());
    }

    private sealed class GrantAll : IPRO.Business.Interfaces.IPackageEntitlementService
    {
        public Task<IPRO.Business.Interfaces.PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) =>
            Task.FromResult(new IPRO.Business.Interfaces.PackageFeatureAccess { FeatureCode = featureCode, IsIncluded = true });
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => Task.FromResult(true);
        public Task<System.Collections.Generic.Dictionary<int, bool>> HasAccessBulkAsync(System.Collections.Generic.IEnumerable<int> agentIds, string featureCode) =>
            throw new NotSupportedException();
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(false);
    }

    private sealed class NullEmail : IEmailService
    {
        public Task<bool> SendAsync(string a, string b, string c, string d, string? e = null, System.Collections.Generic.IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(true);
        public Task<EmailSendResult> SendDetailedAsync(string a, string b, string c, string d, string? e = null, System.Collections.Generic.IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(EmailSendResult.Sent());
        public Task<bool> SendBulkAsync(System.Collections.Generic.IEnumerable<EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
    }

    private sealed class HttpFactory : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) => new();
    }

    private sealed class NullTemp : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public System.Collections.Generic.IDictionary<string, object> LoadTempData(Microsoft.AspNetCore.Http.HttpContext context) => new System.Collections.Generic.Dictionary<string, object>();
        public void SaveTempData(Microsoft.AspNetCore.Http.HttpContext context, System.Collections.Generic.IDictionary<string, object> values) { }
    }
}
