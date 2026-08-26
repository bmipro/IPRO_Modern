using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// Erasure wave (2026-08-26): the delete's remaining safety gaps — H11 (Azure unbinds ran BEFORE
// the shred, so a failed shred left a customer's live domain unbound), M14 (an EraseAsync failure
// surfaced as a raw 500 with no audit entry), M15 (nothing refused deleting an agent owed an
// unresolved refund). Controller-level, like the C1 tests — the only level the ordering is
// visible at. Every defect test observed RED on pre-fix code.
public class AgentDeleteSafetyTests
{
    // ---- M15: an unresolved refund refuses the delete ----------------------------------------

    [Fact]
    public async Task M15_an_agent_owed_an_unresolved_refund_cannot_be_deleted()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, "owed");
        db.Add(new SubscriptionChange
        {
            AgentUserId = agentId,
            RequestedBillingRuleId = (await db.AgentUsers.AsNoTracking().SingleAsync(a => a.Id == agentId)).PackageId,
            ChangeType = SubscriptionChangeType.Cancel,
            Status = SubscriptionChangeStatus.Applied,
            EffectiveDate = DateTime.UtcNow.AddDays(-2),
            AppliedAt = DateTime.UtcNow.AddDays(-2),
            RefundNetAmount = 300m,
            RefundTaxAmount = 39m,
            RefundGrossAmount = 339m,
            RefundStatus = RefundStatus.Pending
        });
        db.Add(new Article { AgentUserId = agentId, Title = "A", Content = "c", ImageUrl = Solo });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var store = new RecordingBlobStore();
        var audit = new RecordingAudit();
        var result = await NewController(db, store, audit: audit).Delete(agentId);

        // Pre-fix the delete ran to completion: the $339 the business owed this person survived
        // only as a retained row nobody would ever work, or died entirely with
        // eraseFinancialRecords — and the waves made this WORSE by minting more refund rows.
        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(await db.AgentUsers.AsNoTracking().AnyAsync(a => a.Id == agentId), "agent must survive");
        Assert.True((await db.AgentUsers.AsNoTracking().SingleAsync(a => a.Id == agentId)).IsActive,
            "a refused delete must not even deactivate — nothing was attempted");
        Assert.Empty(store.Deleted);
        Assert.Contains(audit.Entries, e => e.Action == "AgentDeleteRefused" && e.Details.Contains("339"));
    }

    // ---- H11: the shred commits before any Azure unbind --------------------------------------

    [Fact]
    public async Task H11_a_failed_shred_leaves_the_customers_domain_bound()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, "bound");
        await SeedBoundDomainAsync(db, agentId, "customer-site.example");
        // A retained ledger with the FK guard absent: the shred's cascades eat the retained rows,
        // the eraser VETOES (H10) and rolls back — the proven failed-shred shape from
        // AgentDeletionRetentionTests. Pre-fix the Azure unbind had ALREADY run by then: the
        // customer's live site dark, their account "intact". Rebinding is a manual DNS-and-cert
        // crawl; that trade must never happen for a delete that did not happen.
        await SeedRetainedLedgerAsync(db, agentId);

        var azure = new RecordingAzureDomains();
        var store = new RecordingBlobStore();
        var audit = new RecordingAudit();
        await NewController(db, store, azure, audit).Delete(agentId);

        Assert.True(await db.AgentUsers.AsNoTracking().AnyAsync(a => a.Id == agentId), "veto rolled back");
        Assert.Empty(azure.Removed);   // the domain stayed bound: the site is still alive
        Assert.Empty(store.Deleted);
    }

    [Fact]
    public async Task H11_a_successful_delete_still_unbinds_the_domains()
    {
        // ADMIN-6's regression pin: moving the unbind AFTER the shred must not lose it.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, "unbind");
        await SeedBoundDomainAsync(db, agentId, "leaving.example");

        var azure = new RecordingAzureDomains();
        await NewController(db, new RecordingBlobStore(), azure).Delete(agentId);

        Assert.Contains("leaving.example", azure.Removed);
        Assert.Contains("www.leaving.example", azure.Removed);
        Assert.False(await db.AgentUsers.AsNoTracking().AnyAsync(a => a.Id == agentId));
    }

    // ---- M14: an erase failure is caught, audited, and leaves locked-out-but-intact ----------

    [Fact]
    public async Task M14_an_erase_failure_is_audited_and_reported_not_a_raw_500()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, "caught");
        db.Add(new Article { AgentUserId = agentId, Title = "A", Content = "c", ImageUrl = Solo });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        // A database-level failure mid-shred, made deterministic: deleting this agent's Articles
        // raises a signal, exactly like a lock timeout or connectivity blip would mid-transaction.
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER trg_sa_fail BEFORE DELETE ON Articles FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'simulated mid-shred failure'");

        var audit = new RecordingAudit();
        var controller = NewController(db, new RecordingBlobStore(), audit: audit);

        // Pre-fix the exception escaped the action — a raw 500, no audit entry, an admin with no
        // idea what state the agent is in.
        var ex = await Record.ExceptionAsync(() => controller.Delete(agentId));
        Assert.Null(ex);

        Assert.True(await db.AgentUsers.AsNoTracking().AnyAsync(a => a.Id == agentId), "rows intact");
        Assert.False((await db.AgentUsers.AsNoTracking().SingleAsync(a => a.Id == agentId)).IsActive,
            "locked out but intact: the lockout precedes the transaction and must survive the rollback");
        Assert.Contains(audit.Entries, e => e.Action == "AgentDeleteFailed");
        Assert.NotNull(controller.TempData["Error"]);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS trg_sa_fail");
    }

    // ------------------------------------------------------------------------------ plumbing --

    private const string Solo = "https://iprostorageprod.blob.core.windows.net/article-media/safety-solo.jpg";

    /// The proven failed-shred shape (AgentDeletionRetentionTests): retained financial rows the
    /// FK cascades will eat because the ledger guard is absent in this test database — the eraser
    /// detects the shortfall and vetoes.
    private static async Task SeedRetainedLedgerAsync(IPRODbContext db, int agentId)
    {
        var billing = new IPRO.Entities.Billing
        {
            AgentUserId = agentId,
            BillingRuleId = (await db.AgentUsers.AsNoTracking().SingleAsync(a => a.Id == agentId)).PackageId,
            Amount = 67.80m,
            Status = BillingStatus.Cancelled,
            CancelledAt = DateTime.UtcNow.AddDays(-1),
            StartDate = DateTime.UtcNow.AddDays(-30)
        };
        db.Add(billing);
        await db.SaveChangesAsync();
        var invoice = new Invoice
        {
            BillingId = billing.Id,
            AgentUserId = agentId,
            InvoiceNumber = ($"SA-{Guid.NewGuid():N}")[..18],
            SubTotal = 60m, TaxAmount = 7.80m, TaxRate = 0.13m, TaxRegion = "ON 13% HST",
            Total = 67.80m, PayPalTransactionId = "SA-TXN", IsPaid = true
        };
        db.Add(invoice);
        await db.SaveChangesAsync();
        db.Add(new InvoiceLineItem { InvoiceId = invoice.Id, Description = "SA", Amount = 60m });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// A bound custom domain needs its full parent chain: template -> website -> domain.
    private static async Task SeedBoundDomainAsync(IPRODbContext db, int agentId, string host)
    {
        var template = new WebsiteTemplate { TemplateKey = ($"sa-{Guid.NewGuid():N}")[..16], Name = "SA", BusinessType = "Mortgage" };
        db.Add(template);
        await db.SaveChangesAsync();
        var site = new AgentWebsite { AgentUserId = agentId, TemplateId = template.Id, SiteTitle = "SA" };
        db.Add(site);
        await db.SaveChangesAsync();
        db.Add(new AgentDomain
        {
            AgentUserId = agentId,
            AgentWebsiteId = site.Id,
            DomainName = host,
            WwwDomain = $"www.{host}",
            RootDomain = host,
            AzureBindingStatus = AgentDomainStatus.Bound
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<int> SeedAgentAsync(IPRODbContext db, string tag)
    {
        var rule = new BillingRule { PackageName = ($"SA-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"{tag}-{Guid.NewGuid():N}")[..20],
            Email = $"{tag}-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = tag,
            LastName = "Safety",
            DomainName = ($"{tag}-{Guid.NewGuid():N}")[..24],
            IsActive = true,
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static IPRO.Admin.Controllers.AgentsController NewController(
        IPRODbContext db, IBlobStorageService store,
        IAzureDomainAutomationService? azure = null,
        IAdminAuditLogService? audit = null)
    {
        var uow = new UnitOfWork(db);
        var services = new ServiceCollection();
        services.AddSingleton(store);
        if (azure != null) services.AddSingleton(azure);
        var controller = new IPRO.Admin.Controllers.AgentsController(
            new IPRO.Business.Services.AgentService(uow, new PasswordHasher<AgentUser>()),
            new IPRO.Business.Services.WebsiteService(uow),
            uow,
            NewBillingService(db, uow),
            new PasswordHasher<AgentUser>(),
            NullLogger<IPRO.Admin.Controllers.AgentsController>.Instance,
            audit ?? new NullAudit(),
            db,
            services.BuildServiceProvider());
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "1") }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NullTempData());
        return controller;
    }

    private sealed class RecordingAzureDomains : IAzureDomainAutomationService
    {
        public List<string> Removed { get; } = new();
        public bool IsConfigured => true;
        public Task<AzureDomainAutomationResult> EnsureDomainAsync(string hostName, CancellationToken ct = default)
            => Task.FromResult(new AzureDomainAutomationResult { Success = true, Message = "ok" });
        public Task<AzureDomainAutomationResult> RemoveDomainAsync(string hostName, CancellationToken ct = default)
        {
            Removed.Add(hostName);
            return Task.FromResult(new AzureDomainAutomationResult { Success = true, Message = "removed" });
        }
    }

    private sealed class RecordingAudit : IAdminAuditLogService
    {
        public sealed record Entry(string Action, string Details);
        public List<Entry> Entries { get; } = new();
        public Task LogAsync(int adminUserId, string adminUsername, string action, string details)
        { Entries.Add(new Entry(action, details)); return Task.CompletedTask; }
    }

    private sealed class RecordingBlobStore : IBlobStorageService
    {
        public List<string> Deleted { get; } = new();
        public Task<bool> DeleteAsync(string blobUrl) { Deleted.Add(blobUrl); return Task.FromResult(true); }
        public Task<string> UploadAsync(Stream f, string n, string c, string t, bool p) => Task.FromResult("");
        public Task<Stream?> DownloadAsync(string blobUrl) => Task.FromResult<Stream?>(null);
        public Task<List<string>> ListAsync(string containerName) => Task.FromResult(new List<string>());
        public string GetPublicUrl(string containerName, string fileName) => $"https://x/{containerName}/{fileName}";
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => Task.CompletedTask;
    }

    private static IPRO.Billing.PayPalBillingService NewBillingService(IPRODbContext db, UnitOfWork uow) => new(
        uow, db, new StubHttpFactory(), new StubEmail(),
        Microsoft.Extensions.Options.Options.Create(new IPRO.Billing.PayPalSettings()),
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
        NullLogger<IPRO.Billing.PayPalBillingService>.Instance);

    private sealed class StubHttpFactory : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) => new();
    }

    private sealed class StubEmail : IPRO.Email.IEmailService
    {
        public Task<bool> SendAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(true);
        public Task<IPRO.Email.EmailSendResult> SendDetailedAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(IPRO.Email.EmailSendResult.Sent());
        public Task<bool> SendBulkAsync(IEnumerable<IPRO.Email.EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
    }

    private sealed class NullAudit : IAdminAuditLogService
    {
        public Task LogAsync(int adminUserId, string adminUsername, string action, string details) => Task.CompletedTask;
    }

    private sealed class NullTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
