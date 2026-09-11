using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace IPRO.DataAccess;

public sealed record DatabaseDumpSummary(string DatabaseName, int Tables, long Rows);

// 474 (2026-09-11): a logical dump of the whole database, written in-process because Linux App
// Service has no mysqldump. For every base table (Hangfire's own tables excluded): DROP, the
// server's CREATE TABLE, then the rows as INSERT statements in batches, every value escaped the way
// the MySQL client would. The result replays into an EMPTY database with plain `mysql < file`.
public static class DatabaseDump
{
    private const int BatchRows = 200;

    public static async Task<DatabaseDumpSummary> WriteAsync(IPRODbContext db, TextWriter writer)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();

        var databaseName = Convert.ToString(await ScalarAsync(connection, "SELECT DATABASE()")) ?? string.Empty;
        var tables = (await StringsAsync(connection,
                "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME"))
            .Where(t => !t.StartsWith("Hangfire_", StringComparison.OrdinalIgnoreCase))
            .ToList();

        await writer.WriteLineAsync($"-- IPRO database dump of `{databaseName}`, taken {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC, {tables.Count} tables");
        await writer.WriteLineAsync("-- Replay into an EMPTY database:  mysql -h <host> -u <user> -p --ssl-mode=REQUIRED <database> < this-file.sql");
        await writer.WriteLineAsync("SET NAMES utf8mb4;");
        await writer.WriteLineAsync("SET FOREIGN_KEY_CHECKS=0;");
        await writer.WriteLineAsync("SET UNIQUE_CHECKS=0;");
        await writer.WriteLineAsync("SET SQL_MODE='NO_AUTO_VALUE_ON_ZERO';");

        long rows = 0;
        foreach (var table in tables)
        {
            await writer.WriteLineAsync();
            await writer.WriteLineAsync($"-- {table}");
            await writer.WriteLineAsync($"DROP TABLE IF EXISTS `{table}`;");
            await writer.WriteLineAsync(await CreateTableAsync(connection, table) + ";");
            rows += await WriteRowsAsync(connection, table, writer);
        }

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("SET FOREIGN_KEY_CHECKS=1;");
        await writer.WriteLineAsync("SET UNIQUE_CHECKS=1;");
        await writer.WriteLineAsync($"-- end of dump: {rows} rows");
        await writer.FlushAsync();
        return new DatabaseDumpSummary(databaseName, tables.Count, rows);
    }

    private static async Task<string> CreateTableAsync(DbConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SHOW CREATE TABLE `{table}`";
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException($"SHOW CREATE TABLE returned nothing for {table}");
        return reader.GetString(1);
    }

    private static async Task<long> WriteRowsAsync(DbConnection connection, string table, TextWriter writer)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM `{table}`";
        command.CommandTimeout = 600;
        await using var reader = await command.ExecuteReaderAsync();

        var columns = new List<string>();
        for (var i = 0; i < reader.FieldCount; i++) columns.Add(reader.GetName(i));
        var header = $"INSERT INTO `{table}` ({string.Join(", ", columns.Select(c => $"`{c}`"))}) VALUES";

        long rows = 0;
        var inBatch = 0;
        var line = new StringBuilder();
        while (await reader.ReadAsync())
        {
            line.Clear();
            line.Append(inBatch == 0 ? header + "\n(" : ",\n(");
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (i > 0) line.Append(", ");
                line.Append(reader.IsDBNull(i) ? "NULL" : Literal(reader.GetValue(i)));
            }
            line.Append(')');
            await writer.WriteAsync(line.ToString());
            rows++;
            if (++inBatch == BatchRows)
            {
                await writer.WriteLineAsync(";");
                inBatch = 0;
            }
        }
        if (inBatch > 0) await writer.WriteLineAsync(";");
        return rows;
    }

    // A SQL literal for one value, as the mysql client would write it.
    private static string Literal(object value) => value switch
    {
        bool b => b ? "1" : "0",
        byte[] bytes => bytes.Length == 0 ? "''" : "X'" + Convert.ToHexString(bytes) + "'",
        DateTime d => "'" + d.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "'",
        DateTimeOffset o => "'" + o.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "'",
        TimeSpan t => "'" + t.ToString("c", CultureInfo.InvariantCulture) + "'",
        DateOnly dateOnly => "'" + dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'",
        TimeOnly timeOnly => "'" + timeOnly.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "'",
        Guid g => "'" + g.ToString("D") + "'",
        string s => "'" + MySqlHelper.EscapeString(s) + "'",
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        double dbl => dbl.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        sbyte or byte or short or ushort or int or uint or long or ulong => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        _ => "'" + MySqlHelper.EscapeString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty) + "'"
    };

    private static async Task<object?> ScalarAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task<List<string>> StringsAsync(DbConnection connection, string sql)
    {
        var values = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values;
    }
}
