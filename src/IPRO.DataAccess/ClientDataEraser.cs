using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// Complete removal of one client's data, driven by a declarative table map -- the same design as
// AgentDataEraser, one level down, and for the same reason (auditor 5, F6, 2026-08-15):
//
// ClientService.DeleteAsync used to load the Client with no Includes and call Remove. EF's cascade
// only covers entities it has LOADED, and only 7 of the 19 client-linked tables have database FKs,
// so the other 12 kept their rows. Worst consequences: PortalDocuments rows orphaned WITH their
// files stranded in blob storage forever (client PII), ClientLifeEvents orphans that could kill the
// reminder job for every agent, and RecurringInvoiceSchedules that failed the invoicing job daily.
//
// The delete dialog says "Delete <name> permanently?" -- that is the product promise this class
// implements. That includes the client's invoices: unlike the IPRO-level ledger (retained on agent
// deletion, owner decision 2026-08-12), deleting a client is the AGENT's own informed choice about
// their own books, made against an explicit "permanently".
//
// Tables whose schema design is ON DELETE SET NULL (newsletter sends/recipients, website leads) are
// UNLINKED here rather than deleted -- they are the agent's aggregate history, and the schema
// already chose "keep the row, drop the person". Polls follow the same rule by explicit UPDATE,
// because their tables have no FK to do it. Running the UPDATE even where a SET NULL FK exists is
// deliberate: it is idempotent, and it keeps the behaviour if the constraint is ever dropped --
// which this codebase now does on purpose (FinancialLedgerSchemaGuard).
//
// ADDING A NEW CLIENT-LINKED TABLE? Add it to DeleteMap or UnlinkMap.
// tests/IPRO.IntegrationTests/ClientDataEraserCoverageTests fails the build if you don't.
public static class ClientDataEraser
{
    // Children first. Ordering matters only for readability today, but stays correct if FKs appear.
    private static readonly (string Table, string Where)[] DeleteMap =
    {
        // -- The agent's invoicing OF this client --
        ("ClientInvoiceLineItems",      "ClientInvoiceId IN (SELECT Id FROM ClientInvoices WHERE ClientId = @clientId)"),
        ("ClientInvoices",              "ClientId = @clientId"),
        ("RecurringInvoiceLineItems",   "RecurringInvoiceScheduleId IN (SELECT Id FROM RecurringInvoiceSchedules WHERE ClientId = @clientId)"),
        ("RecurringInvoiceSchedules",   "ClientId = @clientId"),

        // -- Drip campaigns --
        ("DripCampaignStepSends",       "DripCampaignEnrollmentId IN (SELECT Id FROM DripCampaignEnrollments WHERE ClientId = @clientId)"),
        ("DripCampaignEnrollments",     "ClientId = @clientId"),

        // -- CRM records --
        ("ClientComments",              "ClientId = @clientId"),
        ("ClientFollowUps",             "ClientId = @clientId"),
        ("ClientLifeEvents",            "ClientId = @clientId"),
        ("ClientClientCategory",        "ClientsId = @clientId"),

        // -- Client portal --
        ("PortalMessages",              "ClientId = @clientId"),
        ("PortalDocuments",             "ClientId = @clientId"),
        ("PortalAppointmentRequests",   "ClientId = @clientId"),

        // -- Scheduled/queued mail addressed to this person --
        ("DidYouKnowEmailQueueItems",   "ClientId = @clientId"),
        ("ECardRecipients",             "ClientId = @clientId"),
        ("ELetterRecipients",           "ClientId = @clientId"),

        // -- Their authored content --
        ("TestimonialSubmissions",      "ClientId = @clientId"),
    };

    // Keep the row, drop the person. These are aggregate send-history/lead records whose value is
    // the AGENT's history, not the client's data. Newsletter tables and WebsiteLeads have SET NULL
    // FKs that already do this at the database level; Poll tables have no FK, so without the
    // explicit UPDATE their ids would dangle -- the exact state that made PollDispatcher silently
    // send to nobody (F7). PollAnswers survive via PollRecipients: deleting recipients would
    // destroy the agent's survey results.
    private static readonly (string Table, string Column)[] UnlinkMap =
    {
        ("NewsLetterSends",       "ClientId"),
        ("NewsLetterRecipients",  "ClientId"),
        ("WebsiteLeads",          "ClientId"),
        ("PollSends",             "ClientId"),
        ("PollRecipients",        "ClientId"),
    };

