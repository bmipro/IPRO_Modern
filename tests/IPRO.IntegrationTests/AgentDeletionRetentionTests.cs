using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
// The IPRO.Web reference (added for controller-level security tests) pulls the IPRO.Billing
// namespace into scope, which shadows the Billing entity type. Entity uses are fully qualified
// as IPRO.Entities.Billing below rather than relying on a using-alias (which does not shadow a
// namespace in generic-argument position).
using EBilling = IPRO.Entities.Billing;

namespace IPRO.IntegrationTests;

// Regression suite for the 2026-08-14 invoice loss (DOCS/TODO.md item 417): deleting agent Bob2Mot
// destroyed his paid invoice IPRO-2026-000008 because EF-created ON DELETE CASCADE constraints
// deleted the "retained" financial ledger underneath AgentDataEraser. Every test here runs against
// a real MySQL database carrying the real migrated schema.
public class AgentDeletionRetentionTests
{
    // Documents the threat the guard exists for. If this ever fails, EF stopped creating cascade
    // paths into the ledger -- re-evaluate whether FinancialLedgerSchemaGuard is still needed
    // before deleting either.
    [Fact]
    public async Task Schema_creation_creates_cascade_paths_into_the_ledger()
    {
        await using var db = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var context = db.CreateContext();
        Assert.True(await CountLedgerCascadesAsync(context) > 0);
    }

    [Fact]
    public async Task Guard_drops_every_ledger_cascade_and_is_idempotent()
    {
        await using var db = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using (var context = db.CreateContext())
        {
            await FinancialLedgerSchemaGuard.EnsureAsync(context);
            Assert.Equal(0, await CountLedgerCascadesAsync(context));
        }
        // Second run must find nothing to drop and change nothing.
        await using (var context = db.CreateContext())
        {
            await FinancialLedgerSchemaGuard.EnsureAsync(context);
            Assert.Equal(0, await CountLedgerCascadesAsync(context));
        }
    }

    // The guard must NEVER create ledger rows. Its earlier one-time restore of invoice 000008 keyed
    // on "does this DB already have the row", which re-fabricated a phantom $67.80 invoice against a
    // nonexistent agent on every fresh/local/DR database (2026-08-14 ultra-audit). The restore is
    // gone; this pins that the guard is drop-only, even with a matching BillingRule present.
    [Fact]
    public async Task Guard_never_fabricates_any_ledger_row()
    {
        await using var db = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using (var context = db.CreateContext())
        {
            context.Add(new BillingRule { PackageName = "IPro Gold", MonthlyPrice = 60m, SetupFee = 150m });
            await context.SaveChangesAsync();

            await FinancialLedgerSchemaGuard.EnsureAsync(context);
            await FinancialLedgerSchemaGuard.EnsureAsync(context);
        }

        await using (var context = db.CreateContext())
        {
            Assert.Equal(0, await context.Set<Invoice>().CountAsync());
            Assert.Equal(0, await context.Set<EBilling>().CountAsync());
            Assert.Equal(0, await context.Set<InvoiceLineItem>().CountAsync());
        }
    }

    [Fact]
    public async Task Erase_with_retention_keeps_the_full_ledger()
    {
        await using var db = await TestDatabase.CreateAsync(applyLedgerGuard: true);
        int agentId;
        await using (var context = db.CreateContext())
        {
            agentId = await SeedAgentWithLedgerAsync(context);
        }

        AgentErasureReport report;
        await using (var context = db.CreateContext())
        {
            report = await AgentDataEraser.EraseAsync(context, agentId);
        }

        Assert.False(report.RetentionViolated);
        Assert.Equal(4, report.RetainedRows);      // billing + invoice + line item + change
        Assert.Equal(1, report.RetainedInvoices);

        await using (var context = db.CreateContext())
        {
            Assert.Equal(0, await context.Set<AgentUser>().CountAsync(a => a.Id == agentId));
            Assert.Equal(1, await context.Set<EBilling>().CountAsync(b => b.AgentUserId == agentId));
            Assert.Equal(1, await context.Set<Invoice>().CountAsync(i => i.AgentUserId == agentId));
            Assert.Equal(1, await context.Set<SubscriptionChange>().CountAsync(s => s.AgentUserId == agentId));
            Assert.Equal(1, await context.Set<InvoiceLineItem>()
                .CountAsync(l => l.Invoice.AgentUserId == agentId));
        }
    }

