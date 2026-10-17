using System.Net.Http.Json;
using IPRO.Business.SiteImport;
using IPRO.DataAccess;
using IPRO.Utility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

// 585: the housekeeping of the new builder (Beta), every ten minutes.
//   1. Erasures: an adviser deleted from iPro may have a draft in the builder (AgentDataEraser queued
//      it). The builder is asked to erase it, with the key the two share, until it says it has.
//   2. Looks: a "See it on my site" whose hour is over is dropped, pictures and all.
//   3. Passes and sessions over for a day are deleted (only their hashes were ever kept).
public class BuilderBetaJob
{
    private readonly IPRODbContext _db;
    private readonly IBlobStorageService _blob;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BuilderBetaJob> _logger;

    public BuilderBetaJob(IPRODbContext db, IBlobStorageService blob, IHttpClientFactory http, IConfiguration configuration, ILogger<BuilderBetaJob> logger)
    {
        _db = db; _blob = blob; _http = http; _configuration = configuration; _logger = logger;
    }

    public async Task RunAsync()
    {
        var now = DateTime.UtcNow;
        await EraseAsync(now);

        var dropped = await BuilderImportService.DropStalePreviewsAsync(_db, _blob, now);
        if (dropped > 0) _logger.LogInformation("Dropped {Count} builder look(s) whose hour was over", dropped);

        await _db.WebsiteBuilderSessions.Where(s => s.ExpiresAt < now.AddDays(-1)).ExecuteDeleteAsync();
    }

    private async Task EraseAsync(DateTime now)
    {
        var owed = await _db.BuilderTenantErasures.OrderBy(e => e.Id).Take(50).ToListAsync();
        if (owed.Count == 0) return;
        var baseUrl = _configuration["Builder:BaseUrl"]?.Trim().TrimEnd('/');
        var key = _configuration["Builder:ServiceKey"] ?? string.Empty;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _) || key.Length < 32)
        {
            _logger.LogWarning("{Count} builder erasure(s) are waiting: Builder:BaseUrl or Builder:ServiceKey is not set", owed.Count);
            return;
        }

        using var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        foreach (var erasure in owed)
        {
            string? error = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/internal/erase") { Content = JsonContent.Create(new { tenant = erasure.Tenant }) };
                request.Headers.Add("X-Ipro-Service-Key", key);
                using var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode) error = $"The builder answered {(int)response.StatusCode}.";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                error = "The builder could not be reached: " + ex.Message;
            }

            if (error == null)
            {
                _db.BuilderTenantErasures.Remove(erasure);
                _logger.LogInformation("The builder erased a deleted adviser's draft");
            }
            else
            {
                erasure.Attempts++;
                erasure.LastAttemptAt = now;
                erasure.LastError = error.Length <= 300 ? error : error[..300];
                _logger.LogWarning("A builder erasure is still owed (attempt {Attempts}): {Error}", erasure.Attempts, erasure.LastError);
            }
            await _db.SaveChangesAsync();
        }
    }
}
