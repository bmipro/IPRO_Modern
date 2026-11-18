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

// 528 (the owner, 2026-09-27; built 2026-11-18): the announcement bar across the top of every page of an
// adviser's website. Every plan has it (a package line SuperAdmin can untick), it is kept in the header settings,
// drawn above the header in all three designs, and a visitor's close lasts seven days or until the message changes.
public class AnnouncementBar528Tests
{
    // ---- the settings ---------------------------------------------------------------------------------

    [Fact]
    public void Old_header_settings_have_no_bar_and_a_saved_bar_survives_a_header_save()
    {
        var old = WebsiteHeaderSettings.FromJson("{\"Style\":\"compact\",\"Sticky\":true}");
        Assert.False(old.Announcement.IsShown);
        Assert.Equal("theme", old.Announcement.Style);

        old.Announcement = new WebsiteAnnouncementBar { Enabled = true, Message = "Closed Dec 24", LinkText = "Hours", LinkUrl = "/contact" }.Normalized();
        var back = WebsiteHeaderSettings.FromJson(old.ToJson());
        Assert.True(back.Announcement.IsShown);
        Assert.True(back.Announcement.HasLink);
        Assert.Equal("compact", back.Style);   // the header's own settings are untouched
    }

    [Theory]
    [InlineData("/contact", "/contact")]
    [InlineData("/seminars?when=may#top", "/seminars?when=may#top")]
    [InlineData("https://example.ca/book", "https://example.ca/book")]
    [InlineData("//evil.example/x", null)]
    [InlineData("/\\evil.example/x", null)]
    [InlineData("/a b", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("mailto:a@example.ca", null)]
    [InlineData("contact", null)]
    public void A_bar_link_is_a_page_of_the_site_or_a_full_web_address(string typed, string? kept)
    {
        Assert.Equal(kept, WebsiteAnnouncementBar.SafeLink(typed));
    }

    [Fact]
    public void The_message_is_one_line_within_the_cap_and_an_unknown_colour_falls_back_to_the_theme()
    {
        var bar = new WebsiteAnnouncementBar { Message = "Line one\r\n  line two   " + new string('x', 300), Style = "neon" }.Normalized();
        Assert.StartsWith("Line one line two x", bar.Message);
        Assert.True(bar.Message.Length <= WebsiteAnnouncementBar.MaxMessageLength);
        Assert.Equal("theme", bar.Style);
    }

    [Fact]
    public void A_new_message_has_a_new_close_key()
    {
        var a = new WebsiteAnnouncementBar { Enabled = true, Message = "Seminar May 4" }.Normalized();
        var b = new WebsiteAnnouncementBar { Enabled = true, Message = "Seminar May 11" }.Normalized();
        Assert.Matches("^[0-9a-f]{8}$", a.DismissKey);
        Assert.NotEqual(a.DismissKey, b.DismissKey);
        Assert.Equal(a.DismissKey, new WebsiteAnnouncementBar { Enabled = true, Message = "Seminar May 4" }.Normalized().DismissKey);
    }

    // ---- every plan, and SuperAdmin's untick holds ----------------------------------------------------

    [Fact]
    public async Task Every_package_gets_the_line_including_ones_the_catalogue_does_not_know_and_an_untick_is_kept()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var custom = new BillingRule { PackageName = $"Trial-{Guid.NewGuid():N}"[..20], MonthlyPrice = 0m, IsActive = true };
        db.Add(custom);
        await db.SaveChangesAsync();

        await PackageEntitlementSeeder.SeedAsync(db);
        var rows = await db.PackageFeatures.AsNoTracking().Where(f => f.FeatureCode == PackageFeatureCodes.AnnouncementBar).ToListAsync();
        Assert.Equal(await db.BillingRules.CountAsync(), rows.Count);
        Assert.All(rows, r => Assert.True(r.IsIncluded));
        Assert.Contains(rows, r => r.BillingRuleId == custom.Id);

        // SuperAdmin unticks it on the custom package; the next start-up leaves that alone and adds nothing twice.
        var row = await db.PackageFeatures.SingleAsync(f => f.BillingRuleId == custom.Id && f.FeatureCode == PackageFeatureCodes.AnnouncementBar);
        row.IsIncluded = false;
        await db.SaveChangesAsync();
        await PackageEntitlementSeeder.SeedAsync(db);
        Assert.Equal(rows.Count, await db.PackageFeatures.CountAsync(f => f.FeatureCode == PackageFeatureCodes.AnnouncementBar));
        Assert.False(await db.PackageFeatures.Where(f => f.BillingRuleId == custom.Id && f.FeatureCode == PackageFeatureCodes.AnnouncementBar).Select(f => f.IsIncluded).SingleAsync());
    }

