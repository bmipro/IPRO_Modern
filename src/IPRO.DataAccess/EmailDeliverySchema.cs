using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// Delivery-tracking columns for every per-recipient email table.
//
// This lives in IPRO.DataAccess, not in the two Program.cs files, on purpose. INVARIANTS.md rule 4
// says a new column must be added to BOTH src/IPRO.Web/Program.cs and src/IPRO.Admin/Program.cs
// because both apps run schema repair against the same database at startup. Every previous column
// addition satisfied that by copy-pasting the ALTER into both files -- which works right up until
// someone edits one copy. Fifteen columns across three tables is well past the size where that is
// a safe thing to duplicate, so the list lives here once and each Program.cs makes a single call.
//
// Background: NewsLetterRecipients has had DeliveredAt/OpenedAt/ClickedAt/BouncedAt since the
// newsletter tracking work. ECardRecipients, ELetterRecipients and PollRecipients never got them,
// so the SendGrid events for cards, letters and polls had nowhere to land even once the webhook
// started reading their custom args.
public static class EmailDeliverySchema
{
    // Tables that carry one row per recipient of one send. Add new senders here and they inherit
    // the full column set for free.
    private static readonly string[] RecipientTables =
    {
        "ECardRecipients",
        "ELetterRecipients",
        "PollRecipients",
        "DidYouKnowEmailQueueItems"
    };

    private static readonly (string Column, string Definition)[] TrackingColumns =
    {
        ("LastEvent",   "varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''"),
        ("DeliveredAt", "datetime(6) NULL"),
        ("OpenedAt",    "datetime(6) NULL"),
        ("ClickedAt",   "datetime(6) NULL"),
        ("BouncedAt",   "datetime(6) NULL")
    };

    // DidYouKnowEmailQueueItems started life without any of the outcome fields the other three
    // recipient tables already had, so it needs these on top of the shared tracking set.
    private static readonly (string Column, string Definition)[] DidYouKnowOutcomeColumns =
    {
        ("Status",            "varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Queued'"),
        ("SendGridMessageId", "varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''"),
        ("FailureReason",     "varchar(1000) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''")
    };

    // Email consent. Lives here rather than in a second file because it is the same problem: a
    // column list that both apps must agree on. See Client.cs for why these sit alongside the
    // existing IsNewsletterSubscribed flag instead of replacing it.
    private static readonly (string Table, string Column, string Definition)[] ConsentColumns =
    {
        ("Clients", "EmailOptOutAt",         "datetime(6) NULL"),
        ("Clients", "GreetingsOptInAt",      "datetime(6) NULL"),
        ("Clients", "EmailPreferencesToken", "varchar(80) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''"),
        // Default 0: an unsubscribe stops everything unless SuperAdmin ticks the box per design.
        ("ECardDesigns", "SendAfterUnsubscribe", "tinyint(1) NOT NULL DEFAULT 0")
    };

    // The atomic-claim marker on the four SEND tables (not the recipient tables above).
    //
    // Status alone cannot arbitrate a race: two dispatch jobs can both read Scheduled, both write
    // Sending, and both mail the whole list. ClaimedAt is the timestamp the winner stamps in the same
    // conditional UPDATE that flips the status, so a second runner's UPDATE matches nothing.
    //
    // ClaimAttempts does double duty. It bounds retries (a send that crashes the process three times
    // is retired rather than looping forever), and it guarantees the claim UPDATE always changes at
    // least one column -- Pomelo pins MySqlConnector with UseAffectedRows defaulting to true, so an
    // UPDATE whose SET is a no-op reports 0 rows and the claim would silently fail. That matters
    // exactly in the stale-reclaim case, where Status is already Sending.
    //
    // Same reasoning as DidYouKnowEmailQueueItems.ClaimedAtUtc, which has worked this way since the
    // Did You Know queue shipped; this generalises it to the other four senders.
    private static readonly string[] SendTables =
    {
        "NewsLetterSends",
        "ECards",
        "ELetters",
        "PollSends"
    };

    private static readonly (string Column, string Definition)[] SendClaimColumns =
    {
        ("ClaimedAt",     "datetime(6) NULL"),
        ("ClaimAttempts", "int NOT NULL DEFAULT 0")
    };

