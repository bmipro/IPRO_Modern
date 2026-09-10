using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IPRO.DataAccess;

// 472 (2026-09-10): "a client calls and says I accidentally deleted all my clients info". Deleting a
// client used to be ClientDataEraser and nothing else -- rows gone, files gone, the only way back a
// never-rehearsed restore of the whole production database. Now the deletion is snapshotted first:
// every row the eraser removes (SELECT * per table in its own map), the ids of the history rows it
// unlinks, and the files it would have deleted (kept, not deleted). The snapshot lives 30 days and
// can be restored by the agent in one click; the nightly purge removes it and only then the files.
//
// The snapshot is generic on purpose -- column names and values as the database returned them --
// so it needs no per-table code and survives new columns (a column that no longer exists on restore
// is simply not written back). Restore re-inserts parents before children, remapping each row's
// ClientId to the new client and each child's parent id through the parent's new id.
public static class ClientRecycleBin
{
    public const int RetentionDays = 30;

    // A child table whose rows point at a parent row that is ALSO in the snapshot. Its parent id is
    // remapped through the parent's new id; a child whose parent did not come back is skipped.
    private static readonly (string Table, string ParentColumn, string ParentTable)[] ChildLinks =
    {
        ("ClientInvoiceLineItems",    "ClientInvoiceId",            "ClientInvoices"),
        ("ClientInvoiceEmails",       "ClientInvoiceId",            "ClientInvoices"),
        ("RecurringInvoiceLineItems", "RecurringInvoiceScheduleId", "RecurringInvoiceSchedules"),
        ("DripCampaignStepSends",     "DripCampaignEnrollmentId",   "DripCampaignEnrollments"),
    };

    private sealed class Snapshot
    {
        public Dictionary<string, JsonElement> Client { get; set; } = new();
        public Dictionary<string, List<Dictionary<string, JsonElement>>> Tables { get; set; } = new();
        public Dictionary<string, List<long>> Unlinked { get; set; } = new();
    }

