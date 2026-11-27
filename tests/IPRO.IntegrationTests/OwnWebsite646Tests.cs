using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 646 (the owner, 2026-11-01; price decided 2026-11-17: the same monthly prices, no setup fee; built 2026-11-18):
// "Keep your own website" -- iPro for someone who already has one. Their iPro site stays written underneath, its
// address shows a short card that points to theirs, they get a contact form for their own website, the portal's
// Website menu drops Domain and Analytics, their mail shows their own address, and checkout charges no setup fee.
public class OwnWebsite646Tests
{
    // ---- the rules, without a database ----------------------------------------------------------------

    [Theory]
    [InlineData("www.yourfirm.ca", "https://www.yourfirm.ca/")]
    [InlineData("https://yourfirm.ca/about?x=1", "https://yourfirm.ca/about")]
    [InlineData("http://old-firm.com", "http://old-firm.com/")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("yourfirm", null)]
    [InlineData("", null)]
    public void A_website_address_is_kept_as_a_full_web_address(string typed, string? kept)
    {
        Assert.Equal(kept, AgentOwnWebsite.NormalizeUrl(typed));
    }

    [Fact]
    public void Only_their_own_host_and_its_www_twin_may_frame_the_form()
    {
        Assert.Equal(new[] { "https://www.yourfirm.ca", "https://yourfirm.ca", "http://www.yourfirm.ca", "http://yourfirm.ca" },
            AgentOwnWebsite.FrameOrigins("https://www.yourfirm.ca/"));
        Assert.Empty(AgentOwnWebsite.FrameOrigins(null));
        Assert.Equal("www.yourfirm.ca", AgentOwnWebsite.DisplayHost("https://www.yourfirm.ca/"));
    }

    // ---- the choice ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_schema_repair_is_repeatable_choosing_again_keeps_the_form_key_and_switching_back_removes_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedSiteAsync(db);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE `AgentOwnWebsites`");   // production before this deploy
        await StartupSchemaRepair.EnsureOwnWebsiteSchemaAsync(db);
        await StartupSchemaRepair.EnsureOwnWebsiteSchemaAsync(db);

        Assert.Null(await OwnWebsitePreference.GetAsync(db, seed.AgentId));
        await OwnWebsitePreference.ChooseAsync(db, seed.AgentId, null);
        var first = await OwnWebsitePreference.GetAsync(db, seed.AgentId);
        Assert.NotNull(first);
        Assert.Null(first!.WebsiteUrl);
        Assert.Matches("^[0-9a-f]{32}$", first.FormKey);

        await OwnWebsitePreference.ChooseAsync(db, seed.AgentId, "www.yourfirm.ca");
        var second = await OwnWebsitePreference.GetAsync(db, seed.AgentId);
        Assert.Equal("https://www.yourfirm.ca/", second!.WebsiteUrl);
        Assert.Equal(first.FormKey, second.FormKey);   // a form already on their site keeps working
        var found = await OwnWebsitePreference.FindByFormKeyAsync(db, first.FormKey);
        Assert.NotNull(found);
        Assert.Equal(seed.AgentId, found!.Value.AgentUserId);
        Assert.Equal("https://www.yourfirm.ca/", found.Value.WebsiteUrl);
        Assert.Null(await OwnWebsitePreference.FindByFormKeyAsync(db, "not-a-key"));

        Assert.Equal("www.yourfirm.ca", await AgentSiteAddress.HostAsync(db, seed.AgentId, "temp.247advisers.com"));   // their mail shows it

