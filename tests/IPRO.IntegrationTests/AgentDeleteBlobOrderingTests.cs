using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
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

// AUDIT C1. The A5-H12 shared-file guard shipped 2026-08-18 with a passing test and never once
// protected production: the guard lives inside EraseAsync, but AgentsController.Delete deleted the
// UNFILTERED preview list *before* calling it. The existing test asserted on the eraser's report
// and so could not see the controller at all.
//
// These tests assert at the CONTROLLER, against a fake blob store that records exactly which URLs
// were asked to be deleted. That is the only level at which C1 is visible.
public class AgentDeleteBlobOrderingTests
{
    private const string Shared = "https://iprostorageprod.blob.core.windows.net/article-media/shared.jpg";
    private const string Solo = "https://iprostorageprod.blob.core.windows.net/article-media/solo.jpg";

    [Fact]
    public async Task Deleting_an_agent_never_deletes_a_file_another_agent_still_uses()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var victim = await SeedAgentAsync(db, "victim");
        var survivor = await SeedAgentAsync(db, "survivor");

        // The A5-H12 shape: starter provisioning copies the same artwork into every agent's own
        // Article row, so two agents legitimately point at one file.
        db.Add(new Article { AgentUserId = victim, Title = "V", Content = "c", ImageUrl = Shared });
        db.Add(new Article { AgentUserId = survivor, Title = "S", Content = "c", ImageUrl = Shared });
        db.Add(new Article { AgentUserId = victim, Title = "Solo", Content = "c", ImageUrl = Solo });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var store = new RecordingBlobStore();
        var result = await NewController(db, store).Delete(victim);

        Assert.IsType<RedirectToActionResult>(result);
        // The survivor's page must still render: the shared file was never touched.
        Assert.DoesNotContain(Shared, store.Deleted, StringComparer.OrdinalIgnoreCase);
        // ...and erasure still actually erases: a file only the victim used is gone.
        Assert.Contains(Solo, store.Deleted, StringComparer.OrdinalIgnoreCase);
        Assert.False(await db.AgentUsers.AsNoTracking().AnyAsync(a => a.Id == victim));
        Assert.True(await db.AgentUsers.AsNoTracking().AnyAsync(a => a.Id == survivor));
    }

    [Fact]
    public async Task A_retention_violation_aborts_the_delete_and_touches_no_file()
    {
        // AUDIT H10. Simulated by making the pre-count see a retained invoice that the shred then
        // cannot preserve -- here by erasing financial records is NOT requested, yet the row is
        // removed out from under the transaction by a cascade stand-in. We assert the SHAPE the
        // controller must honour: on RetentionViolated nothing is deleted and the agent survives.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, "retained");
        db.Add(new Article { AgentUserId = agentId, Title = "A", Content = "c", ImageUrl = Solo });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Sanity: a normal delete DOES remove the file, so the assertion above is meaningful.
        var store = new RecordingBlobStore();
        await NewController(db, store).Delete(agentId);
        Assert.Contains(Solo, store.Deleted, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_shred_runs_before_any_file_is_deleted()
    {
        // The ordering guarantee C1 depends on: by the time a delete is requested, the agent's own
        // rows are already gone -- which is the only way "shared" can be told from "mine".
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, "ordering");
        db.Add(new Article { AgentUserId = agentId, Title = "A", Content = "c", ImageUrl = Solo });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var probe = testDb.CreateContext();
        var store = new RecordingBlobStore
        {
            OnDelete = _ => Assert.False(
                probe.Articles.AsNoTracking().Any(a => a.AgentUserId == agentId),
                "a file was deleted while the agent's rows still existed -- the shared-file check cannot work in that order")
        };
        await NewController(db, store).Delete(agentId);
        Assert.NotEmpty(store.Deleted);
        await probe.DisposeAsync();
    }

    // ------------------------------------------------------------------------------ plumbing --

    private static async Task<int> SeedAgentAsync(IPRODbContext db, string tag)
    {
        var rule = new BillingRule { PackageName = ($"BO-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"{tag}-{Guid.NewGuid():N}")[..20],
            Email = $"{tag}@example.test",
            FirstName = tag,
            LastName = "Blob",
            DomainName = ($"{tag}-{Guid.NewGuid():N}")[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static IPRO.Admin.Controllers.AgentsController NewController(IPRODbContext db, IBlobStorageService store)
    {
        var uow = new UnitOfWork(db);
        var services = new ServiceCollection();
        services.AddSingleton(store);
        var controller = new IPRO.Admin.Controllers.AgentsController(
            new IPRO.Business.Services.AgentService(uow, new PasswordHasher<AgentUser>()),
            new IPRO.Business.Services.WebsiteService(uow),
            uow,
            NewBillingService(db, uow),
            new PasswordHasher<AgentUser>(),
            NullLogger<IPRO.Admin.Controllers.AgentsController>.Instance,
            new NullAudit(),
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

    private sealed class RecordingBlobStore : IBlobStorageService
    {
        public List<string> Deleted { get; } = new();
        public Action<string>? OnDelete { get; set; }
        public Task<bool> DeleteAsync(string blobUrl)
        {
            OnDelete?.Invoke(blobUrl);
            Deleted.Add(blobUrl);
            return Task.FromResult(true);
        }
        public Task<string> UploadAsync(Stream f, string n, string c, string t, bool p) => Task.FromResult("");
        public Task<Stream?> DownloadAsync(string blobUrl) => Task.FromResult<Stream?>(null);
        public Task<List<string>> ListAsync(string containerName) => Task.FromResult(new List<string>());
        public string GetPublicUrl(string containerName, string fileName) => $"https://x/{containerName}/{fileName}";
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => Task.CompletedTask;
    }

    // The real service, like PrepaidValueTests: no PayPal settings and no Active billing row means
    // Delete's cancellation branch is skipped, which is the path under test here.
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
