using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 474 (2026-09-11), step 4 of the data-backup plan. Azure's own backups (35 days, rehearsed
// 09-10) live inside Azure; this is a file we own. There is no mysqldump on Linux App Service, so
// DatabaseDump writes the same thing in-process: every table's CREATE TABLE and its rows as INSERT
// statements, values escaped, a script MySQL replays into an empty database. DatabaseDumpJob gzips
// it into the private db-backups container every night and removes files older than 30 days; the
// SuperAdmin Backups page lists them and can run a dump on demand.
public class DatabaseDumpTests
{
    [Fact]
    public async Task The_dump_replays_into_an_empty_database_with_every_row_and_value_intact()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var tricky = "O'Neil \\ \"quoted\" line1\nline2 éè \U0001F600 100%";
        db.Clients.Add(new Client
        {
            AgentUserId = agentId, FirstName = "Tricky", LastName = tricky, Email = $"t-{Guid.NewGuid():N}@example.test",
            Notes = tricky, DateOfBirth = new DateTime(1980, 2, 29), IsNewsletterSubscribed = true, CreatedAt = new DateTime(2026, 9, 11, 12, 34, 56, 789, DateTimeKind.Utc)
        });
        db.Clients.Add(new Client { AgentUserId = agentId, FirstName = "Plain", LastName = "P", Email = $"p-{Guid.NewGuid():N}@example.test" });
        db.NumberSequences.Add(new NumberSequence { Key = "invoice:2026", LastValue = 12345678901L, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var script = new StringBuilder();
        await using (var writer = new StringWriter(script))
        {
            var summary = await DatabaseDump.WriteAsync(db, writer);
            Assert.True(summary.Tables > 20, $"only {summary.Tables} tables");
            Assert.True(summary.Rows >= 3, $"only {summary.Rows} rows");
        }
        var sql = script.ToString();
        Assert.Contains("CREATE TABLE `Clients`", sql, StringComparison.OrdinalIgnoreCase); // Windows MySQL lower-cases table names; Azure keeps them
        Assert.Contains("INSERT INTO `Clients`", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("`Hangfire_", sql, StringComparison.OrdinalIgnoreCase);

        // Replay into a fresh database on the same server, then compare.
        var scratch = $"ipro_dump_{Guid.NewGuid():N}"[..30];
        var builder = new MySqlConnectionStringBuilder(db.Database.GetConnectionString()!) { Database = "mysql", AllowUserVariables = true };
        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        try
        {
            await Exec(connection, $"CREATE DATABASE `{scratch}` CHARACTER SET utf8mb4");
            await Exec(connection, $"USE `{scratch}`");
            await Exec(connection, sql);

            Assert.Equal(2L, await Scalar<long>(connection, "SELECT COUNT(*) FROM `Clients`"));
            Assert.Equal(tricky, await Scalar<string>(connection, "SELECT `LastName` FROM `Clients` WHERE `FirstName` = 'Tricky'"));
            Assert.Equal(tricky, await Scalar<string>(connection, "SELECT `Notes` FROM `Clients` WHERE `FirstName` = 'Tricky'"));
            Assert.Equal(new DateTime(1980, 2, 29), await Scalar<DateTime>(connection, "SELECT `DateOfBirth` FROM `Clients` WHERE `FirstName` = 'Tricky'"));
            Assert.Equal(new DateTime(2026, 9, 11, 12, 34, 56, 789), await Scalar<DateTime>(connection, "SELECT `CreatedAt` FROM `Clients` WHERE `FirstName` = 'Tricky'"));
            Assert.True(await Scalar<bool>(connection, "SELECT `IsNewsletterSubscribed` FROM `Clients` WHERE `FirstName` = 'Tricky'"));
            Assert.True(await Scalar<bool>(connection, "SELECT `DateOfBirth` IS NULL FROM `Clients` WHERE `FirstName` = 'Plain'"));
            Assert.Equal(12345678901L, await Scalar<long>(connection, "SELECT `LastValue` FROM `NumberSequences` WHERE `Key` = 'invoice:2026'"));
            Assert.Equal(1L, await Scalar<long>(connection, "SELECT COUNT(*) FROM `AgentUsers`"));
        }
        finally
        {
            await Exec(connection, $"DROP DATABASE IF EXISTS `{scratch}`");
        }
    }

    [Fact]
    public async Task The_nightly_job_uploads_a_gzipped_dump_privately_and_removes_files_older_than_thirty_days()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedAgentAsync(db);

        var now = DateTime.UtcNow;
        var blobs = new RecordingBlobStore();
        blobs.Existing.Add($"https://blobs/{IPRO.Scheduler.DatabaseDumpJob.ContainerName}/aaaa_ipro_crm-{now.AddDays(-31):yyyyMMdd-HHmmss}.sql.gz");
        blobs.Existing.Add($"https://blobs/{IPRO.Scheduler.DatabaseDumpJob.ContainerName}/bbbb_ipro_crm-{now.AddDays(-2):yyyyMMdd-HHmmss}.sql.gz");
        blobs.Existing.Add($"https://blobs/{IPRO.Scheduler.DatabaseDumpJob.ContainerName}/cccc_not-a-dump.txt");

        var expired = blobs.Existing[0]; // captured before the run: the fake drops it from Existing when it is deleted
        await new IPRO.Scheduler.DatabaseDumpJob(db, blobs, NullLogger<IPRO.Scheduler.DatabaseDumpJob>.Instance).RunAsync();

        var upload = Assert.Single(blobs.Uploads);
        Assert.Equal(IPRO.Scheduler.DatabaseDumpJob.ContainerName, upload.Container);
        Assert.True(upload.IsPrivate);
        Assert.Equal("application/gzip", upload.ContentType);
        Assert.Matches(@"^ipro_test_[0-9a-f]+-\d{8}-\d{6}\.sql\.gz$", upload.FileName);

        await using var unzipped = new GZipStream(new MemoryStream(upload.Bytes), CompressionMode.Decompress);
        using var reader = new StreamReader(unzipped, Encoding.UTF8);
        var sql = await reader.ReadToEndAsync();
        Assert.Contains("CREATE TABLE `AgentUsers`", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INSERT INTO `AgentUsers`", sql, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(new[] { expired }, blobs.Deleted); // the 31-day-old dump, nothing else
    }

    [Fact]
    public void The_job_is_scheduled_the_admin_page_exists_and_the_checklist_knows()
    {
        var program = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Program.cs"));
        Assert.Contains("RecurringJob.AddOrUpdate<DatabaseDumpJob>(\"database-dump\"", program);

        Assert.True(File.Exists(FindRepoFile(@"src\IPRO.Admin\Controllers\BackupsController.cs")));
        Assert.True(File.Exists(FindRepoFile(@"src\IPRO.Admin\Views\Backups\Index.cshtml")));
        Assert.Contains("href=\"/Backups\"", File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\Shared\_Layout.cshtml")));

        Assert.Contains("db-backups", File.ReadAllText(FindRepoFile(@"DOCS\14_BACKUP_AND_RELEASE_CHECKLIST.md")));
    }

    // ---- harness ------------------------------------------------------------------------------

    private static async Task<int> SeedAgentAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T474-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t474-{Guid.NewGuid():N}")[..20], Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Dump", LastName = "Agent", CompanyName = "Dump Co",
            DomainName = ($"t474-{Guid.NewGuid():N}")[..24], PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task Exec(MySqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(MySqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value, typeof(T))!;
    }

    private sealed class RecordingBlobStore : IBlobStorageService
    {
        public sealed record Upload(string FileName, string Container, string ContentType, bool IsPrivate, byte[] Bytes);
        public List<Upload> Uploads { get; } = new();
        public List<string> Existing { get; } = new();
        public List<string> Deleted { get; } = new();

        public async Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate)
        {
            using var buffer = new MemoryStream();
            await fileStream.CopyToAsync(buffer);
            Uploads.Add(new Upload(fileName, containerName, contentType, isPrivate, buffer.ToArray()));
            var url = $"https://blobs/{containerName}/{Guid.NewGuid():N}_{fileName}";
            Existing.Add(url);
            return url;
        }
        public Task<bool> DeleteAsync(string blobUrl) { Deleted.Add(blobUrl); Existing.Remove(blobUrl); return Task.FromResult(true); }
        public Task<Stream?> DownloadAsync(string blobUrl) => Task.FromResult<Stream?>(null);
        public Task<List<string>> ListAsync(string containerName) => Task.FromResult(Existing.Where(u => u.Contains($"/{containerName}/")).ToList());
        public string GetPublicUrl(string containerName, string fileName) => $"https://blobs/{containerName}/{fileName}";
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => Task.CompletedTask;
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
