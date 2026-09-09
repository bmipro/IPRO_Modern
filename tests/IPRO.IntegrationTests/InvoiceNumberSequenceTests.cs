using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 418 (2026-09-09). Invoice IPRO-2026-000008 was issued twice: a full agent deletion removed
// the rows that carried 000008-000012, and the generator, which was MAX(existing)+1, handed 000008
// to the next customer. Client invoices had the same shape per agent. A number, once issued, must
// never be issued again, whatever happens to the rows: numbers now come from a counter that only
// goes up (NumberSequences), seeded from the existing maximum the first time a key is used, and
// locked per key so concurrent callers never share a value.
public class InvoiceNumberSequenceTests
{
    [Fact]
    public async Task A_client_invoice_number_is_never_reused_after_its_rows_are_deleted()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var clientId = await SeedClientAsync(db, agentId);
        var service = new IPRO.Business.Services.ClientInvoiceService(new UnitOfWork(db));

        var issued = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var number = await service.GenerateDocumentNumberAsync(agentId, ClientInvoiceDocumentType.Invoice);
            issued.Add(number);
            db.ClientInvoices.Add(new ClientInvoice { AgentUserId = agentId, ClientId = clientId, DocumentNumber = number, ViewToken = Guid.NewGuid().ToString("N"), Total = 10m });
            await db.SaveChangesAsync();
        }
        Assert.Equal(new[] { "INV-1001", "INV-1002", "INV-1003" }, issued);

        // The rows go away -- a deleted client, a shredded agent, a cleanup. The numbers do not.
        db.ClientInvoices.RemoveRange(await db.ClientInvoices.Where(i => i.AgentUserId == agentId).ToListAsync());
        await db.SaveChangesAsync();

        Assert.Equal("INV-1004", await service.GenerateDocumentNumberAsync(agentId, ClientInvoiceDocumentType.Invoice));
        // Estimates count separately, and so does another agent.
        Assert.Equal("EST-1001", await service.GenerateDocumentNumberAsync(agentId, ClientInvoiceDocumentType.Estimate));
        var otherAgent = await SeedAgentAsync(db);
        Assert.Equal("INV-1001", await service.GenerateDocumentNumberAsync(otherAgent, ClientInvoiceDocumentType.Invoice));
    }

    [Fact]
    public async Task The_platform_sequence_seeds_from_the_existing_maximum_the_first_time_and_never_looks_back()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var billingId = await SeedBillingAsync(db, agentId);
        // A database that already holds numbers from before the counter existed.
        foreach (var n in new[] { 3, 5 })
        {
            db.Invoices.Add(new Invoice { BillingId = billingId, AgentUserId = agentId, InvoiceNumber = $"IPRO-2026-{n:000000}", SubTotal = 1m, Total = 1m, IssuedAt = DateTime.UtcNow });
        }
        await db.SaveChangesAsync();

        var key = InvoiceNumbering.PlatformKey(2026);
        Assert.Equal(6, await NumberSequences.NextAsync(db, key, () => InvoiceNumbering.SeedPlatformAsync(db, "IPRO-2026-")));

        // Delete every invoice: the counter still moves forward.
        db.Invoices.RemoveRange(await db.Invoices.ToListAsync());
        await db.SaveChangesAsync();
        Assert.Equal(7, await NumberSequences.NextAsync(db, key, () => InvoiceNumbering.SeedPlatformAsync(db, "IPRO-2026-")));

        // Another year is another counter.
        Assert.Equal(1, await NumberSequences.NextAsync(db, InvoiceNumbering.PlatformKey(2027), () => InvoiceNumbering.SeedPlatformAsync(db, "IPRO-2027-")));
    }

    [Fact]
    public async Task Concurrent_callers_get_distinct_consecutive_numbers()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var key = $"test:{Guid.NewGuid():N}";

        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var db = testDb.CreateContext();
            return await NumberSequences.NextAsync(db, key, () => Task.FromResult(100L));
        });
        var values = await Task.WhenAll(tasks);

        Assert.Equal(20, values.Distinct().Count());
        Assert.Equal(101, values.Min());
        Assert.Equal(120, values.Max());
    }

    [Fact]
    public async Task The_sequence_works_inside_a_callers_transaction()
    {
        // The billing service mints invoice numbers mid-flow, sometimes inside its own transaction.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var key = $"test:{Guid.NewGuid():N}";

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            Assert.Equal(1, await NumberSequences.NextAsync(db, key, () => Task.FromResult(0L)));
            Assert.Equal(2, await NumberSequences.NextAsync(db, key, () => Task.FromResult(0L)));
            await tx.CommitAsync();
        }
        Assert.Equal(3, await NumberSequences.NextAsync(db, key, () => Task.FromResult(0L)));
    }

    [Fact]
    public void Both_generators_use_the_sequence_and_production_gets_the_table()
    {
        var billing = File.ReadAllText(FindRepoFile(@"src\IPRO.Billing\PayPalBillingService.cs"));
        Assert.Contains("NumberSequences.NextAsync(", billing);
        Assert.Contains("InvoiceNumbering.PlatformKey(", billing);

        var client = File.ReadAllText(FindRepoFile(@"src\IPRO.Business\Services\ClientInvoiceService.cs"));
        Assert.Contains("NumberSequences.NextAsync(", client);
        Assert.Contains("InvoiceNumbering.ClientKey(", client);

        var repair = File.ReadAllText(FindRepoFile(@"src\IPRO.DataAccess\StartupSchemaRepair.cs"));
        Assert.Contains("CREATE TABLE IF NOT EXISTS `NumberSequences`", repair);
        foreach (var app in new[] { @"src\IPRO.Web\Program.cs", @"src\IPRO.Admin\Program.cs" })
            Assert.Contains("EnsureNumberSequenceSchemaAsync", File.ReadAllText(FindRepoFile(app)));
    }

    // ---- harness ------------------------------------------------------------------------------

    private static async Task<int> SeedAgentAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T418-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t418-{Guid.NewGuid():N}")[..20], Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Num", LastName = "Agent", CompanyName = "Num Co",
            DomainName = ($"t418-{Guid.NewGuid():N}")[..24], PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<int> SeedClientAsync(IPRODbContext db, int agentId)
    {
        var client = new Client { AgentUserId = agentId, FirstName = "Cli", LastName = "Ent", Email = $"cli-{Guid.NewGuid():N}@example.test" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    private static async Task<int> SeedBillingAsync(IPRODbContext db, int agentId)
    {
        var agent = await db.AgentUsers.SingleAsync(a => a.Id == agentId);
        var billing = new IPRO.Entities.Billing // the IPRO.Billing namespace shadows the entity name
        {
            AgentUserId = agentId, BillingRuleId = agent.PackageId, PayPalSubscriptionId = $"I-T418-{Guid.NewGuid():N}"[..20], PayPalPlanId = "P-T418",
            Amount = 40m, Currency = "CAD", Status = BillingStatus.Active, Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddMonths(-1), CreatedAt = DateTime.UtcNow
        };
        db.Add(billing);
        await db.SaveChangesAsync();
        return billing.Id;
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
