using System.Text.Json;
using IPRO.DataAccess;
using IPRO.Utility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

// 472 (2026-09-10): removes recycle-bin snapshots past their 30 days, then their files. The order is
// deliberate: a failed file delete leaves an orphaned blob (the recoverable outcome); the reverse
// would leave a snapshot whose documents are already gone.
public class ClientRecycleBinPurgeJob
{
    private readonly IPRODbContext _db;
    private readonly IBlobStorageService _blob;
    private readonly ILogger<ClientRecycleBinPurgeJob> _logger;

    public ClientRecycleBinPurgeJob(IPRODbContext db, IBlobStorageService blob, ILogger<ClientRecycleBinPurgeJob> logger)
    {
        _db = db; _blob = blob; _logger = logger;
    }

    public async Task RunAsync()
    {
        var now = DateTime.UtcNow;
        var expired = await _db.ClientRecycleBinItems.Where(i => i.PurgeAfter <= now).ToListAsync();
        foreach (var item in expired)
        {
            try
            {
                List<string> urls;
                try { urls = JsonSerializer.Deserialize<List<string>>(item.BlobUrlsJson) ?? new List<string>(); }
                catch (JsonException) { urls = new List<string>(); }

                _db.ClientRecycleBinItems.Remove(item);
                await _db.SaveChangesAsync();

                var removed = 0;
                foreach (var url in urls)
                {
                    try { if (await _blob.DeleteAsync(url)) removed++; }
                    catch (Exception ex) { _logger.LogWarning(ex, "Recycle bin purge: could not delete file {Url} for item {Id}", url, item.Id); }
                }
                _logger.LogInformation("Recycle bin purge: item {Id} ({Name}, agent {AgentId}) removed after {Days} days, {Files}/{Total} file(s) deleted",
                    item.Id, item.DisplayName, item.AgentUserId, ClientRecycleBin.RetentionDays, removed, urls.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Recycle bin purge failed for item {Id}", item.Id);
            }
        }
    }
}
