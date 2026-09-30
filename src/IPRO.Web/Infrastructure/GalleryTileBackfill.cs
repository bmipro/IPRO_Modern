using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// 535: photos uploaded before today are the phone originals, shown whole in every tile. This gives each
// of them the small copy the tiles now use and measures it, once. It deletes nothing and keeps each
// original as the photo's full view: an original can sit in a newsletter already delivered, which no
// database check can see (the rule in BlobReferences), so keeping it is the only safe choice. What it
// adds counts toward the adviser's storage like any tile.
//
// "Measured" is the marker: a photo with Width 0 has not been looked at; a photo the backfill could not
// read (a missing file, an unreadable one) gets -1 so it is not fetched again on every start.
public static class GalleryTileBackfill
{
    public sealed record Result(int Tiled, int Measured, int Unreadable);

    public static async Task<Result> RunAsync(IPRODbContext db, IBlobStorageService blob, ILogger logger, int maxPhotos, CancellationToken ct)
    {
        var blocks = await db.WebsiteContentBlocks.AsNoTracking()
            .Where(b => b.BlockType == WebsiteBlockTypes.Gallery && b.SettingsJson != null && b.SettingsJson.Contains("\"Url\""))
            .Select(b => new { b.Id, b.SettingsJson })
            .ToListAsync(ct);

        int tiled = 0, measured = 0, unreadable = 0, looked = 0;
        foreach (var block in blocks)
        {
            if (looked >= maxPhotos || ct.IsCancellationRequested) break;
            var pending = WebsiteGallerySettings.FromJson(block.SettingsJson).Images.Where(i => i.Width == 0 && !string.IsNullOrWhiteSpace(i.Url)).ToList();
            if (pending.Count == 0) continue;

            var updates = new Dictionary<string, (string ThumbUrl, int Width, int Height, long AddedBytes)>();
            foreach (var image in pending)
            {
                if (looked >= maxPhotos || ct.IsCancellationRequested) break;
                looked++;
                try
                {
                    byte[]? original = null;
                    await using (var stream = await blob.DownloadAsync(image.Url))
                    {
                        if (stream != null)
                        {
                            using var copy = new MemoryStream();
                            await stream.CopyToAsync(copy, ct);
                            original = copy.ToArray();
                        }
                    }
                    var photo = original == null ? null : GalleryImages.Process(original);
                    if (photo == null)
                    {
                        updates[image.Url] = (string.Empty, -1, -1, 0);
                        unreadable++;
                        continue;
                    }

                    var thumbUrl = string.Empty;
                    long added = 0;
                    if (photo.Tile != null)
                    {
                        using var tileStream = new MemoryStream(photo.Tile.Bytes);
                        var name = Path.GetFileNameWithoutExtension(image.Url.Split('?')[0]) + "-tile" + photo.Tile.Extension;
                        thumbUrl = await blob.UploadAsync(tileStream, name, "website-gallery", photo.Tile.ContentType, isPrivate: false);
                        added = photo.Tile.Bytes.LongLength;
                        tiled++;
                    }
                    updates[image.Url] = (thumbUrl, photo.SourceWidth, photo.SourceHeight, added);
                    measured++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Anything unexpected about one photo must not stop the rest; it is looked at again
                    // on the next start (Width stays 0), and the log says which.
                    logger.LogWarning(ex, "Gallery backfill: could not make a tile for {Url}.", image.Url);
                }
            }

            if (updates.Count == 0) continue;
            // Read the block again just before writing: an adviser may have added, removed or captioned
            // photos while the tiles were being made, and those edits must survive.
            var fresh = await db.WebsiteContentBlocks.FirstOrDefaultAsync(b => b.Id == block.Id, ct);
            if (fresh == null) continue;
            var settings = WebsiteGallerySettings.FromJson(fresh.SettingsJson);
            foreach (var image in settings.Images)
            {
                if (image.Width != 0 || !updates.TryGetValue(image.Url, out var update)) continue;
                image.ThumbUrl = update.ThumbUrl;
                image.Width = update.Width;
                image.Height = update.Height;
                image.FileSizeBytes += update.AddedBytes;
            }
            fresh.SettingsJson = settings.ToJson();
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        if (looked > 0)
            logger.LogWarning("Gallery backfill: {Tiled} tiles made, {Measured} photos measured, {Unreadable} unreadable.", tiled, measured, unreadable);
        return new Result(tiled, measured, unreadable);
    }
}

// Runs the backfill once, a couple of minutes after the web app starts (never on a bystander instance:
// Jobs__RecurringDisabled), a few hundred photos at a time; anything left waits for the next start.
public sealed class GalleryTileBackfillService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<GalleryTileBackfillService> _logger;

    public GalleryTileBackfillService(IServiceScopeFactory scopes, ILogger<GalleryTileBackfillService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IPRODbContext>();
            var blob = scope.ServiceProvider.GetRequiredService<IBlobStorageService>();
            await GalleryTileBackfill.RunAsync(db, blob, _logger, maxPhotos: 300, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gallery backfill stopped; the photos not reached are tried again on the next start.");
        }
    }
}