    public static async Task EnsureAsync(IPRODbContext db)
    {
        var ownsConnection = db.Database.GetDbConnection().State != ConnectionState.Open;
        if (ownsConnection) await db.Database.OpenConnectionAsync();
        try
        {
            foreach (var (table, column, definition) in ConsentColumns)
            {
                if (!await TableExistsAsync(db, table)) continue;
                if (await ColumnExistsAsync(db, table, column)) continue;

                await using var alter = db.Database.GetDbConnection().CreateCommand();
                alter.CommandText = $"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition};";
                await alter.ExecuteNonQueryAsync();
            }

            // Unsubscribe links are looked up by token on every click, and the token is the only
            // thing identifying the client, so it needs an index.
            await EnsureTokenIndexAsync(db);

            foreach (var table in SendTables)
            {
                if (!await TableExistsAsync(db, table)) continue;

                foreach (var (column, definition) in SendClaimColumns)
                {
                    if (await ColumnExistsAsync(db, table, column)) continue;

                    await using var alter = db.Database.GetDbConnection().CreateCommand();
                    alter.CommandText = $"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition};";
                    await alter.ExecuteNonQueryAsync();
                }

                // The stale-claim sweep filters on (Status, ClaimedAt) every time a dispatch job
                // runs. Without this it is a full scan of the send table on every pass.
                await EnsureIndexAsync(db, table, $"idx_{table.ToLowerInvariant()}_claim", "`Status`, `ClaimedAt`");
            }

            foreach (var table in RecipientTables)
            {
                // A table can legitimately be absent on a database that has not yet run the
                // CREATE TABLE pass in this same startup sequence, so skip rather than throw.
                if (!await TableExistsAsync(db, table)) continue;

                var columns = table == "DidYouKnowEmailQueueItems"
                    ? TrackingColumns.Concat(DidYouKnowOutcomeColumns)
                    : TrackingColumns;

                foreach (var (column, definition) in columns)
                {
                    if (await ColumnExistsAsync(db, table, column)) continue;

                    await using var alter = db.Database.GetDbConnection().CreateCommand();
                    // Table and column names are compile-time constants from the arrays above --
                    // never user input -- so interpolation here carries no injection risk, and
                    // MySQL does not accept parameters for identifiers anyway.
                    alter.CommandText = $"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition};";
                    await alter.ExecuteNonQueryAsync();
                }
            }
        }
        finally
        {
            if (ownsConnection) await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task EnsureTokenIndexAsync(IPRODbContext db)
    {
        if (!await ColumnExistsAsync(db, "Clients", "EmailPreferencesToken")) return;

        await using var check = db.Database.GetDbConnection().CreateCommand();
        check.CommandText =
            "SELECT COUNT(*) FROM information_schema.STATISTICS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Clients' " +
            "AND INDEX_NAME = 'idx_clients_email_preferences_token';";
        if (Convert.ToInt32(await check.ExecuteScalarAsync()) > 0) return;

        await using var create = db.Database.GetDbConnection().CreateCommand();
        create.CommandText =
            "CREATE INDEX `idx_clients_email_preferences_token` ON `Clients` (`EmailPreferencesToken`);";
        await create.ExecuteNonQueryAsync();
    }

    // Generalised from EnsureTokenIndexAsync. Identifiers come from the compile-time arrays above.
    private static async Task EnsureIndexAsync(IPRODbContext db, string table, string indexName, string columnList)
    {
        await using var check = db.Database.GetDbConnection().CreateCommand();
        check.CommandText =
            "SELECT COUNT(*) FROM information_schema.STATISTICS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND INDEX_NAME = @index;";
        var tableParameter = check.CreateParameter();
        tableParameter.ParameterName = "@table";
        tableParameter.Value = table;
        check.Parameters.Add(tableParameter);
        var indexParameter = check.CreateParameter();
        indexParameter.ParameterName = "@index";
        indexParameter.Value = indexName;
        check.Parameters.Add(indexParameter);
        if (Convert.ToInt32(await check.ExecuteScalarAsync()) > 0) return;

        await using var create = db.Database.GetDbConnection().CreateCommand();
        create.CommandText = $"CREATE INDEX `{indexName}` ON `{table}` ({columnList});";
        await create.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TableExistsAsync(IPRODbContext db, string table)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM information_schema.TABLES " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@table";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<bool> ColumnExistsAsync(IPRODbContext db, string table, string column)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM information_schema.COLUMNS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND COLUMN_NAME = @column;";
        var tableParameter = command.CreateParameter();
        tableParameter.ParameterName = "@table";
        tableParameter.Value = table;
        command.Parameters.Add(tableParameter);
        var columnParameter = command.CreateParameter();
        columnParameter.ParameterName = "@column";
        columnParameter.Value = column;
        command.Parameters.Add(columnParameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }
}