    // Snapshot, then erase, in one transaction: either the client is in the bin with everything, or
    // nothing happened.
    public static async Task<ClientRecycleBinItem> DeleteToBinAsync(IPRODbContext db, int clientId)
    {
        var client = await db.Clients.AsNoTracking().FirstAsync(c => c.Id == clientId);

        await using var transaction = await db.Database.BeginTransactionAsync();

        var clientRow = (await RowsAsync(db, "SELECT * FROM `Clients` WHERE `Id` = @p0", clientId)).Single();
        var tables = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, where) in ClientDataEraser.DeleteTables)
        {
            if (!await TableExistsAsync(db, table)) continue;
            var rows = await RowsAsync(db, $"SELECT * FROM `{table}` WHERE {where.Replace("@clientId", "@p0")}", clientId);
            if (rows.Count > 0) tables[table] = rows;
        }
        var unlinked = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, column) in ClientDataEraser.UnlinkTables)
        {
            if (!await TableExistsAsync(db, table)) continue;
            var ids = await IdsAsync(db, $"SELECT `Id` FROM `{table}` WHERE `{column}` = @p0", clientId);
            if (ids.Count > 0) unlinked[table] = ids;
        }

        var report = await ClientDataEraser.EraseAsync(db, clientId);

        var now = DateTime.UtcNow;
        var item = new ClientRecycleBinItem
        {
            AgentUserId = client.AgentUserId,
            OriginalClientId = clientId,
            DisplayName = $"{client.FirstName} {client.LastName}".Trim(),
            Email = client.Email,
            DeletedAt = now,
            PurgeAfter = now.AddDays(RetentionDays),
            PayloadJson = JsonSerializer.Serialize(new { Client = clientRow, Tables = tables, Unlinked = unlinked }),
            BlobUrlsJson = JsonSerializer.Serialize(report.BlobUrls)
        };
        db.ClientRecycleBinItems.Add(item);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return item;
    }

    // Returns the restored client's new id, or null when the item is not this agent's (or is gone).
    public static async Task<int?> RestoreAsync(IPRODbContext db, int itemId, int agentUserId)
    {
        var item = await db.ClientRecycleBinItems.FirstOrDefaultAsync(i => i.Id == itemId && i.AgentUserId == agentUserId);
        if (item == null) return null;

        Snapshot? snapshot;
        try { snapshot = JsonSerializer.Deserialize<Snapshot>(item.PayloadJson); }
        catch (JsonException) { return null; }
        if (snapshot == null || snapshot.Client.Count == 0) return null;

        await using var transaction = await db.Database.BeginTransactionAsync();

        var clientValues = ToValues(snapshot.Client, await ColumnsAsync(db, "Clients"));
        clientValues["AgentUserId"] = (long)agentUserId; // never anyone else's, whatever the snapshot says
        clientValues.Remove("Id");
        var newClientId = await InsertAsync(db, "Clients", clientValues, readNewId: true);
        if (newClientId == null)
        {
            await transaction.RollbackAsync();
            return null;
        }

        // Parents before children: the eraser's map is children-first, so walk it backwards.
        var newIds = new Dictionary<string, Dictionary<long, long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, _) in ClientDataEraser.DeleteTables.Reverse())
        {
            if (!snapshot.Tables.TryGetValue(table, out var rows) || rows.Count == 0) continue;
            var columns = await ColumnsAsync(db, table);
            if (columns.Count == 0) continue; // the table no longer exists
            var link = ChildLinks.FirstOrDefault(l => l.Table.Equals(table, StringComparison.OrdinalIgnoreCase));
            var map = new Dictionary<long, long>();

            foreach (var row in rows)
            {
                var values = ToValues(row, columns);
                if (values.ContainsKey("ClientId")) values["ClientId"] = (long)newClientId.Value;
                if (values.ContainsKey("ClientsId")) values["ClientsId"] = (long)newClientId.Value; // the category join table
                if (link.Table != null && values.TryGetValue(link.ParentColumn, out var parent) && parent is long oldParentId)
                {
                    if (!newIds.TryGetValue(link.ParentTable, out var parentMap) || !parentMap.TryGetValue(oldParentId, out var newParentId)) continue;
                    values[link.ParentColumn] = newParentId;
                }
                long? oldId = values.TryGetValue("Id", out var idValue) && idValue is long id ? id : null;
                values.Remove("Id");
                var newId = await InsertAsync(db, table, values, readNewId: oldId != null);
                if (newId != null && oldId != null) map[oldId.Value] = newId.Value;
            }
            newIds[table] = map;
        }

        // The agent's history rows that were kept but unlinked point at the person again.
        foreach (var (table, ids) in snapshot.Unlinked)
        {
            if (ids.Count == 0 || !await TableExistsAsync(db, table)) continue;
            var column = ClientDataEraser.UnlinkTables.FirstOrDefault(u => u.Table.Equals(table, StringComparison.OrdinalIgnoreCase)).Column;
            if (string.IsNullOrEmpty(column)) continue;
            await using var command = NewCommand(db);
            var placeholders = string.Join(",", ids.Select((_, i) => $"@i{i}"));
            command.CommandText = $"UPDATE `{table}` SET `{column}` = @client WHERE `Id` IN ({placeholders}) AND `{column}` IS NULL";
            AddParameter(command, "@client", (long)newClientId.Value);
            for (var i = 0; i < ids.Count; i++) AddParameter(command, $"@i{i}", ids[i]);
            await command.ExecuteNonQueryAsync();
        }

        db.ClientRecycleBinItems.Remove(item);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return (int)newClientId.Value;
    }

    // ---- capture --------------------------------------------------------------------------------

    private static async Task<List<Dictionary<string, object?>>> RowsAsync(IPRODbContext db, string sql, int clientId)
    {
        var rows = new List<Dictionary<string, object?>>();
        await using var command = NewCommand(db);
        command.CommandText = sql;
        AddParameter(command, "@p0", clientId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static async Task<List<long>> IdsAsync(IPRODbContext db, string sql, int clientId)
    {
        var ids = new List<long>();
        await using var command = NewCommand(db);
        command.CommandText = sql;
        AddParameter(command, "@p0", clientId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            if (!reader.IsDBNull(0)) ids.Add(Convert.ToInt64(reader.GetValue(0)));
        return ids;
    }

    // ---- restore --------------------------------------------------------------------------------

    // JSON back to parameter values, for the columns the table has today. Numbers and dates travel as
    // strings and MySQL converts them on insert; booleans become 1/0 for tinyint(1).
    private static Dictionary<string, object?> ToValues(Dictionary<string, JsonElement> row, HashSet<string> liveColumns)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, element) in row)
        {
            if (!liveColumns.Contains(column)) continue;
            values[column] = element.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.True => 1L,
                JsonValueKind.False => 0L,
                JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetRawText(),
                _ => element.GetString()
            };
        }
        return values;
    }

    // One row. A row the database refuses (a parent that no longer exists, a unique value already
    // taken) is skipped rather than failing the whole restore: MySQL leaves the transaction usable
    // after a statement error, and a client with one missing e-card recipient beats no client.
    private static async Task<long?> InsertAsync(IPRODbContext db, string table, Dictionary<string, object?> values, bool readNewId)
    {
        if (values.Count == 0) return null;
        var columns = values.Keys.ToList();
        try
        {
            await using (var insert = NewCommand(db))
            {
                insert.CommandText = $"INSERT INTO `{table}` ({string.Join(",", columns.Select(c => $"`{c}`"))}) VALUES ({string.Join(",", columns.Select((_, i) => $"@v{i}"))})";
                for (var i = 0; i < columns.Count; i++) AddParameter(insert, $"@v{i}", values[columns[i]]);
                await insert.ExecuteNonQueryAsync();
            }
            if (!readNewId) return 0;
            await using var last = NewCommand(db);
            last.CommandText = "SELECT LAST_INSERT_ID()";
            return Convert.ToInt64(await last.ExecuteScalarAsync());
        }
        catch (DbException)
        {
            return null;
        }
    }

    private static async Task<HashSet<string>> ColumnsAsync(IPRODbContext db, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = NewCommand(db);
        command.CommandText = "SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @p0";
        AddParameter(command, "@p0", table);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        return columns;
    }

    private static async Task<bool> TableExistsAsync(IPRODbContext db, string table) =>
        (await ColumnsAsync(db, table)).Count > 0;

    // ---- plumbing -------------------------------------------------------------------------------

    private static DbCommand NewCommand(IPRODbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) connection.Open();
        var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
