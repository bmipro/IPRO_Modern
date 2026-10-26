using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Web.Controllers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 596 (.NET 10): C# 14 binds `array.Contains(x)` inside a query to MemoryExtensions.Contains, a span method, where
// C# 12 bound Enumerable.Contains. EF Core 8.0.2 cannot translate that call, so every such query threw at run time
// ("could not be translated") -- the package seeder at every start of both apps among them. EF Core 8.0.15 and later
// translate it (dotnet/efcore#35720); 8.0.32 is pinned. These are all such queries in iPro's code at 596 (found in
// the compiled code: an expression tree that names a MemoryExtensions method), each run against MySQL with its
// answer checked. A List<T> keeps List<T>.Contains (WebsiteController's domain variants) and is not affected.
public class ArrayContainsQueries596Tests
{
    private const string Zone = "247advisers.com";

    [Fact]
    public async Task An_array_lookup_in_a_query_is_the_span_method_now_and_still_reaches_MySQL_as_IN()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var first = await AgentAsync(db);
        var second = await AgentAsync(db);
        await AgentAsync(db);

        var ids = new[] { first, second };
        Expression<Func<AgentUser, bool>> lookup = a => ids.Contains(a.Id);
        // What C# 14 makes of it, and so the reason for the pin: with Enumerable.Contains here it would not matter.
        Assert.Equal(typeof(MemoryExtensions), ((MethodCallExpression)lookup.Body).Method.DeclaringType);

