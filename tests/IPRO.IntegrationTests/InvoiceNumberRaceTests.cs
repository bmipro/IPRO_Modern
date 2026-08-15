using System;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using EBilling = IPRO.Entities.Billing;
using Xunit;

namespace IPRO.IntegrationTests;

// Auditor 5, F12. Invoice numbers are MAX(existing)+1 read from committed rows, so two concurrent
// writers can compute the same number. The unique index makes the loser throw -- AFTER PayPal has
// captured the money -- so the old behaviour was a paid charge with no invoice row: the same end
// state as the 2026-08-14 loss by a different route, and observed live on 2026-08-10 when two
// webhook resends raced.
//
// The fix in PayPalBillingService.CreateInvoiceWithLinesAsync keeps the entity tracked, regenerates
// the number and saves again. That rests on one EF assumption this test pins against the REAL
// database: after a SaveChanges rejected by the unique index, the entity is still tracked as Added
// and a second SaveChanges with a fresh number succeeds.
public class InvoiceNumberRaceTests
{
    [Fact]
    public async Task A_duplicate_number_rejection_can_be_retried_with_a_fresh_number_on_the_same_context()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: true);
        await using var db = testDb.CreateContext();

        var (agentId, billingId) = await SeedAgentWithBillingAsync(db);

        // The committed row a racing writer already inserted.
        db.Add(NewInvoice(agentId, billingId, "IPRO-2099-000001"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // The loser of the race: computes the same number from the same committed data.
        var loser = NewInvoice(agentId, billingId, "IPRO-2099-000001");
        db.Add(loser);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var mysql = Assert.IsType<MySqlException>(ex.InnerException);
        Assert.Equal(MySqlErrorCode.DuplicateKeyEntry, mysql.ErrorCode);
        Assert.Contains("InvoiceNumber", mysql.Message, StringComparison.OrdinalIgnoreCase);

        // The retry the fix performs: same context, same tracked entity, next number.
        loser.InvoiceNumber = "IPRO-2099-000002";
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var stored = await db.Database
            .SqlQueryRaw<string>("SELECT InvoiceNumber AS Value FROM Invoices WHERE AgentUserId = {0} ORDER BY InvoiceNumber", agentId)
            .ToListAsync();

        Assert.Equal(new[] { "IPRO-2099-000001", "IPRO-2099-000002" }, stored);
    }

    private static Invoice NewInvoice(int agentId, int billingId, string number) => new()
    {
        AgentUserId = agentId,
        BillingId = billingId,
        InvoiceNumber = number,
        SubTotal = 60m,
        TaxAmount = 7.8m,
        TaxRate = 0.13m,
        TaxRegion = "ON HST",
        Total = 67.8m,
        Currency = "CAD",
        IssuedAt = DateTime.UtcNow,
        IsPaid = true
    };

    private static async Task<(int AgentId, int BillingId)> SeedAgentWithBillingAsync(IPRO.DataAccess.IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = $"Pkg-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m };
        db.Add(rule);
        await db.SaveChangesAsync();

        var agent = new AgentUser
        {
            UserName = $"race-{Guid.NewGuid():N}"[..20],
            Email = "race@example.com",
            FirstName = "Race",
            LastName = "Test",
            DomainName = $"race-{Guid.NewGuid():N}"[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();

        var billing = new EBilling
        {
            AgentUserId = agent.Id,
            BillingRuleId = rule.Id,
            PayPalSubscriptionId = "I-RACETEST",
            Amount = 67.8m,
            Status = BillingStatus.Active,
            StartDate = DateTime.UtcNow
        };
        db.Add(billing);
        await db.SaveChangesAsync();

        return (agent.Id, billing.Id);
    }
}
