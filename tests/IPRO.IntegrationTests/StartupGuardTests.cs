using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 445 (2026-09-01): startup must not be able to take the site down. See StartupGuard for the
// incident. These tests pin the three rules and the two Program.cs files that apply them.
public class StartupGuardTests
{
    // ---- rule 1: a failing step is logged and skipped, never fatal --------------------------

    [Fact]
    public async Task A_throwing_step_is_swallowed_logged_and_leaves_the_tracker_clean()
    {
        await using var db = NeverConnectedContext();
        db.Add(new BillingRule { PackageName = "would-be-retried", MonthlyPrice = 1m });
        Assert.NotEmpty(db.ChangeTracker.Entries());

        var ok = await StartupGuard.RunStepAsync("Boom",
            () => throw new InvalidOperationException("simulated blocked DDL"),
            db, NullLogger.Instance);

        Assert.False(ok);
        // The failed entity must not linger to be re-attempted by the next SaveChanges.
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task A_succeeding_step_runs_and_reports_true()
    {
        await using var db = NeverConnectedContext();
        var ran = false;
        var ok = await StartupGuard.RunStepAsync("Fine", () => { ran = true; return Task.CompletedTask; }, db, NullLogger.Instance);
        Assert.True(ok);
        Assert.True(ran);
    }

    // ---- rule 2: the advisory lock is held for the block and released with it -------------

    [Fact]
    public async Task Entering_holds_the_advisory_lock_and_disposing_releases_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var holder = testDb.CreateContext();
        await using var observer = testDb.CreateContext();

        var lease = await StartupGuard.EnterAsync(holder, NullLogger.Instance);
        Assert.True(lease.LockHeld);
        // IS_USED_LOCK returns the holding connection id, or NULL when nobody holds it.
        Assert.NotNull(await IsUsedLockAsync(observer));

        await lease.DisposeAsync();
        Assert.Null(await IsUsedLockAsync(observer));
    }

    [Fact]
    public void The_lock_waits_longer_than_a_healthy_startup_and_the_repair_timeout_is_short()
    {
        // 34-54s healthy startups were observed; the wait must outlast one so web and admin
        // serialise rather than both giving up. The repair timeout is what turns a stuck lock
        // into a log line instead of a dead process.
        Assert.True(StartupGuard.LockWaitSeconds >= 60);
        Assert.True(StartupGuard.RepairTimeoutSeconds <= 20);
    }

    // ---- rule 3 + application: both Program.cs files ----------------------------------------

    public static readonly string[] Programs =
    {
        @"src\IPRO.Web\Program.cs",
        @"src\IPRO.Admin\Program.cs",
    };

    public static TheoryData<string> ProgramCases()
    {
        var d = new TheoryData<string>();
        foreach (var p in Programs) d.Add(p);
        return d;
    }

    [Theory]
    [MemberData(nameof(ProgramCases))]
    public void Startup_takes_the_lock_arms_the_timeouts_after_migrations_and_wraps_every_repair(string file)
    {
        var src = File.ReadAllText(FindRepoFile(file));
        var block = StartupBlock(src);

        // The lease is taken right after the context is resolved and lives for the whole block.
        Assert.Contains("await using var startupLease = await StartupGuard.EnterAsync(db, app.Logger);", block);

        // Timeouts are armed AFTER MigrateAsync -- a real migration must never be cut off at 15s.
        var migrate = block.IndexOf("await db.Database.MigrateAsync();", StringComparison.Ordinal);
        var arm = block.IndexOf("await StartupGuard.ArmRepairTimeoutsAsync(db, app.Logger);", StringComparison.Ordinal);
        Assert.True(migrate >= 0, "MigrateAsync must still run, unwrapped, first");
        Assert.True(arm > migrate, "ArmRepairTimeoutsAsync must come after MigrateAsync");

        // MigrateAsync is deliberately the one unwrapped step.
        Assert.DoesNotMatch(new Regex(@"RunStepAsync\([^;]*MigrateAsync"), block);

        // No repair, guard, seeder or blob call is awaited bare at the block's top level.
        var bare = Regex.Matches(block,
            @"^    await (StartupSchemaRepair|WebsiteContentSchema|EmailDeliverySchema|IPRO\.DataAccess\.FinancialLedgerSchemaGuard|\w+Seeder|blob|EnsureAdminUserSchemaAsync)\b",
            RegexOptions.Multiline);
        Assert.True(bare.Count == 0,
            $"{file}: {bare.Count} un-guarded startup step(s) remain: " +
            string.Join(" | ", bare.Cast<Match>().Select(m => m.Value.Trim())));

        // And the wrapping is real, not a rename: dozens of steps go through the guard.
        Assert.True(Regex.Matches(block, @"await StartupGuard\.RunStepAsync\(").Count >= 30,
            $"{file}: expected the repair block to be wrapped step by step");
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static string StartupBlock(string src)
    {
        var start = src.IndexOf("using (var scope = app.Services.CreateScope())", StringComparison.Ordinal);
        Assert.True(start >= 0, "the startup scope block moved; this pin needs updating");
        var end = src.IndexOf("\napp.Run();", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return src[start..end];
    }

    private static async Task<long?> IsUsedLockAsync(IPRODbContext db)
    {
        var rows = await db.Database
            .SqlQueryRaw<long?>($"SELECT IS_USED_LOCK('{StartupGuard.LockName}') AS Value")
            .ToListAsync();
        return rows.Single();
    }

    private static IPRODbContext NeverConnectedContext()
    {
        // Never queried: RunStepAsync only touches the change tracker.
        var options = new DbContextOptionsBuilder<IPRODbContext>()
            .UseMySql("Server=localhost;Database=never_used;User=none;Password=none",
                new MySqlServerVersion(new Version(8, 0, 36)))
            .Options;
        return new IPRODbContext(options);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
