using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 472 (2026-09-10). "A client calls and says I accidentally deleted all my clients info" -- until
// now the only answer was a never-rehearsed restore of the whole production database, and the
// deleted files were gone for good. Deleting a client now moves everything the eraser would remove
// (the client, comments, follow-ups, life events, category links, portal documents, invoices with
// their lines, queued mail, and so on) into a recycle bin as a faithful snapshot, keeps the files,
// and re-links the agent's history rows (newsletter and poll sends, website leads) on restore.
// Restore is one click on the Recently Deleted page; a nightly job purges items after 30 days and
// only then deletes the files.
public class ClientRecycleBinTests
{
    [Fact]
    public async Task Deleting_a_client_moves_it_to_the_bin_and_restore_brings_everything_back()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await SeedAgentWithWebsiteAsync(db);
        var otherAgentId = (await SeedAgentWithWebsiteAsync(db)).agentId;

        var category = new ClientCategory { AgentUserId = agentId, Name = "VIP" };
        db.Add(category);
        await db.SaveChangesAsync();

        var client = new Client
        {
            AgentUserId = agentId, FirstName = "Victim", LastName = "V", Email = $"v-{Guid.NewGuid():N}@example.test",
            Phone = "416-555-0100", Notes = "Prefers email", IsNewsletterSubscribed = true
        };
        client.Categories.Add(category);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        db.Add(new ClientComment { ClientId = client.Id, Comment = "First call" });
        db.Add(new ClientComment { ClientId = client.Id, Comment = "Second call" });
        db.Add(new ClientFollowUp { ClientId = client.Id, Title = "Renewal review", DueAt = new DateTime(2026, 10, 1, 14, 0, 0, DateTimeKind.Utc) });
        db.Add(new ClientLifeEvent { ClientId = client.Id, Label = "Anniversary", EventDate = new DateTime(2026, 6, 15), IsActive = true });
        var docUrl = $"https://blobs/portal/{client.Id}-{Guid.NewGuid():N}.pdf";
        db.Add(new PortalDocument { ClientId = client.Id, FileName = "policy.pdf", BlobUrl = docUrl, ContentType = "application/pdf", FileSizeBytes = 1234 });
        var invoice = new ClientInvoice { AgentUserId = agentId, ClientId = client.Id, DocumentNumber = "INV-1001", ViewToken = Guid.NewGuid().ToString("N"), SubTotal = 100m, Total = 100m };
        db.Add(invoice);
        await db.SaveChangesAsync();
        db.Add(new ClientInvoiceLineItem { ClientInvoiceId = invoice.Id, Description = "Consultation", Quantity = 1, UnitPrice = 100m, Amount = 100m });
        var lead = new WebsiteLead { AgentUserId = agentId, AgentWebsiteId = websiteId, ClientId = client.Id, Email = client.Email, FirstName = "Victim" };
        db.Add(lead);
        await db.SaveChangesAsync();
        var originalId = client.Id;
        db.ChangeTracker.Clear();

        // ---- delete: the rows go, the files stay, the bin has a snapshot --------------------------
        var item = await ClientRecycleBin.DeleteToBinAsync(db, originalId);
        db.ChangeTracker.Clear();

        Assert.False(await db.Clients.AnyAsync(c => c.Id == originalId));
        Assert.Equal(0, await db.Set<ClientComment>().CountAsync(c => c.ClientId == originalId));
        Assert.Equal(0, await db.Set<PortalDocument>().CountAsync(d => d.ClientId == originalId));
        Assert.Equal(0, await db.ClientInvoices.CountAsync(i => i.ClientId == originalId));
        Assert.Null((await db.WebsiteLeads.SingleAsync(l => l.Id == lead.Id)).ClientId);

        var stored = await db.ClientRecycleBinItems.SingleAsync(i => i.Id == item.Id);
        Assert.Equal(agentId, stored.AgentUserId);
        Assert.Equal(originalId, stored.OriginalClientId);
        Assert.Equal("Victim V", stored.DisplayName);
        Assert.Equal(client.Email, stored.Email);
        Assert.Equal(stored.DeletedAt.AddDays(ClientRecycleBin.RetentionDays), stored.PurgeAfter);
        Assert.Contains(docUrl, JsonSerializer.Deserialize<List<string>>(stored.BlobUrlsJson)!);

        // ---- another agent cannot restore it ----------------------------------------------------
        Assert.Null(await ClientRecycleBin.RestoreAsync(db, item.Id, otherAgentId));

        // ---- restore: the client and everything under it, history re-linked --------------------
        var restoredId = await ClientRecycleBin.RestoreAsync(db, item.Id, agentId);
        Assert.NotNull(restoredId);
        db.ChangeTracker.Clear();

        var restored = await db.Clients.Include(c => c.Categories).SingleAsync(c => c.Id == restoredId!.Value);
        Assert.Equal("Victim", restored.FirstName);
        Assert.Equal(client.Email, restored.Email);
        Assert.Equal("416-555-0100", restored.Phone);
        Assert.Equal("Prefers email", restored.Notes);
        Assert.True(restored.IsNewsletterSubscribed);
        Assert.Equal(agentId, restored.AgentUserId);
        Assert.Contains(restored.Categories, c => c.Id == category.Id);