    // The Bob2Mot scenario itself: cascades intact (guard never ran), retention requested. The rows
    // WILL be destroyed -- the eraser's contract is that this must surface as a loud violation, not
    // as "0 retained". This test failing silent again is how we'd lose an invoice next time.
    [Fact]
    public async Task Erase_without_guard_reports_retention_violation()
    {
        await using var db = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        int agentId;
        await using (var context = db.CreateContext())
        {
            agentId = await SeedAgentWithLedgerAsync(context);
        }

        AgentErasureReport report;
        await using (var context = db.CreateContext())
        {
            report = await AgentDataEraser.EraseAsync(context, agentId);
        }

        Assert.True(report.RetentionViolated);
        Assert.Equal(4, report.RetentionShortfallRows);
    }

    [Fact]
    public async Task Full_shred_erases_the_ledger_too()
    {
        await using var db = await TestDatabase.CreateAsync(applyLedgerGuard: true);
        int agentId;
        await using (var context = db.CreateContext())
        {
            agentId = await SeedAgentWithLedgerAsync(context);
        }

        AgentErasureReport report;
        await using (var context = db.CreateContext())
        {
            report = await AgentDataEraser.EraseAsync(context, agentId, eraseFinancialRecords: true);
        }

        Assert.Empty(report.RetainedFinancial);
        Assert.False(report.RetentionViolated);

        await using (var context = db.CreateContext())
        {
            Assert.Equal(0, await context.Set<EBilling>().CountAsync(b => b.AgentUserId == agentId));
            Assert.Equal(0, await context.Set<Invoice>().CountAsync(i => i.AgentUserId == agentId));
            Assert.Equal(0, await context.Set<SubscriptionChange>().CountAsync(s => s.AgentUserId == agentId));
        }
    }

    // PreviewAsync and EraseAsync share one table map by design; this pins that they can never
    // drift apart ("what would be deleted" must equal "what was deleted").
    [Fact]
    public async Task Preview_reports_exactly_what_erase_then_deletes()
    {
        await using var db = await TestDatabase.CreateAsync(applyLedgerGuard: true);
        int agentId;
        await using (var context = db.CreateContext())
        {
            agentId = await SeedAgentWithLedgerAsync(context);
        }

        AgentErasureReport preview, erase;
        await using (var context = db.CreateContext())
        {
            preview = await AgentDataEraser.PreviewAsync(context, agentId);
        }
        await using (var context = db.CreateContext())
        {
            erase = await AgentDataEraser.EraseAsync(context, agentId);
        }

        Assert.Equal(preview.TotalRows, erase.TotalRows);
        Assert.Equal(preview.RetainedRows, erase.RetainedRows);
    }

    private static async Task<int> SeedAgentWithLedgerAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = "IPro Gold", MonthlyPrice = 60m, SetupFee = 150m };
        db.Add(rule);
        await db.SaveChangesAsync();

        var agent = new AgentUser
        {
            UserName = "retention-test-agent",
            Email = "retention.test@example.com",
            FirstName = "Retention",
            LastName = "Test",
            DomainName = $"retention-{Guid.NewGuid():N}"[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();

        var billing = new EBilling
        {
            AgentUserId = agent.Id,
            BillingRuleId = rule.Id,
            PayPalSubscriptionId = "I-RETENTIONTEST",
            Amount = 67.80m,
            Status = BillingStatus.Active,
            StartDate = DateTime.UtcNow
        };
        db.Add(billing);
        await db.SaveChangesAsync();

        var invoice = new Invoice
        {
            BillingId = billing.Id,
            AgentUserId = agent.Id,
            InvoiceNumber = "IPRO-2026-TEST01",
            SubTotal = 60m,
            TaxAmount = 7.80m,
            TaxRate = 0.13m,
            TaxRegion = "ON 13% HST",
            Total = 67.80m,
            PayPalTransactionId = "RETENTION-TEST-TXN",
            IsPaid = true
        };
        db.Add(invoice);
        await db.SaveChangesAsync();

        db.Add(new InvoiceLineItem
        {
            InvoiceId = invoice.Id,
            Description = "IPro Gold monthly recurring subscription",
            Amount = 60m,
            SortOrder = 1
        });
        db.Add(new SubscriptionChange
        {
            AgentUserId = agent.Id,
            RequestedBillingRuleId = rule.Id,
            BillingId = billing.Id,
            ChangeType = SubscriptionChangeType.Subscribe,
            Status = SubscriptionChangeStatus.Applied,
            AmountDue = 67.80m,
            EffectiveDate = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<int> CountLedgerCascadesAsync(IPRODbContext db)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS " +
            "WHERE CONSTRAINT_SCHEMA = DATABASE() AND DELETE_RULE = 'CASCADE' " +
            "AND LOWER(TABLE_NAME) IN ('billings','invoices','subscriptionchanges')";
        if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync();
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