        await OwnWebsitePreference.ClearAsync(db, seed.AgentId);
        Assert.Null(await OwnWebsitePreference.GetAsync(db, seed.AgentId));
        Assert.Null(await OwnWebsitePreference.FindByFormKeyAsync(db, first.FormKey));
        Assert.Equal("temp.247advisers.com", await AgentSiteAddress.HostAsync(db, seed.AgentId, "temp.247advisers.com"));
    }

    // ---- iPro's address -----------------------------------------------------------------------------

    [Fact]
    public async Task Their_iPro_address_shows_the_card_robots_are_turned_away_and_the_sitemap_is_gone()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedSiteAsync(db);

        var before = await NewController(db, seed.Host).Index();
        Assert.IsType<PublicWebsiteViewModel>(Assert.IsType<ViewResult>(before).Model);   // an ordinary site first

        await OwnWebsitePreference.ChooseAsync(db, seed.AgentId, "https://www.yourfirm.ca");
        var page = Assert.IsType<ViewResult>(await NewController(db, seed.Host).Page("about"));
        Assert.Equal("OwnWebsite", page.ViewName);
        var card = Assert.IsType<OwnWebsiteCardViewModel>(page.Model);
        Assert.Equal("Own Co", card.BusinessName);
        Assert.Equal("www.yourfirm.ca", card.WebsiteHost);
        Assert.EndsWith("/ClientPortalAccount/Login", card.ClientSignInUrl);

        var robots = Assert.IsType<ContentResult>(await NewController(db, seed.Host).Robots());
        Assert.Contains("Disallow: /", robots.Content);
        Assert.IsType<NotFoundResult>(await NewController(db, seed.Host).Sitemap());
    }

    // ---- the contact form for their own website -------------------------------------------------------

    [Fact]
    public async Task The_form_opens_by_its_key_and_only_their_own_website_may_frame_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedSiteAsync(db);
        await OwnWebsitePreference.ChooseAsync(db, seed.AgentId, "https://www.yourfirm.ca");
        var key = (await OwnWebsitePreference.GetAsync(db, seed.AgentId))!.FormKey;

        Assert.IsType<NotFoundResult>(await NewController(db, "app.example.test").LeadForm(new string('a', 32)));

        var controller = NewController(db, "app.example.test");
        controller.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        controller.Response.Headers.ContentSecurityPolicy = "default-src 'self'; frame-ancestors 'self';";
        var view = Assert.IsType<ViewResult>(await controller.LeadForm(key));
        Assert.Equal("LeadFormEmbed", view.ViewName);
        Assert.Equal("Own Co", Assert.IsType<LeadFormEmbedViewModel>(view.Model).BusinessName);
        Assert.False(controller.Response.Headers.ContainsKey("X-Frame-Options"));
        Assert.Contains("frame-ancestors 'self' https://www.yourfirm.ca https://yourfirm.ca http://www.yourfirm.ca http://yourfirm.ca;", controller.Response.Headers.ContentSecurityPolicy.ToString());
    }

    [Fact]
    public async Task A_message_becomes_a_lead_and_a_client_and_a_robot_or_a_missing_consent_makes_nothing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedSiteAsync(db);
        await OwnWebsitePreference.ChooseAsync(db, seed.AgentId, "https://www.yourfirm.ca");
        var key = (await OwnWebsitePreference.GetAsync(db, seed.AgentId))!.FormKey;

        // A robot fills the hidden field: it is thanked, and nothing is made.
        var robot = Assert.IsType<ViewResult>(await NewController(db, "app.example.test").SubmitLeadForm(key, Message(m => m.HoneypotField = "x")));
        Assert.True(Assert.IsType<LeadFormEmbedViewModel>(robot.Model).Sent);
        Assert.False(await db.WebsiteLeads.AnyAsync());

        // No consent: sent back with the reason and what was typed.
        var noConsent = Assert.IsType<LeadFormEmbedViewModel>(Assert.IsType<ViewResult>(await NewController(db, "app.example.test").SubmitLeadForm(key, Message(m => m.ConsentGiven = false))).Model);
        Assert.False(noConsent.Sent);
        Assert.Contains(noConsent.Problems, p => p.Contains("tick the box"));
        Assert.Equal("Ada", noConsent.FirstName);
        Assert.False(await db.WebsiteLeads.AnyAsync());

        // A real message.
        var sent = Assert.IsType<LeadFormEmbedViewModel>(Assert.IsType<ViewResult>(await NewController(db, "app.example.test").SubmitLeadForm(key, Message(_ => { }))).Model);
        Assert.True(sent.Sent);
        var lead = await db.WebsiteLeads.AsNoTracking().SingleAsync();
        Assert.Equal(WebsiteLeadTypes.Contact, lead.SubmissionType);
        Assert.Equal("www.yourfirm.ca", lead.SourceDomain);
        Assert.Equal("ada@example.test", lead.Email);
        Assert.Equal(seed.WebsiteId, lead.AgentWebsiteId);
        var client = await db.Clients.AsNoTracking().SingleAsync(c => c.AgentUserId == seed.AgentId);
        Assert.Equal(lead.ClientId, client.Id);

        // The same address again within five minutes makes nothing more.
        await NewController(db, "app.example.test").SubmitLeadForm(key, Message(_ => { }));
        Assert.Equal(1, await db.WebsiteLeads.CountAsync());
    }

    // ---- sign-up, billing, the portal, the marketing pages --------------------------------------------

    [Fact]
    public void Sign_up_records_the_choice_before_checkout_and_checkout_charges_no_setup_fee()
    {
        var account = Read(@"src\IPRO.Web\Controllers\AccountController.cs");
        var provisioning = account.IndexOf("WebsiteProvisioning.EnsurePublishedAsync(", StringComparison.Ordinal);
        var choice = account.IndexOf("await OwnWebsitePreference.ChooseAsync(_db, agent.Id, model.OwnWebsiteUrl);", StringComparison.Ordinal);
        var checkout = account.IndexOf("await _billing.CreateSubscriptionAsync(", StringComparison.Ordinal);
        Assert.True(provisioning > 0 && choice > provisioning && checkout > choice, "provisioning, then the choice, then checkout");

        var register = Read(@"src\IPRO.Web\Views\Account\Register.cshtml");
        Assert.Contains("name=\"KeepOwnWebsite\" value=\"true\"", register);
        Assert.Contains("data-own-label=\"no setup fee\"", register);

        Assert.Contains("if (keepsOwnWebsite) baseSetupFee = 0m;", Read(@"src\IPRO.Billing\PayPalBillingService.cs"));

        var own = new RegistrationWelcomeModel { FullName = "A B", UserName = "ab", SetupDomain = "ab.247advisers.com", KeepsOwnWebsite = true, PortalSignInUrl = "https://app.example.test/Account/Login" };
        var html = RegistrationWelcomeTemplate.BuildHtml(own);
        Assert.Contains("Your Own Website", html);
        Assert.Contains("Sign In to Your Portal", html);
        Assert.DoesNotContain("Open Your Temporary Website", html);
        Assert.Contains("Open Your Temporary Website", RegistrationWelcomeTemplate.BuildHtml(new RegistrationWelcomeModel { FullName = "A B", UserName = "ab", SetupDomain = "ab.247advisers.com" }));
    }

    [Fact]
    public void The_menu_drops_domain_and_analytics_and_the_marketing_pages_answer_the_question()
    {
        var layout = Read(@"src\IPRO.Web\Views\Shared\_Layout.cshtml");
        Assert.Contains("keepsOwnWebsite = await IPRO.DataAccess.OwnWebsitePreference.IsChosenAsync(LayoutDb, layoutAgentId);", layout);
        foreach (var href in new[] { "href=\"/portal/Website/Domains\"", "href=\"/portal/WebsiteAnalytics\"" })
        {
            var at = layout.IndexOf(href, StringComparison.Ordinal);
            var guard = layout.LastIndexOf("@if (!keepsOwnWebsite)", at, StringComparison.Ordinal);
            Assert.True(guard > 0 && at - guard < 200, $"{href} sits inside the keep-own-website guard");
        }

        var home = Read(@"src\IPRO.Web\Views\Home\Index.cshtml");
        Assert.Contains("id=\"i2-own-site\"", home);
        Assert.Contains("href=\"/Account/Register?website=own\"", home);
        Assert.Contains("id=\"i2-faq\"", home);
        Assert.True(home.IndexOf("id=\"i2-own-site\"", StringComparison.Ordinal) < home.IndexOf("class=\"i2-proof\"", StringComparison.Ordinal));
        foreach (var page in new[] { "Accountants", "Mortgage" })
            Assert.Contains("website=own", Read($@"src\IPRO.Web\Views\Home\{page}.cshtml"));

        Assert.Contains("## Keep Your Own Website", Read(@"DOCS\04_WEBSITE_BUILDER.md"));
        Assert.Contains("## Contact Form for Your Own Website", Read(@"DOCS\05_DOMAINS_AND_LEADS.md"));
    }

    // ---- harness --------------------------------------------------------------------------------------

    private static readonly IDataProtectionProvider Protection = DataProtectionProvider.Create("t646-shared");

    private static LeadFormEmbedViewModel Message(Action<LeadFormEmbedViewModel> change)
    {
        var model = new LeadFormEmbedViewModel
        {
            FirstName = "Ada", LastName = "Lovelace", Email = "Ada@Example.test", Phone = "416-555-0100", Message = "Please call me.",
            ConsentGiven = true,
            FormStartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 20,
            CaptchaToken = Protection.CreateProtector("IPRO.Web.PublicWebsite.Captcha.v1").Protect($"7|{DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30}|{Guid.NewGuid():N}"),
            CaptchaAnswer = "7"
        };
        change(model);
        return model;
    }

    private sealed record Seed(int AgentId, int WebsiteId, string Host);

    private static async Task<Seed> SeedSiteAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = $"OW-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        var host = $"ow-{Guid.NewGuid():N}"[..14] + ".example.test";
        var agent = new AgentUser
        {
            UserName = $"ow-{Guid.NewGuid():N}"[..20], Email = $"ow-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Own", LastName = "Site", CompanyName = "Own Co",
            DomainName = $"ow-{Guid.NewGuid():N}"[..24], Country = "Canada", Province = "Ontario"
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        agent.PackageId = rule.Id;
        // A package that lets the form add its sender to the professional's clients (the Contacts line).
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.Contacts, FeatureName = "Contacts", IsIncluded = true, LimitLabel = "Unlimited", SortOrder = 90, CreatedAt = DateTime.UtcNow });
        db.Add(new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 60m, Status = BillingStatus.Active, Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddDays(-40), NextBillingDate = DateTime.UtcNow.AddDays(20)
        });
        var template = new WebsiteTemplate { TemplateKey = $"tk-{Guid.NewGuid():N}"[..16], Name = "Test", BusinessType = "All" };
        db.Add(template);
        await db.SaveChangesAsync();
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, IsPublished = true, CustomDomain = host };
        db.Add(website);
        await db.SaveChangesAsync();
        db.Add(new AgentDomain { AgentUserId = agent.Id, AgentWebsiteId = website.Id, DomainName = host, WwwDomain = host, RootDomain = host, IsPrimary = true, OwnershipVerifiedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seed(agent.Id, website.Id, host);
    }

    private static PublicWebsiteController NewController(IPRODbContext db, string host)
    {
        var controller = new PublicWebsiteController(
            db, new PackageEntitlementService(new UnitOfWork(db), db), new NullEmail(), NullLogger<PublicWebsiteController>.Instance,
            new ConfigurationBuilder().Build(), Protection, new NullBlob(), new MemoryCache(new MemoryCacheOptions()), new NullConsent());
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NullTempData());
        return controller;
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
        public Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate) => Task.FromResult("https://blob.example.test/x");
        public Task<bool> DeleteAsync(string blobUrl) => Task.FromResult(true);
        public Task<Stream?> DownloadAsync(string blobUrl) => Task.FromResult<Stream?>(null);
        public Task<List<string>> ListAsync(string containerName) => Task.FromResult(new List<string>());
        public string GetPublicUrl(string containerName, string fileName) => $"https://blob.example.test/{containerName}/{fileName}";
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => Task.CompletedTask;
    }

    private sealed class NullConsent : IEmailConsentService
    {
        public bool IsSuppressed(Client client, EmailChannel channel, bool designSurvivesOptOut = false) => false;
        public Task<SuppressionResult> SuppressAllAsync(Client client, string source) => throw new NotSupportedException();
        public Task ResubscribeAsync(Client client) => throw new NotSupportedException();
        public bool LiftBounceSuppression(Client client) => throw new NotSupportedException();
        public Task<int> CancelSuppressedDripEnrollmentsAsync(int batchLimit = 500) => Task.FromResult(0);
        public Task<string> GetOrCreateTokenAsync(Client client) => Task.FromResult("tok");
        public string BuildPreferencesUrl(string token) => $"https://example.test/prefs/{token}";
    }

    private sealed class NullTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
