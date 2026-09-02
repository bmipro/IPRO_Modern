using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 457 (2026-09-02). "What is the URL to the agent's client portal?" had three answers, and none
// of them was shown to the agent once the client was active. The client-facing paths are
// never-shadowed precisely so a client uses the portal on the AGENT'S domain, yet the invite email
// pointed at the platform host and the profile page showed the agent-portal (/portal/...) form.
// Now one resolver decides the client portal's address for an agent -- their healthy custom
// domain, otherwise the platform (the owner's rule) -- and the profile card shows the sign-in
// address whether the client is invited or active ("somewhere that url need to be available").
public class ClientPortalAddressTests
{
    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:BaseUrl"] = "https://app.example.test",
            ["App:TemporarySiteRootDomain"] = "247advisers.test"
        })
        .Build();

    [Fact]
    public async Task A_healthy_custom_domain_is_the_client_portal_address()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, "bahman");
        await SeedWebsiteWithDomainAsync(db, agentId, "www.example-adviser.test", bound: true);

        var baseUrl = await ClientPortalUrls.GetBaseUrlAsync(db, agentId, Config());

        Assert.Equal("https://www.example-adviser.test", baseUrl);
        Assert.Equal("https://www.example-adviser.test/ClientPortalAccount/Login", ClientPortalUrls.LoginUrl(baseUrl));
        Assert.Equal("https://www.example-adviser.test/ClientPortalAccount/Activate?token=abc", ClientPortalUrls.ActivateUrl(baseUrl, "abc"));
    }

    [Fact]
    public async Task A_custom_domain_that_is_not_bound_yet_does_not_win()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, "pending");
        await SeedWebsiteWithDomainAsync(db, agentId, "www.not-yet.test", bound: false);

        Assert.Equal("https://app.example.test", await ClientPortalUrls.GetBaseUrlAsync(db, agentId, Config()));
    }

    [Fact]
    public async Task A_legacy_ssl_status_still_counts_as_healthy()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, "legacy");
        await SeedWebsiteWithDomainAsync(db, agentId, "www.legacy.test", bound: true, sslStatus: "SslBound");

        Assert.Equal("https://www.legacy.test", await ClientPortalUrls.GetBaseUrlAsync(db, agentId, Config()));
    }

    [Fact]
    public async Task Without_an_attached_domain_the_platform_host_is_used()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var withSubdomain = await SeedAgentAsync(db, "sub");
        var withoutSubdomain = await SeedAgentAsync(db, "");

        // The free subdomain also serves the portal, but the owner's rule for links we send is:
        // an attached domain, otherwise the platform.
        Assert.Equal("https://app.example.test", await ClientPortalUrls.GetBaseUrlAsync(db, withSubdomain, Config()));
        Assert.Equal("https://app.example.test", await ClientPortalUrls.GetBaseUrlAsync(db, withoutSubdomain, Config()));
    }

    [Fact]
    public async Task The_invite_email_sends_the_client_to_the_agents_own_domain()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, "inviter");
        await SeedWebsiteWithDomainAsync(db, agentId, "www.inviter.test", bound: true);
        var client = new Client { AgentUserId = agentId, FirstName = "Cli", LastName = "Ent", Email = $"cli-{Guid.NewGuid():N}@example.test" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var email = new RecordingEmail();
        var controller = new IPRO.Web.Controllers.ClientsController(
            null!, null!, null!, db, new GrantAll(), email, null!, null!,
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), Config());
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());

        await controller.InvitePortal(client.Id);

        var sent = Assert.Single(email.Sent);
        db.ChangeTracker.Clear();
        var token = (await db.Clients.AsNoTracking().SingleAsync(c => c.Id == client.Id)).PortalInviteToken;
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.Contains($"https://www.inviter.test/ClientPortalAccount/Activate?token={token}", sent.Body);
        Assert.DoesNotContain("app.example.test", sent.Body);
    }

    [Fact]
    public void The_profile_card_shows_the_sign_in_address_whether_invited_or_active()
    {
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Clients\Details.cshtml"));
        var card = view.Substring(view.IndexOf("Client Portal</h6>", StringComparison.Ordinal));
        card = card.Substring(0, card.IndexOf("Testimonial", StringComparison.Ordinal));

        // The address is shown in the active branch AND the invited branch, from the resolver,
        // and the old Url.Action form (which produced the /portal/... variant) is gone.
        var active = card.Substring(card.IndexOf("Active since", StringComparison.Ordinal));
        active = active.Substring(0, active.IndexOf("else if", StringComparison.Ordinal));
        Assert.Contains("ClientPortalLoginUrl", active);

        var invited = card.Substring(card.IndexOf("pending activation", StringComparison.Ordinal));
        invited = invited.Substring(0, invited.IndexOf("Invite to Portal", StringComparison.Ordinal));
        Assert.Contains("ClientPortalLoginUrl", invited);
        Assert.Contains("ClientPortalActivateUrl", invited);
        Assert.DoesNotContain("Url.Action(\"Activate\"", card);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static async Task<int> SeedAgentAsync(IPRODbContext db, string subdomain)
    {
        var rule = new BillingRule { PackageName = ($"T457-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t457-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Portal",
            LastName = "Agent",
            CompanyName = "Portal Co",
            DomainName = subdomain,
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task SeedWebsiteWithDomainAsync(IPRODbContext db, int agentId, string customDomain, bool bound, string? sslStatus = null)
    {
        var template = new WebsiteTemplate { TemplateKey = ($"t457-{Guid.NewGuid():N}")[..16], Name = "T457", BusinessType = "Insurance" };
        db.Add(template);
        await db.SaveChangesAsync();
        var website = new AgentWebsite { AgentUserId = agentId, TemplateId = template.Id, SiteTitle = "Portal Co", CustomDomain = customDomain };
        db.AgentWebsites.Add(website);
        await db.SaveChangesAsync();
        db.AgentDomains.Add(new AgentDomain
        {
            AgentUserId = agentId, AgentWebsiteId = website.Id,
            DomainName = customDomain, WwwDomain = customDomain, RootDomain = customDomain.Replace("www.", ""),
            AzureBindingStatus = bound ? AgentDomainStatus.Bound : AgentDomainStatus.BindingPending,
            SslStatus = bound ? (sslStatus ?? AgentDomainStatus.Bound) : AgentDomainStatus.BindingPending,
            IsPrimary = true
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private sealed class RecordingEmail : IEmailService
    {
        public List<(string To, string Subject, string Body)> Sent { get; } = new();
        public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;
        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        { Sent.Add((toEmail, subject, htmlBody)); return Task.FromResult(EmailSendResult.Sent("msg-1")); }
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

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
