using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 460 (2026-09-02). The "Your appointment has been scheduled" email said "You can review this
// anytime from your client portal" and gave no address. The owner: put the ClientPortal URL in the
// email -- https://theirdomain.com/ClientPortalAccount/Login when a domain is attached, otherwise
// https://app.iproadvisers.com/ClientPortalAccount/Login. Same resolver as the invite (457).
public class AppointmentEmailPortalLinkTests
{
    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:BaseUrl"] = "https://app.example.test",
            ["App:TemporarySiteRootDomain"] = "247advisers.test"
        })
        .Build();

    [Fact]
    public async Task The_scheduled_email_links_to_the_portal_on_the_agents_domain()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        await SeedHealthyDomainAsync(db, agentId, "www.example-adviser.test");
        var requestId = await SeedRequestAsync(db, agentId, "about the business of hosting");

        var email = new RecordingEmail();
        var controller = NewController(db, email, agentId);
        await controller.Schedule(requestId, new DateTime(2026, 9, 9, 12, 0, 0));

        var sent = Assert.Single(email.Sent);
        Assert.Equal("Your appointment has been scheduled", sent.Subject);
        Assert.Contains("https://www.example-adviser.test/ClientPortalAccount/Login", sent.Body);
        Assert.Contains("about the business of hosting", sent.Body);
        Assert.DoesNotContain("app.example.test", sent.Body);
    }

    [Fact]
    public async Task Without_an_attached_domain_the_email_links_to_the_platform_portal()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var requestId = await SeedRequestAsync(db, agentId, null);

        var email = new RecordingEmail();
        var controller = NewController(db, email, agentId);
        await controller.Schedule(requestId, new DateTime(2026, 9, 9, 12, 0, 0));

        var sent = Assert.Single(email.Sent);
        Assert.Contains("https://app.example.test/ClientPortalAccount/Login", sent.Body);
    }

    [Fact]
    public async Task The_declined_email_offers_the_portal_for_a_new_request()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        await SeedHealthyDomainAsync(db, agentId, "www.declined.test");
        var requestId = await SeedRequestAsync(db, agentId, null);

        var email = new RecordingEmail();
        var controller = NewController(db, email, agentId);
        await controller.Decline(requestId);

        var sent = Assert.Single(email.Sent);
        Assert.Equal("Your appointment request was declined", sent.Subject);
        Assert.Contains("https://www.declined.test/ClientPortalAccount/Login", sent.Body);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static IPRO.Web.Controllers.PortalRequestsController NewController(IPRODbContext db, IEmailService email, int agentId)
    {
        var controller = new IPRO.Web.Controllers.PortalRequestsController(db, email, new GrantAll(), Config());
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());
        return controller;
    }

    private static async Task<int> SeedAgentAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T460-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t460-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Appt", LastName = "Agent", CompanyName = "Appt Co",
            DomainName = ($"t460-{Guid.NewGuid():N}")[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task SeedHealthyDomainAsync(IPRODbContext db, int agentId, string host)
    {
        var template = new WebsiteTemplate { TemplateKey = ($"t460-{Guid.NewGuid():N}")[..16], Name = "T460", BusinessType = "Insurance" };
        db.Add(template);
        await db.SaveChangesAsync();
        var website = new AgentWebsite { AgentUserId = agentId, TemplateId = template.Id, SiteTitle = "Appt Co", CustomDomain = host };
        db.AgentWebsites.Add(website);
        await db.SaveChangesAsync();
        db.AgentDomains.Add(new AgentDomain
        {
            AgentUserId = agentId, AgentWebsiteId = website.Id,
            DomainName = host, WwwDomain = host, RootDomain = host.Replace("www.", ""),
            AzureBindingStatus = AgentDomainStatus.Bound, SslStatus = AgentDomainStatus.Bound, IsPrimary = true
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<int> SeedRequestAsync(IPRODbContext db, int agentId, string? notes)
    {
        var client = new Client { AgentUserId = agentId, FirstName = "iPro", LastName = "Test", Email = $"client-{Guid.NewGuid():N}@example.test" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var request = new PortalAppointmentRequest { ClientId = client.Id, Notes = notes, PreferredDate = DateTime.UtcNow.AddDays(7) };
        db.PortalAppointmentRequests.Add(request);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return request.Id;
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
}