        var comments = await db.Set<ClientComment>().Where(c => c.ClientId == restoredId).OrderBy(c => c.Id).Select(c => c.Comment).ToListAsync();
        Assert.Equal(new[] { "First call", "Second call" }, comments);
        var followUp = await db.Set<ClientFollowUp>().SingleAsync(f => f.ClientId == restoredId);
        Assert.Equal("Renewal review", followUp.Title);
        Assert.Equal(new DateTime(2026, 10, 1, 14, 0, 0), followUp.DueAt);
        Assert.Equal("Anniversary", (await db.ClientLifeEvents.SingleAsync(e => e.ClientId == restoredId)).Label);
        var document = await db.Set<PortalDocument>().SingleAsync(d => d.ClientId == restoredId);
        Assert.Equal(docUrl, document.BlobUrl);
        Assert.Equal(1234, document.FileSizeBytes);
        var restoredInvoice = await db.ClientInvoices.SingleAsync(i => i.ClientId == restoredId);
        Assert.Equal("INV-1001", restoredInvoice.DocumentNumber);
        Assert.Equal(100m, restoredInvoice.Total);
        var line = await db.Set<ClientInvoiceLineItem>().SingleAsync(l => l.ClientInvoiceId == restoredInvoice.Id);
        Assert.Equal("Consultation", line.Description);
        Assert.Equal(restoredId, (await db.WebsiteLeads.SingleAsync(l => l.Id == lead.Id)).ClientId);

        Assert.False(await db.ClientRecycleBinItems.AnyAsync(i => i.Id == item.Id));
        Assert.Null(await ClientRecycleBin.RestoreAsync(db, item.Id, agentId));
    }

    [Fact]
    public async Task The_nightly_purge_removes_expired_items_and_only_then_deletes_their_files()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, _) = await SeedAgentWithWebsiteAsync(db);

        var expired = new ClientRecycleBinItem
        {
            AgentUserId = agentId, OriginalClientId = 1, DisplayName = "Old", Email = "old@example.test",
            DeletedAt = DateTime.UtcNow.AddDays(-31), PurgeAfter = DateTime.UtcNow.AddDays(-1),
            PayloadJson = "{}", BlobUrlsJson = JsonSerializer.Serialize(new[] { "https://blobs/portal/old.pdf" })
        };
        var fresh = new ClientRecycleBinItem
        {
            AgentUserId = agentId, OriginalClientId = 2, DisplayName = "Recent", Email = "recent@example.test",
            DeletedAt = DateTime.UtcNow.AddDays(-2), PurgeAfter = DateTime.UtcNow.AddDays(28),
            PayloadJson = "{}", BlobUrlsJson = JsonSerializer.Serialize(new[] { "https://blobs/portal/recent.pdf" })
        };
        db.AddRange(expired, fresh);
        await db.SaveChangesAsync();

        var blobs = new RecordingBlobStore();
        await new IPRO.Scheduler.ClientRecycleBinPurgeJob(db, blobs, NullLogger<IPRO.Scheduler.ClientRecycleBinPurgeJob>.Instance).RunAsync();
        db.ChangeTracker.Clear();

        Assert.False(await db.ClientRecycleBinItems.AnyAsync(i => i.Id == expired.Id));
        Assert.True(await db.ClientRecycleBinItems.AnyAsync(i => i.Id == fresh.Id));
        Assert.Equal(new[] { "https://blobs/portal/old.pdf" }, blobs.Deleted);
    }

    [Fact]
    public void The_portal_the_schema_repair_the_purge_job_and_the_eraser_are_wired()
    {
        var controller = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\ClientsController.cs"));
        Assert.Contains("ClientRecycleBin.DeleteToBinAsync(", controller);
        Assert.Contains("ClientRecycleBin.RestoreAsync(", controller);
        Assert.Contains("public async Task<IActionResult> RecycleBin(", controller);

        var details = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Clients\Details.cshtml"));
        Assert.DoesNotContain("permanently?", details);
        Assert.Contains("Recently Deleted", details);
        var index = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Clients\Index.cshtml"));
        Assert.Contains("/portal/Clients/RecycleBin", index);

        var repair = File.ReadAllText(FindRepoFile(@"src\IPRO.DataAccess\StartupSchemaRepair.cs"));
        Assert.Contains("CREATE TABLE IF NOT EXISTS `ClientRecycleBinItems`", repair);
        foreach (var app in new[] { @"src\IPRO.Web\Program.cs", @"src\IPRO.Admin\Program.cs" })
            Assert.Contains("EnsureClientRecycleBinSchemaAsync", File.ReadAllText(FindRepoFile(app)));
        Assert.Contains("RecurringJob.AddOrUpdate<ClientRecycleBinPurgeJob>(\"client-recycle-bin-purge\"", File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Program.cs")));

        var eraser = File.ReadAllText(FindRepoFile(@"src\IPRO.DataAccess\AgentDataEraser.cs"));
        Assert.Contains("(\"ClientRecycleBinItems\",", eraser);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static async Task<(int agentId, int websiteId)> SeedAgentWithWebsiteAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T472-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t472-{Guid.NewGuid():N}")[..20], Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Bin", LastName = "Agent", CompanyName = "Bin Co",
            DomainName = ($"t472-{Guid.NewGuid():N}")[..24], PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var template = new WebsiteTemplate { TemplateKey = ($"t472-{Guid.NewGuid():N}")[..16], Name = "T472" };
        db.Add(template);
        await db.SaveChangesAsync();
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id };
        db.Add(website);
        await db.SaveChangesAsync();
        return (agent.Id, website.Id);
    }

    private sealed class RecordingBlobStore : IBlobStorageService
    {
        public List<string> Deleted { get; } = new();
        public Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate) => Task.FromResult($"https://blobs/{containerName}/{fileName}");
        public Task<bool> DeleteAsync(string blobUrl) { Deleted.Add(blobUrl); return Task.FromResult(true); }
        public Task<Stream?> DownloadAsync(string blobUrl) => Task.FromResult<Stream?>(null);
        public Task<List<string>> ListAsync(string containerName) => Task.FromResult(new List<string>());
        public string GetPublicUrl(string containerName, string fileName) => $"https://blobs/{containerName}/{fileName}";
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => Task.CompletedTask;
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
