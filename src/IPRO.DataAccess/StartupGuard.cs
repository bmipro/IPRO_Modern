using System;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.DataAccess;

// Startup must not be able to take the site down. (TODO 445, 2026-09-01.)
//
// Both apps run ~30 schema-repair calls (live ALTER TABLE / CREATE TABLE IF NOT EXISTS), a schema
// guard, and a dozen seeders on the main thread before the app listens. On 2026-09-01 a deploy's
// second restart killed the web app mid-DDL; the dead session held a MySQL metadata lock; every
// later startup blocked on it until MySqlConnector's 30s command timeout threw, unhandled -- exit
// 134, five times in a row, production 503 on every path for 24 minutes. The database was healthy
// throughout. Nothing in that sequence needed to stop the site: on an established database every
// repair is a no-op check, and a repair that genuinely cannot run costs one feature a 500 with a
// named reason, not the whole site a 503 with none.
//
// Three rules, applied in Program.cs of BOTH apps:
//
//   1. RunStepAsync wraps every repair and seeder: log it (logger AND stderr, because the container
//      log is readable even when telemetry never flushes), clear the tracker so a failed entity is
//      not re-attempted by the next SaveChanges, continue. Mirrors what the seeders already did.
//   2. EnterAsync holds a MySQL advisory lock for the whole block, so web and admin -- which deploy
//      minutes apart and run the SAME block against the SAME database -- never run repairs
//      concurrently. Unlike SeedGuard, a timeout does NOT skip the work: startup runs regardless,
//      unlocked, with a warning. The connection is held open for the scope so the lock (which is
//      per-connection) and the session variable below are reliable.
//   3. ArmRepairTimeoutsAsync, called AFTER MigrateAsync, makes a blocked DDL fail fast: 15s command
//      timeout and 15s lock_wait_timeout, so a stuck lock costs 15 seconds and a log line, not 30
//      seconds and the process. Deliberately after migrations, so a real future migration is never
//      cut off at 15 seconds.
//
// MigrateAsync itself is deliberately NOT wrapped: refusing to start on a failed migration is the
// one place where "down" beats "up on the wrong schema", and on the incident path (a held lock)
// MigrateAsync is a no-op read that succeeds anyway.
public static class StartupGuard
{
    public const string LockName = "ipro_startup";
    public const int LockWaitSeconds = 90;       // longer than a healthy startup (34-54s observed)
    public const int RepairTimeoutSeconds = 15;

    public static async Task<bool> RunStepAsync(string name, Func<Task> step, IPRODbContext db, ILogger logger)
    {
        try
        {
            await step();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Startup] {Step} failed; the app is starting anyway.", name);
            Console.Error.WriteLine($"[Startup:{name}] FAILED: {ex}");
            // A swallowed failure leaves the failed entities TRACKED on this shared DbContext; the
            // next SaveChangesAsync anywhere in the startup scope would re-attempt them and die.
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public static async Task<StartupLease> EnterAsync(IPRODbContext db, ILogger logger)
    {
        var connection = db.Database.GetDbConnection();
        var opened = false;
        try
        {
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync();
                opened = true;
            }
        }
        catch (Exception ex)
        {
            // No connection means no lock and no session variable; the steps will each fail and be
            // logged individually. Starting anyway is still the right call.
            logger.LogWarning(ex, "[Startup] could not open the startup connection; continuing without the advisory lock.");
            return new StartupLease(connection, held: false, opened: false, logger);
        }

        var held = false;
        try
        {
            held = await AcquireAsync(connection, LockName, LockWaitSeconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Startup] advisory lock attempt failed; continuing without it.");
        }

        if (held)
            logger.LogInformation("[Startup] holding advisory lock '{Lock}' for the repair block.", LockName);
        else
            logger.LogWarning("[Startup] another instance held '{Lock}' for {Seconds}s; continuing WITHOUT the lock.", LockName, LockWaitSeconds);

        return new StartupLease(connection, held, opened, logger);
    }

    // After MigrateAsync, never before.
    public static async Task ArmRepairTimeoutsAsync(IPRODbContext db, ILogger logger)
    {
        db.Database.SetCommandTimeout(RepairTimeoutSeconds);
        try
        {
            await db.Database.ExecuteSqlRawAsync($"SET SESSION lock_wait_timeout = {RepairTimeoutSeconds}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Startup] could not set lock_wait_timeout; the command timeout still bounds a blocked repair.");
        }
    }

    public sealed class StartupLease : IAsyncDisposable
    {
        private readonly DbConnection _connection;
        private readonly bool _opened;
        private readonly ILogger _logger;

        internal StartupLease(DbConnection connection, bool held, bool opened, ILogger logger)
        {
            _connection = connection;
            LockHeld = held;
            _opened = opened;
            _logger = logger;
        }

        public bool LockHeld { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (LockHeld) await ReleaseAsync(_connection, LockName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Startup] could not release '{Lock}'; it dies with the connection.", LockName);
            }
            finally
            {
                if (_opened)
                {
                    try { await _connection.CloseAsync(); } catch { /* closing is best effort */ }
                }
            }
        }
    }

    // Same two statements SeedGuard uses; kept local because SeedGuard's semantics (skip on
    // timeout, 30s, "ipro_seed:" prefix) are right for seeders and wrong here.
    private static async Task<bool> AcquireAsync(DbConnection connection, string name, int timeoutSeconds)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT GET_LOCK(@name, @timeoutSeconds)";
        AddParameter(command, "@name", name);
        AddParameter(command, "@timeoutSeconds", timeoutSeconds);
        var result = await command.ExecuteScalarAsync();
        // 1 = acquired, 0 = timed out, NULL = error.
        return result is not null and not DBNull && Convert.ToInt64(result) == 1L;
    }

    private static async Task ReleaseAsync(DbConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RELEASE_LOCK(@name)";
        AddParameter(command, "@name", name);
        await command.ExecuteScalarAsync();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
