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

    // Runs at startup in BOTH apps, AFTER MigrateAsync (Program.cs) -- never before, or it is a no-op
    // on a fresh/restored database whose ledger tables do not exist yet.
    //
    // History: this guard also performed a one-time restore of invoice IPRO-2026-000008, destroyed by
    // the cascade on 2026-08-14, reconstructed from the invoice email + PayPal's records. That restore
    // succeeded in production and was removed on 2026-08-15: keying it on "does this DB already have
    // the row" meant it re-fabricated the invoice (against a nonexistent agent) on every fresh, local,
    // and DR database -- a phantom in the Revenue report and tax remittance (2026-08-14 ultra-audit).
    // If 000008 ever needs restoring again it is a one-off manual SQL operation, not startup code.
    public static async Task EnsureAsync(IPRODbContext db, ILogger? logger = null)
    {
        // Must not take the app down (see the 2026-08-01 seeder crash-loop): a failure here is logged
        // as an error and retried on the next startup.
        try
        {
            await DropLedgerCascadesAsync(db, logger);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "FinancialLedgerSchemaGuard: FAILED to drop ledger CASCADE constraints -- " +
                "agent deletion can still destroy invoices. Fix before deleting any agent.");
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
}
