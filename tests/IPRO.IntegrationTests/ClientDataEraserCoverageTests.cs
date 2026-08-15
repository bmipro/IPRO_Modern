using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// Same contract as AgentDataEraserCoverageTests, one level down: every table with a ClientId column
// must be handled by ClientDataEraser -- deleted, unlinked, or explicitly exempted here with a
// reason -- and every table the eraser names must actually exist. A miss is silent in production
// (TableExistsAsync skips unknown names; unlisted tables simply keep their rows), so the test is
// the only thing that makes the omission loud.
public class ClientDataEraserCoverageTests
{
    private static readonly Dictionary<string, string> IntentionallyNotErased = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Clients"] = "The client row itself -- EraseAsync deletes it last, outside the maps.",
    };

    [Fact]
    public async Task Every_table_with_a_ClientId_column_is_deleted_unlinked_or_exempt()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var clientLinked = await TablesWithColumnAsync(db, "ClientId");
        Assert.NotEmpty(clientLinked);

        var covered = new HashSet<string>(ClientDataEraser.CoveredTables, StringComparer.OrdinalIgnoreCase);

        var uncovered = clientLinked
            .Where(t => !covered.Contains(t) && !IntentionallyNotErased.ContainsKey(t))
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(uncovered.Count == 0,
            "These tables have a ClientId column but no entry in ClientDataEraser's DeleteMap or UnlinkMap:\n  " +
            string.Join("\n  ", uncovered) +
            "\n\nAdd each to a map, or to IntentionallyNotErased here with the reason. An unlisted table " +
            "keeps its rows forever when a client is deleted -- the exact orphaning auditor 5's F6 found " +
            "twelve times over.");
    }

    [Fact]
    public async Task Every_table_the_client_eraser_names_actually_exists()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var real = await AllTablesAsync(db);
        var phantom = ClientDataEraser.CoveredTables
            .Where(t => !real.Contains(t))
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(phantom.Count == 0,
            "ClientDataEraser lists tables that do not exist in the schema:\n  " + string.Join("\n  ", phantom));
    }

    [Fact]
    public async Task Erasing_a_client_removes_owned_rows_unlinks_history_and_returns_blob_urls()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        // Agent + two clients: the victim and a bystander whose data must be untouched.
        var rule = new BillingRule { PackageName = $"Pkg-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m };
        db.Add(rule);
        await db.SaveChangesAsync();

        var agent = new AgentUser
        {
            UserName = $"ce-{Guid.NewGuid():N}"[..20],
            Email = "ce@example.com",
            FirstName = "Client",
            LastName = "Eraser",
            DomainName = $"ce-{Guid.NewGuid():N}"[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();

        var template = new WebsiteTemplate { TemplateKey = $"t-{Guid.NewGuid():N}"[..16], Name = "T" };
        db.Add(template);
        await db.SaveChangesAsync();
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id };
        db.Add(website);
        await db.SaveChangesAsync();

        var victim = new Client { AgentUserId = agent.Id, FirstName = "Victim", LastName = "V", Email = "v@example.com", IsNewsletterSubscribed = true };
        var bystander = new Client { AgentUserId = agent.Id, FirstName = "Bystander", LastName = "B", Email = "b@example.com", IsNewsletterSubscribed = true };
        db.Clients.AddRange(victim, bystander);
        await db.SaveChangesAsync();

        // One row in each harm category the audit called out, for both clients.
        foreach (var c in new[] { victim, bystander })
        {
            db.ClientLifeEvents.Add(new ClientLifeEvent { ClientId = c.Id, Label = "Anniversary", EventDate = DateTime.UtcNow.Date, IsActive = true });
            db.PortalDocuments.Add(new PortalDocument { ClientId = c.Id, FileName = "doc.pdf", BlobUrl = $"https://blobs/portal/{c.Id}.pdf", FileSizeBytes = 10 });
            db.WebsiteLeads.Add(new WebsiteLead { AgentUserId = agent.Id, AgentWebsiteId = website.Id, ClientId = c.Id, Email = c.Email, FirstName = c.FirstName });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var report = await ClientDataEraser.EraseAsync(db, victim.Id);

        // Owned rows: gone for the victim, intact for the bystander.
        Assert.Equal(0, await db.ClientLifeEvents.CountAsync(e => e.ClientId == victim.Id));
        Assert.Equal(1, await db.ClientLifeEvents.CountAsync(e => e.ClientId == bystander.Id));
        Assert.Equal(0, await db.PortalDocuments.CountAsync(d => d.ClientId == victim.Id));
        Assert.Equal(1, await db.PortalDocuments.CountAsync(d => d.ClientId == bystander.Id));

        // History rows: kept but unlinked -- the row survives with ClientId nulled.
        Assert.Equal(1, await db.WebsiteLeads.CountAsync(l => l.AgentUserId == agent.Id && l.ClientId == null));
        Assert.Equal(1, await db.WebsiteLeads.CountAsync(l => l.ClientId == bystander.Id));

        // The client row itself is gone; the blob URL came back for the caller to delete the file.
        Assert.False(await db.Clients.AnyAsync(c => c.Id == victim.Id));
        Assert.Contains($"https://blobs/portal/{victim.Id}.pdf", report.BlobUrls);
        Assert.DoesNotContain($"https://blobs/portal/{bystander.Id}.pdf", report.BlobUrls);
    }

    private static async Task<List<string>> TablesWithColumnAsync(IPRODbContext db, string column)
    {
        var rows = new List<string>();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT TABLE_NAME FROM information_schema.COLUMNS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND COLUMN_NAME = @column";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@column";
        parameter.Value = column;
        command.Parameters.Add(parameter);

        await db.Database.OpenConnectionAsync();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows;
    }

    private static async Task<HashSet<string>> AllTablesAsync(IPRODbContext db)
    {
        var rows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE()";

        await db.Database.OpenConnectionAsync();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows;
    }
}
