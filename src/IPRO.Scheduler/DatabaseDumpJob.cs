using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using IPRO.DataAccess;
using IPRO.Utility;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

// 474 (2026-09-11): a gzipped SQL dump of the whole database into the private db-backups container
// every night, and the removal of dumps older than 30 days. A file we own, restorable table by
// table, independent of Azure's own backups (35 days, rehearsed 09-10). The SuperAdmin Backups page
// lists the files and can run this on demand.
public class DatabaseDumpJob
{
    public const string ContainerName = "db-backups";
    public const int RetentionDays = 30;

    private readonly IPRODbContext _db;
    private readonly IBlobStorageService _blob;
    private readonly ILogger<DatabaseDumpJob> _logger;

    public DatabaseDumpJob(IPRODbContext db, IBlobStorageService blob, ILogger<DatabaseDumpJob> logger)
    {
        _db = db; _blob = blob; _logger = logger;
    }

    public static string FileName(string databaseName, DateTime utc) => $"{databaseName}-{utc:yyyyMMdd-HHmmss}.sql.gz";

    public static bool TryParseTimestamp(string blobUrlOrName, out DateTime utc)
    {
        var match = Regex.Match(blobUrlOrName, @"-(\d{8}-\d{6})\.sql\.gz$");
        if (match.Success && DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc))
            return true;
        utc = default;
        return false;
    }

    public Task RunAsync() => RunAndReportAsync();

    // The uploaded file's URL (private; download through the Azure portal or the CLI).
    public async Task<string> RunAndReportAsync()
    {
        var now = DateTime.UtcNow;
        var temp = Path.Combine(Path.GetTempPath(), $"ipro-dump-{Guid.NewGuid():N}.sql.gz");
        try
        {
            DatabaseDumpSummary summary;
            await using (var file = File.Create(temp))
            await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
            await using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
            {
                summary = await DatabaseDump.WriteAsync(_db, writer);
            }

            var bytes = new FileInfo(temp).Length;
            string url;
            await using (var read = File.OpenRead(temp))
            {
                url = await _blob.UploadAsync(read, FileName(summary.DatabaseName, now), ContainerName, "application/gzip", isPrivate: true);
            }
            _logger.LogInformation("Database dump: {Tables} tables, {Rows} rows, {Bytes} bytes gzipped, uploaded to {Url}",
                summary.Tables, summary.Rows, bytes, url);

            // Retention: only files that carry a dump timestamp, only past the window. Anything
            // else in the container is left alone.
            var removed = 0;
            var cutoff = now.AddDays(-RetentionDays);
            foreach (var existing in await _blob.ListAsync(ContainerName))
            {
                if (!TryParseTimestamp(existing, out var stamp) || stamp > cutoff) continue;
                try { if (await _blob.DeleteAsync(existing)) removed++; }
                catch (Exception ex) { _logger.LogWarning(ex, "Database dump: could not remove {Url}", existing); }
            }
            if (removed > 0) _logger.LogInformation("Database dump: {Removed} file(s) older than {Days} days removed", removed, RetentionDays);
            return url;
        }
        finally
        {
            try { File.Delete(temp); } catch { /* a leftover temp file is harmless */ }
        }
    }
}