        var query = db.AgentUsers.Where(lookup).OrderBy(a => a.Id).Select(a => a.Id);
        Assert.Contains(" IN (", query.ToQueryString());
        Assert.Equal(ids, await query.ToArrayAsync());
    }

    [Fact]
    public async Task The_package_seeder_finds_its_packages_and_retires_a_withdrawn_feature()
    {
        // PackageEntitlementSeeder.cs:99 (names.Contains) and :298 (RetiredFeatureCodes.Contains). It runs at every
        // start of both apps, so on EF Core 8.0.2 neither would have started.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await PackageEntitlementSeeder.SeedAsync(db);
        var silver = await db.BillingRules.SingleAsync(r => r.PackageName == "IPro Silver");
        db.PackageFeatures.Add(new PackageFeature { BillingRuleId = silver.Id, FeatureCode = "mail_merge", FeatureName = "Mail merge", IsIncluded = true });
        await db.SaveChangesAsync();
        var packages = await db.BillingRules.CountAsync();
        db.ChangeTracker.Clear();

        await PackageEntitlementSeeder.SeedAsync(db);   // the next start

        Assert.Equal(packages, await db.BillingRules.CountAsync());
        Assert.False(await db.PackageFeatures.AnyAsync(f => f.FeatureCode == "mail_merge"));
    }

    [Fact]
    public async Task The_admin_dashboard_counts_the_open_support_tickets()
    {
        // AdminDashboardController.cs:43, 48 and 49 (openStatuses.Contains).
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agent = await AgentAsync(db);
        var open = Ticket(agent, SupportTicketStatus.Open, unreadForAdmin: true);
        var inProgress = Ticket(agent, SupportTicketStatus.InProgress, unreadForAdmin: false);
        db.SupportTickets.AddRange(open, inProgress, Ticket(agent, SupportTicketStatus.Resolved, unreadForAdmin: true));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var uow = new UnitOfWork(db);
        var controller = new IPRO.Admin.Controllers.AdminDashboardController(new AgentService(uow, new PasswordHasher<AgentUser>()), uow, db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) } }
        };
        await controller.Index();

        var listed = Assert.IsType<List<SupportTicket>>(controller.ViewData["OpenTickets"]);
        Assert.Equal(new[] { open.Id, inProgress.Id }, listed.Select(t => t.Id));   // the unanswered one first
        Assert.Equal(2, controller.ViewData["OpenTicketCount"]);
        Assert.Equal(1, controller.ViewData["AwaitingReplyCount"]);
    }

    [Fact]
    public async Task The_client_portal_sign_in_finds_the_adviser_of_a_free_site_and_of_a_proven_domain()
    {
        // ClientPortalSignInScope.cs:58 (the free site) and :64 (a domain row's three names).
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agent, freeSite, _) = await WebsiteWithDomainAsync(db, "contains596.example.test");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:TemporarySiteRootDomain"] = Zone }).Build();

        Assert.Equal(agent, await IPRO.Web.Infrastructure.ClientPortalSignInScope.AdviserForHostAsync(db, config, freeSite));
        Assert.Equal(agent, await IPRO.Web.Infrastructure.ClientPortalSignInScope.AdviserForHostAsync(db, config, "www.contains596.example.test"));
        Assert.Equal(agent, await IPRO.Web.Infrastructure.ClientPortalSignInScope.AdviserForHostAsync(db, config, "contains596.example.test"));
        Assert.Null(await IPRO.Web.Infrastructure.ClientPortalSignInScope.AdviserForHostAsync(db, config, "www.nobody596.example.test"));
    }

    [Fact]
    public async Task The_public_site_finds_the_website_of_a_proven_domain_and_of_a_free_site()
    {
        // PublicWebsiteController.cs:922-925 (a domain row's three names) and :962 (the free site), the lookup
        // behind every public page, form and sitemap.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (_, freeSite, website) = await WebsiteWithDomainAsync(db, "site596.example.test");

        Assert.Equal(website, (await FindWebsiteAsync(db, "www.site596.example.test"))?.Id);
        Assert.Equal(website, (await FindWebsiteAsync(db, "site596.example.test"))?.Id);
        Assert.Equal(website, (await FindWebsiteAsync(db, freeSite))?.Id);
        Assert.Null(await FindWebsiteAsync(db, "www.nobody596.example.test"));
    }

    [Fact]
    public async Task Bulk_status_and_bulk_delete_touch_only_the_chosen_leads_of_the_adviser()
    {
        // WebsiteLeadsController.cs:145 (BulkSetStatus) and :189 (DeleteLeadsAsync, behind BulkDelete).
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agent, _, website) = await WebsiteWithDomainAsync(db, "leads596.example.test");
        var (other, _, otherWebsite) = await WebsiteWithDomainAsync(db, "otherleads596.example.test");
        var leads = new[] { Lead(agent, website, "a"), Lead(agent, website, "b"), Lead(agent, website, "c"), Lead(other, otherWebsite, "theirs") };
        db.WebsiteLeads.AddRange(leads);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var (a, b, c, theirs) = (leads[0].Id, leads[1].Id, leads[2].Id, leads[3].Id);

        await Leads(db, agent).BulkSetStatus(new[] { a, b, theirs }, WebsiteLeadStatuses.Contacted);
        Assert.Equal(
            new[] { (a, WebsiteLeadStatuses.Contacted), (b, WebsiteLeadStatuses.Contacted), (c, WebsiteLeadStatuses.New), (theirs, WebsiteLeadStatuses.New) },
            (await db.WebsiteLeads.AsNoTracking().OrderBy(l => l.Id).ToListAsync()).Select(l => (l.Id, l.Status)));

        await Leads(db, agent).BulkDelete(new[] { a, theirs });
        Assert.Equal(new[] { b, c, theirs }, await db.WebsiteLeads.AsNoTracking().OrderBy(l => l.Id).Select(l => l.Id).ToArrayAsync());
    }

    // -------------------------------------------------------------------------- helpers --

    private static async Task<int> AgentAsync(IPRODbContext db)
    {
        var agent = new AgentUser
        {
            UserName = $"c596-{Guid.NewGuid():N}"[..20], Email = $"c596-{Guid.NewGuid():N}"[..14] + "@example.test",
            FirstName = "Span", LastName = "Contains", DomainName = $"c596-{Guid.NewGuid():N}"[..24] + "." + Zone,
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    // An adviser with a published website, a free site under the zone and a proven, bound domain (www. and the
    // bare name).
    private static async Task<(int Agent, string FreeSite, int Website)> WebsiteWithDomainAsync(IPRODbContext db, string root)
    {
        var agent = await AgentAsync(db);
        var template = new WebsiteTemplate { TemplateKey = $"c596-{Guid.NewGuid():N}"[..16], Name = "T", BusinessType = "All", IsActive = true };
        db.Add(template);
        await db.SaveChangesAsync();
        var website = new AgentWebsite { AgentUserId = agent, TemplateId = template.Id, SiteTitle = "Mine", IsPublished = true };
        db.Add(website);
        await db.SaveChangesAsync();
        db.Add(new AgentDomain
        {
            AgentUserId = agent, AgentWebsiteId = website.Id, DomainName = "www." + root, RootDomain = root, WwwDomain = "www." + root,
            DnsStatus = AgentDomainStatus.DnsReady, AzureBindingStatus = AgentDomainStatus.Bound, SslStatus = AgentDomainStatus.Bound,
            OwnershipVerifiedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var freeSite = (await db.AgentUsers.AsNoTracking().SingleAsync(u => u.Id == agent)).DomainName;
        db.ChangeTracker.Clear();
        return (agent, freeSite, website.Id);
    }

    private static SupportTicket Ticket(int agentId, SupportTicketStatus status, bool unreadForAdmin) => new()
    {
        AgentUserId = agentId, Subject = status.ToString(), Status = status, HasUnreadForAdmin = unreadForAdmin,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow,
    };

    private static WebsiteLead Lead(int agentId, int websiteId, string name) => new()
    {
        AgentUserId = agentId, AgentWebsiteId = websiteId, FirstName = name, LastName = "Lead", Email = $"{name}@example.test", Message = "hello",
    };

    private static async Task<AgentWebsite?> FindWebsiteAsync(IPRODbContext db, string host)
    {
        var controller = new PublicWebsiteController(
            db, new PackageEntitlementService(new UnitOfWork(db), db), new NullEmail(),
            NullLogger<PublicWebsiteController>.Instance, new ConfigurationBuilder().Build(),
            DataProtectionProvider.Create($"c596-{Guid.NewGuid():N}"), new NullBlob(),
            new MemoryCache(new MemoryCacheOptions()), new NullConsent());
        var find = typeof(PublicWebsiteController).GetMethod("FindWebsiteForHostRawAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(find);
        db.ChangeTracker.Clear();
        return await (Task<AgentWebsite?>)find!.Invoke(controller, new object[] { host, true })!;
    }

    private static WebsiteLeadsController Leads(IPRODbContext db, int agentId)
    {
        var controller = new WebsiteLeadsController(db);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, new NullTempData());
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

    private sealed class NullTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