    // Files to remove from blob storage. Collected BEFORE deletion -- afterwards nothing is left to
    // read the URLs from. No shared-asset check needed here: portal documents are always private
    // per-client uploads, never library artwork.
    private static readonly (string Table, string Column, string Where)[] BlobSources =
    {
        ("PortalDocuments", "BlobUrl", "ClientId = @clientId"),
    };

    // Exposed solely for ClientDataEraserCoverageTests, which compares this against every table in
    // the live schema carrying a ClientId column.
    public static IReadOnlyList<string> CoveredTables { get; } =
        DeleteMap.Select(m => m.Table)
            .Concat(UnlinkMap.Select(m => m.Table))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public sealed record ClientErasureLine(string Table, int Rows);

    public sealed record ClientErasureReport(
        int ClientId,
        IReadOnlyList<ClientErasureLine> Deleted,
        IReadOnlyList<ClientErasureLine> Unlinked,
        IReadOnlyList<string> BlobUrls);

    // Deletes everything in the maps AND the Clients row itself, in that order, so no window exists
    // where the client is gone but their rows still resolve.
    public static async Task<ClientErasureReport> EraseAsync(IPRODbContext db, int clientId, CancellationToken ct = default)
    {
        var deleted = new List<ClientErasureLine>();
        var unlinked = new List<ClientErasureLine>();

        var blobs = new List<string>();
        foreach (var (table, column, where) in BlobSources)
        {
            if (!await TableExistsAsync(db, table, ct)) continue;
            blobs.AddRange(await StringsAsync(db,
                $"SELECT `{column}` FROM `{table}` WHERE ({where}) AND `{column}` IS NOT NULL AND `{column}` <> ''",
                clientId, ct));
        }

        // EF1002 suppressed for the same reason as AgentDataEraser: table/where are compile-time
        // constants from the maps above; the one runtime value goes through a real parameter.
        foreach (var (table, where) in DeleteMap)
        {
            if (!await TableExistsAsync(db, table, ct)) continue;
#pragma warning disable EF1002
            var rows = await db.Database.ExecuteSqlRawAsync(
                $"DELETE FROM `{table}` WHERE {where.Replace("@clientId", "{0}")}",
                new object[] { clientId }, ct);
#pragma warning restore EF1002
            if (rows > 0) deleted.Add(new ClientErasureLine(table, rows));
        }

        foreach (var (table, column) in UnlinkMap)
        {
            if (!await TableExistsAsync(db, table, ct)) continue;
#pragma warning disable EF1002
            var rows = await db.Database.ExecuteSqlRawAsync(
                $"UPDATE `{table}` SET `{column}` = NULL WHERE `{column}` = {{0}}",
                new object[] { clientId }, ct);
#pragma warning restore EF1002
            if (rows > 0) unlinked.Add(new ClientErasureLine(table, rows));
        }

#pragma warning disable EF1002
        var clientRows = await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM `Clients` WHERE Id = {0}", new object[] { clientId }, ct);
#pragma warning restore EF1002
        if (clientRows > 0) deleted.Add(new ClientErasureLine("Clients", clientRows));

        return new ClientErasureReport(clientId, deleted, unlinked, blobs.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static async Task<bool> TableExistsAsync(IPRODbContext db, string table, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT COUNT(1) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@table";
        parameter.Value = table;
        command.Parameters.Add(parameter);

        await db.Database.OpenConnectionAsync(ct);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task<List<string>> StringsAsync(IPRODbContext db, string sql, int clientId, CancellationToken ct)
    {
        var values = new List<string>();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql.Replace("@clientId", "@p0");
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@p0";
        parameter.Value = clientId;
        command.Parameters.Add(parameter);

        await db.Database.OpenConnectionAsync(ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(0)) values.Add(reader.GetString(0));
        }
        return values;
    }
}