    // ---- the live page ------------------------------------------------------------------------------

    [Fact]
    public async Task The_live_page_carries_the_bar_until_this_visitor_closes_it_and_a_new_message_shows_again()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var bar = new WebsiteAnnouncementBar { Enabled = true, Message = "Office closed Dec 24", LinkText = "Hours", LinkUrl = "/contact", Dismissible = true }.Normalized();
        var seed = await SeedSiteAsync(db, bar);

        var model = await RenderAsync(db, seed.Host, new GrantAll(true), cookie: null);
        Assert.NotNull(model.AnnouncementBar);
        Assert.Equal("Office closed Dec 24", model.AnnouncementBar!.Message);

        Assert.Null((await RenderAsync(db, seed.Host, new GrantAll(true), cookie: bar.DismissKey)).AnnouncementBar);

        // A changed message is shown again to the visitor who closed the old one.
        var site = await db.AgentWebsites.SingleAsync(w => w.Id == seed.WebsiteId);
        var settings = WebsiteHeaderSettings.FromJson(site.HeaderSettingsJson);
        settings.Announcement.Message = "Office closed Dec 24 to Jan 2";
        site.HeaderSettingsJson = settings.ToJson();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.NotNull((await RenderAsync(db, seed.Host, new GrantAll(true), cookie: bar.DismissKey)).AnnouncementBar);
    }

    [Fact]
    public async Task A_package_without_the_line_shows_no_bar()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedSiteAsync(db, new WebsiteAnnouncementBar { Enabled = true, Message = "Hello" }.Normalized());
        Assert.Null((await RenderAsync(db, seed.Host, new GrantAll(false), cookie: null)).AnnouncementBar);
    }

    [Fact]
    public async Task A_bar_that_is_off_costs_the_page_no_package_question()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedSiteAsync(db, new WebsiteAnnouncementBar { Enabled = false, Message = "Saved for later" }.Normalized());
        var counting = new GrantAll(true);
        Assert.Null((await RenderAsync(db, seed.Host, counting, cookie: null)).AnnouncementBar);
        Assert.Equal(0, counting.FeatureQuestions);
    }

    // ---- the screens --------------------------------------------------------------------------------

    [Fact]
    public void The_three_designs_draw_it_after_the_skip_link_and_before_the_header()
    {
        foreach (var shell in new[] { "_ClassicSidebar", "_EditorialVisual", "_ModernProfessional" })
        {
            var src = Read($@"src\IPRO.Web\Views\PublicWebsite\{shell}.cshtml");
            var skip = src.IndexOf("class=\"site-skip-link\"", StringComparison.Ordinal);
            var bar = src.IndexOf("@await Html.PartialAsync(\"_PublicAnnouncementBar\", Model)", StringComparison.Ordinal);
            var nav = src.IndexOf("@await Html.PartialAsync(\"_PublicNavigation\", Model)", StringComparison.Ordinal);
            Assert.True(skip >= 0 && bar > skip && nav > bar, $"{shell}: skip link, then the bar, then the header");
        }

        var partial = Read(@"src\IPRO.Web\Views\PublicWebsite\_PublicAnnouncementBar.cshtml");
        Assert.Contains("<script nonce=\"@Context.GetCspNonce()\">", partial);
        Assert.Contains("@IPRO.Entities.WebsiteAnnouncementBar.DismissCookie=", partial);
        Assert.Contains("SameSite=Lax", partial);
        Assert.Contains("aria-label=\"@SiteLanguageMarkup.CloseAnnouncement(language)\"", partial);
        Assert.DoesNotContain("Html.Raw", partial);   // plain text only

        var editor = Read(@"src\IPRO.Web\Views\WebsitePages\Navigation.cshtml");
        Assert.Contains("action=\"/portal/WebsitePages/SaveAnnouncement\"", editor);
        Assert.Contains("Show the announcement bar", editor);

        var privacy = Read(@"src\IPRO.Web\Views\Shared\_LegalPrivacy.cshtml");
        Assert.Contains("<code>ipro_bar_closed</code>", privacy);
        Assert.Equal("ipro_bar_closed", WebsiteAnnouncementBar.DismissCookie);

        Assert.Contains("## Add an Announcement Bar", Read(@"DOCS\04_WEBSITE_BUILDER.md"));
    }

    // ---- harness --------------------------------------------------------------------------------------

    private sealed record Seed(int AgentId, int WebsiteId, string Host);

    private static async Task<Seed> SeedSiteAsync(IPRODbContext db, WebsiteAnnouncementBar bar)
    {
        var rule = new BillingRule { PackageName = $"AB-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        var host = $"ab-{Guid.NewGuid():N}"[..14] + ".example.test";
        var agent = new AgentUser
        {
            UserName = $"ab-{Guid.NewGuid():N}"[..20],
            Email = $"ab-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Bar", LastName = "Site",
            DomainName = $"ab-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario"
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        agent.PackageId = rule.Id;
        db.Add(new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 60m, Status = BillingStatus.Active, Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddDays(-40), NextBillingDate = DateTime.UtcNow.AddDays(20)
        });
        var template = new WebsiteTemplate { TemplateKey = $"tk-{Guid.NewGuid():N}"[..16], Name = "Test", BusinessType = "All" };
        db.Add(template);
        await db.SaveChangesAsync();
        var header = new WebsiteHeaderSettings { Announcement = bar };
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, IsPublished = true, CustomDomain = host, HeaderSettingsJson = header.ToJson() };
        db.Add(website);
        await db.SaveChangesAsync();
        db.Add(new AgentDomain { AgentUserId = agent.Id, AgentWebsiteId = website.Id, DomainName = host, WwwDomain = host, RootDomain = host, IsPrimary = true, OwnershipVerifiedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seed(agent.Id, website.Id, host);
    }

    private static async Task<PublicWebsiteViewModel> RenderAsync(IPRODbContext db, string host, IPackageEntitlementService entitlements, string? cookie)
    {
        var controller = new PublicWebsiteController(
            db, entitlements, new NullEmail(), NullLogger<PublicWebsiteController>.Instance,
            new ConfigurationBuilder().Build(), DataProtectionProvider.Create($"t528-{Guid.NewGuid():N}"),
            new NullBlob(), new MemoryCache(new MemoryCacheOptions()), new NullConsent());
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        if (cookie != null) context.Request.Headers.Cookie = $"{WebsiteAnnouncementBar.DismissCookie}={cookie}";
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NullTempData());
        var view = Assert.IsType<ViewResult>(await controller.Index());
        return Assert.IsType<PublicWebsiteViewModel>(view.Model);
    }

    private sealed class GrantAll : IPackageEntitlementService
    {
        private readonly bool _bar;
        public int FeatureQuestions;
        public GrantAll(bool bar) { _bar = bar; }
        public Task<PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) =>
            Task.FromResult(new PackageFeatureAccess { FeatureCode = featureCode, IsIncluded = featureCode != PackageFeatureCodes.AnnouncementBar || _bar });
        public Task<bool> HasAccessAsync(int agentId, string featureCode)
        {
            if (featureCode == PackageFeatureCodes.AnnouncementBar) FeatureQuestions++;
            return Task.FromResult(featureCode != PackageFeatureCodes.AnnouncementBar || _bar);
        }
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode) => throw new NotSupportedException();
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(false);
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
