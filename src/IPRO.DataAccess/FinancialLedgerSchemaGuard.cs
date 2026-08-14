using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.DataAccess;

// The financial ledger (Billings, Invoices, InvoiceLineItems, SubscriptionChanges) must OUTLIVE the
// agent it belongs to: CRA expects sales/tax records kept for six years, and AgentDataEraser retains
// these tables by design when an agent is deleted.
//
// The EF migrations, however, created foreign keys with ON DELETE CASCADE on exactly these tables
// (both apps run MigrateAsync at startup). On 2026-08-14 that combination destroyed a real paid
// invoice: deleting agent Bob2Mot's AgentUsers row cascaded Billings -> Invoices -> InvoiceLineItems
// at the database level, after the eraser had deliberately skipped those tables. The eraser's header
// comment claimed the schema had no FKs at all -- it was wrong (48 exist).
//
// This guard runs at startup in BOTH apps, after MigrateAsync, and drops every CASCADE constraint on
// the ledger tables. Idempotent: re-running finds nothing to drop. A fresh database (local rebuild)
// gets its FKs recreated by MigrateAsync and immediately stripped here, so no environment can cascade
// into the ledger again. RESTRICT constraints (e.g. Billings -> BillingRules) are kept: they protect
// reference data and never delete ledger rows. InvoiceLineItems -> Invoices CASCADE is also kept:
// line items must die with their invoice, and invoices themselves can no longer be cascaded into.
public static class FinancialLedgerSchemaGuard
{
    private static readonly string[] LedgerTables = { "billings", "invoices", "subscriptionchanges" };

    public static async Task EnsureAsync(IPRODbContext db, ILogger? logger = null)
    {
        // Neither phase may take the app down (see the 2026-08-01 seeder crash-loop): a failure here
        // is logged as an error and retried on the next startup.
        try
        {
            await DropLedgerCascadesAsync(db, logger);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "FinancialLedgerSchemaGuard: FAILED to drop ledger CASCADE constraints -- " +
                "agent deletion can still destroy invoices. Fix before deleting any agent.");
        }

        try
        {
            await RestoreInvoice000008Async(db, logger);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "FinancialLedgerSchemaGuard: FAILED to restore invoice IPRO-2026-000008.");
        }
    }

    private static async Task DropLedgerCascadesAsync(IPRODbContext db, ILogger? logger)
    {
        // Table names compared lowercased: Azure MySQL / Linux stores them case-folded while local
        // Windows MySQL may not, and this guard must behave identically in both.
        var toDrop = new List<(string Table, string Constraint)>();

        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText =
                "SELECT TABLE_NAME, CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS " +
                "WHERE CONSTRAINT_SCHEMA = DATABASE() AND DELETE_RULE = 'CASCADE' " +
                $"AND LOWER(TABLE_NAME) IN ('{string.Join("','", LedgerTables)}')";
            if (command.Connection!.State != System.Data.ConnectionState.Open)
                await command.Connection.OpenAsync();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                toDrop.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (table, constraint) in toDrop)
        {
            // Names come from information_schema for the current database, not from any request.
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync($"ALTER TABLE `{table}` DROP FOREIGN KEY `{constraint}`");
#pragma warning restore EF1002
            logger?.LogWarning(
                "FinancialLedgerSchemaGuard: dropped CASCADE constraint {Constraint} on {Table} -- " +
                "the financial ledger must never be deletable via cascade.", constraint, table);
        }
    }

    // One-time restoration of invoice IPRO-2026-000008 (agent Bob2Mot, deleted 2026-08-14), the
    // invoice the cascade destroyed. Every value below is reconstructed from primary evidence: the
    // invoice email the customer received (number, bill-to snapshot, line, amounts, ON 13% HST) and
    // PayPal's own records (subscription I-XEV6M0A7PHVX created 2026-08-13 21:35:33Z, activated
    // 21:36:15Z, sale 0M110368MY262274R for $67.80, cancelled 2026-08-14 13:06:27Z).
    //
    // AgentUserId 16: the real id died with the agent row and cannot be recovered. 16 is a
    // historical id verified free (deleted long before invoice retention existed, no surviving rows
    // reference it) and, being far below the table's AUTO_INCREMENT, can never be assigned to a
    // future agent. The ledger renders such rows with the "deleted" badge and prints from the
    // bill-to snapshot, so the id is bookkeeping only.
    private static async Task RestoreInvoice000008Async(IPRODbContext db, ILogger? logger)
    {
        var already = await db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Invoices WHERE PayPalTransactionId LIKE '%I-XEV6M0A7PHVX%'")
            .FirstAsync();
        if (already > 0) return;

        await db.Database.ExecuteSqlRawAsync(@"
INSERT INTO Billings (AgentUserId, BillingRuleId, PayPalSubscriptionId, PayPalPlanId, Amount, Currency, Status, Period, StartDate, CancelledAt, CreatedAt)
SELECT 16, (SELECT Id FROM BillingRules WHERE PackageName = 'IPro Gold' LIMIT 1),
       'I-XEV6M0A7PHVX', 'P-7CH55713C8636634HNJ7BLZA', 67.80, 'CAD', 2, 0,
       '2026-08-13 21:36:15', '2026-08-14 13:06:27', '2026-08-13 21:35:33'
WHERE NOT EXISTS (SELECT 1 FROM Billings WHERE PayPalSubscriptionId = 'I-XEV6M0A7PHVX')");

        await db.Database.ExecuteSqlRawAsync(@"
INSERT INTO Invoices (BillingId, AgentUserId, InvoiceNumber, SubTotal, TaxAmount, Total, Currency, PayPalTransactionId, IssuedAt, IsPaid, TaxRate, TaxRegion, BillToName, BillToCompany, BillToEmail, BillToAddress)
SELECT (SELECT Id FROM Billings WHERE PayPalSubscriptionId = 'I-XEV6M0A7PHVX' LIMIT 1),
       16, 'IPRO-2026-000008', 60.00, 7.80, 67.80, 'CAD',
       'I-XEV6M0A7PHVX, 0M110368MY262274R', '2026-08-13 21:36:15', 1, 0.13000, 'ON 13% HST',
       'Bob2 Mot', 'ABC Inc.', 'bmotamed@yahoo.com',
       CONCAT_WS('\n', '123 Front street', 'Toronto', 'Ontario M5R 1R5', 'Canada')");

        await db.Database.ExecuteSqlRawAsync(@"
INSERT INTO InvoiceLineItems (InvoiceId, Description, Amount, SortOrder, CreatedAt)
SELECT Id, 'IPro Gold monthly recurring subscription', 60.00, 1, '2026-08-13 21:36:15'
FROM Invoices WHERE InvoiceNumber = 'IPRO-2026-000008' AND AgentUserId = 16");

        logger?.LogWarning(
            "FinancialLedgerSchemaGuard: restored invoice IPRO-2026-000008 ($67.80, Bob2Mot) destroyed " +
            "by the FK cascade on 2026-08-14. Reconstructed from the invoice email and PayPal's records.");
    }
}
