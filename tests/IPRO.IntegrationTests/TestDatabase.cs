using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using IPRO.DataAccess;
using Microsoft.EntityFrameworkCore;

namespace IPRO.IntegrationTests;

// A throwaway MySQL database with the REAL schema: created fresh, taken through the same
// MigrateAsync both apps run at startup, dropped on dispose. Tests run against the actual
// constraints the production database has -- the whole point of this suite is that the 2026-08-14
// invoice loss lived in the gap between what the code assumed and what the schema enforced, a gap
// no mocked or in-memory database can reproduce.
//
// Requires local MySQL (ops\Start-LocalEnv.ps1 brings it up). Override the connection with the
// IPRO_TEST_DB_TEMPLATE environment variable ({0} is replaced with the database name).
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly DbContextOptions<IPRODbContext> _options;

    private TestDatabase(DbContextOptions<IPRODbContext> options) => _options = options;

    // 619: dropping a test's database takes seconds on this PC and half a minute under a part's parallel load,
    // and other tests' saves wait behind it. At MySqlConnector's default of 30 seconds a test failed with no
    // assertion involved, and a drop that timed out left its database behind.
    private const int CommandTimeoutSeconds = 300;

    private static string ConnectionString(string name)
    {
        var template = Environment.GetEnvironmentVariable("IPRO_TEST_DB_TEMPLATE");
        return string.IsNullOrEmpty(template)
            ? $"server=127.0.0.1;port=3306;database={name};user=root;SslMode=None;AllowPublicKeyRetrieval=True;Default Command Timeout={CommandTimeoutSeconds}"
            : string.Format(template, name);
    }

    // 619: a run that is stopped leaves its databases behind (94 of them by 2026-10-29). The first test of each
    // run drops the ones more than a day old, by this class's exact name shape, so another run's are safe.
    private static readonly Lazy<Task> Sweep = new(SweepLeftoversAsync);

    private static async Task SweepLeftoversAsync()
    {
        try
        {
            await using var connection = new MySqlConnector.MySqlConnection(ConnectionString("mysql"));
            await connection.OpenAsync();
            var names = new List<string>();
            await using (var list = connection.CreateCommand())
            {
                list.CommandText = "SELECT table_schema FROM information_schema.tables WHERE table_schema REGEXP '^ipro_test_[0-9a-f]{20}$' " +
                                   "GROUP BY table_schema HAVING MAX(create_time) < NOW() - INTERVAL 1 DAY";
                await using var reader = await list.ExecuteReaderAsync();
                while (await reader.ReadAsync()) names.Add(reader.GetString(0));
            }
            foreach (var name in names)
            {
                await using var drop = connection.CreateCommand();
                drop.CommandText = $"DROP DATABASE `{name}`";
                await drop.ExecuteNonQueryAsync();
            }
        }
        catch (MySqlConnector.MySqlException)
        {
            // Housekeeping never fails a test: the next run tries again.
        }
    }

    public static async Task<TestDatabase> CreateAsync(bool applyLedgerGuard)
    {
        await Sweep.Value;

        // Unique name per test: tests are isolated from each other and safe to run in parallel.
        var name = $"ipro_test_{Guid.NewGuid():N}"[..30];
        var connectionString = ConnectionString(name);

        // Pinned version rather than ServerVersion.AutoDetect: AutoDetect opens a connection to a
        // database that does not exist yet.
        var options = new DbContextOptionsBuilder<IPRODbContext>()
            .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 36)))
            .Options;

        var db = new TestDatabase(options);
        // Flake hardening (2026-08-29): under full-suite parallelism dozens of contexts create
        // databases and open connections against one local MySQL at once, and a transient
        // failure (connection slot exhaustion, a creation timeout) fails whichever test is
        // mid-create -- a different victim each run. Two full-suite runs each lost exactly one
        // self-contained test that passed 3/3 solo; this retry absorbs INFRASTRUCTURE errors
        // only. It wraps schema creation alone -- an assertion failure inside a test can never
        // reach it, so nothing real is ever masked.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await CreateSchemaAsync(db, applyLedgerGuard);
                break;
            }
            catch (MySqlConnector.MySqlException) when (attempt < 3)
            {
                await Task.Delay(1500 * attempt);
            }
        }
        return db;
    }

    private static async Task CreateSchemaAsync(TestDatabase db, bool applyLedgerGuard)
    {
        await using (var context = db.CreateContext())
        {
            // EnsureCreated, not MigrateAsync: production's real schema is migrations PLUS the
            // startup schema repairs in both Program.cs files, and the repairs are not callable
            // from here. A migrate-only database is missing repair-added columns (found the hard
            // way: BillingRule.DefaultWebsiteTemplateId broke every EF insert). EnsureCreated
            // builds the schema straight from the entity model -- the schema the code actually
            // programs against, cascades included. When the repairs move into a shared class
            // (TODO item 419) this switches to MigrateAsync + that class, i.e. the true boot path.
            await context.Database.EnsureCreatedAsync();
            if (applyLedgerGuard) await FinancialLedgerSchemaGuard.EnsureAsync(context);
        }
    }

    public IPRODbContext CreateContext() => new(_options);

    public async ValueTask DisposeAsync()
    {
        await using var context = CreateContext();
        await context.Database.EnsureDeletedAsync();
    }
}
