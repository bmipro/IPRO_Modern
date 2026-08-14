using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// The test AgentDataEraser's header has pointed at since the eraser was written, and which did not
// exist until 2026-08-15. Its absence is why two agent-owned tables (WebsiteSpamAttempts and the
// ClientClientCategory join) were missing from the map, and why a BannerSlides entry for a table
// that has never existed sat there looking like coverage.
//
// The failure this guards against is quiet. TableExistsAsync skips unknown tables and the erasure
// reports only what it deleted, so a table missing from the map produces no error anywhere -- just
// an undercount in the SuperAdmin preview, and real orphaned rows the moment the cascade that has
// been sweeping them up is removed. That is not hypothetical: FinancialLedgerSchemaGuard now drops
// cascades deliberately.
public class AgentDataEraserCoverageTests
{
    // Tables with an AgentUserId column that the eraser must NOT delete, each for a stated reason.
    // Anything not listed here and not in the map fails the test.
    private static readonly Dictionary<string, string> IntentionallyNotErased = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AgentUsers"] = "The agent row itself. Deleted by the caller after the eraser runs, not by the map.",
        ["AdminAuditLogs"] = "SuperAdmin's own audit trail. Records WHO deleted the agent; erasing it would erase the evidence of the erasure.",
    };

    [Fact]
    public async Task Every_table_with_an_AgentUserId_column_is_either_erased_or_explicitly_exempt()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var agentOwned = await TablesWithColumnAsync(db, "AgentUserId");
        Assert.NotEmpty(agentOwned); // guards against the query silently matching nothing

        var covered = new HashSet<string>(AgentDataEraser.CoveredTables, StringComparer.OrdinalIgnoreCase);

        var uncovered = agentOwned
            .Where(t => !covered.Contains(t) && !IntentionallyNotErased.ContainsKey(t))
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(uncovered.Count == 0,
            "These tables have an AgentUserId column but no entry in AgentDataEraser's Map or FinancialMap:\n  " +
            string.Join("\n  ", uncovered) +
            "\n\nAdd each to the map, or to IntentionallyNotErased in this test with the reason why it must survive " +
            "an agent's deletion. Leaving it out means the SuperAdmin erasure preview undercounts, and the rows " +
            "orphan outright if the cascade currently sweeping them is ever dropped.");
    }

    [Fact]
    public async Task Every_table_named_in_the_map_actually_exists()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var real = await AllTablesAsync(db);
        var phantom = AgentDataEraser.CoveredTables
            .Where(t => !real.Contains(t))
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(phantom.Count == 0,
            "AgentDataEraser lists tables that do not exist in the schema:\n  " +
            string.Join("\n  ", phantom) +
            "\n\nTableExistsAsync skips them at run time, so they never fail loudly -- they just read as " +
            "coverage that isn't there. Remove the entry, or correct the name if it was a typo.");
    }

    private static async Task<List<string>> TablesWithColumnAsync(IPRODbContext db, string column)
    {
        var rows = new List<string>();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT TABLE_NAME FROM information_schema.COLUMNS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND COLUMN_NAME = @column";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@column";
        parameter.Value = column;
        command.Parameters.Add(parameter);

        await db.Database.OpenConnectionAsync();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows;
    }

    private static async Task<HashSet<string>> AllTablesAsync(IPRODbContext db)
    {
        var rows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE()";

        await db.Database.OpenConnectionAsync();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows;
    }
}
