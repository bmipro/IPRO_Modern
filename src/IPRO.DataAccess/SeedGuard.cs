using System;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.DataAccess;

// Every "check then act" seeder in this codebase (AnyAsync() -> AddRange() -> SaveChangesAsync())
// runs on BOTH ipro-prod-web and ipro-prod-admin at startup, against the SAME shared MySQL
// database, triggered by the SAME git push with no coordination between the two deploy
// pipelines. That is a real, provable TOCTOU race: both processes can see an empty table and
// both insert. Where the table has no unique constraint, the race silently duplicates every
// row. Where it does (ECardDesigns/ELetterTemplates -- unique on Key, added 2026-07-29), the
// process that commits second gets a duplicate-key exception instead.
//
// That exact exception, unhandled, is what took ipro-prod-web and ipro-prod-admin down together
// on 2026-07-29: an unhandled .NET exception escaping Main on Linux exits via SIGABRT (128+6=134)
// by design of the CoreCLR PAL -- that is the ordinary signature for "something threw before
// app.Run()", not evidence of a native crash or memory corruption. See DOCS/09_TROUBLESHOOTING.md.
//
// This wraps a seeder's check-and-insert in a MySQL advisory lock (GET_LOCK/RELEASE_LOCK), so
// only one process performs it at a time -- the same primitive Hangfire.Storage.MySql's own
// Upgrade() path already uses correctly elsewhere in this dependency tree (its sibling Install()
// path does not, which is a latent risk in that package, not this one, and is out of scope here).
public static class SeedGuard
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    public static async Task RunAsync(IPRODbContext db, string lockName, ILogger? logger, Func<Task> seed)
    {
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed) await connection.OpenAsync();

        try
        {
            if (!await TryAcquireAsync(connection, lockName))
            {
                // Another process holds the lock for this seeder right now, or held it recently
                // and we timed out waiting. Either way it has already seeded (or is about to),
                // so there is nothing safe for us to do here except leave it alone.
                logger?.LogInformation(
                    "Seed lock '{Lock}' held by another process; skipping this run.", lockName);
                return;
            }

            try
            {
                await seed();
            }
            finally
            {
                await ReleaseAsync(connection, lockName);
            }
        }
        finally
        {
            if (wasClosed) await connection.CloseAsync();
        }
    }

    private static async Task<bool> TryAcquireAsync(DbConnection connection, string lockName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT GET_LOCK(@name, @timeoutSeconds)";
        AddParameter(command, "@name", $"ipro_seed:{lockName}");
        AddParameter(command, "@timeoutSeconds", (int)LockTimeout.TotalSeconds);
        var result = await command.ExecuteScalarAsync();
        // MySQL's GET_LOCK returns 1 (acquired), 0 (timed out), or NULL (error). Converting rather
        // than pattern-matching a specific CLR type: whether the driver boxes this as int or long
        // is a driver-version detail, not something to hardcode a guess about -- a wrong guess
        // here would make the lock silently never acquire, which is worse than not having a lock
        // at all, since seeding would then just quietly stop happening on every future deploy.
        return result is not null and not DBNull && Convert.ToInt64(result) == 1L;
    }

    private static async Task ReleaseAsync(DbConnection connection, string lockName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RELEASE_LOCK(@name)";
        AddParameter(command, "@name", $"ipro_seed:{lockName}");
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
