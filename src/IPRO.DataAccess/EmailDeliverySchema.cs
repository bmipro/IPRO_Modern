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

    public static async Task EnsureAsync(IPRODbContext db)
    {
        var ownsConnection = db.Database.GetDbConnection().State != ConnectionState.Open;
        if (ownsConnection) await db.Database.OpenConnectionAsync();
        try
        {
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
