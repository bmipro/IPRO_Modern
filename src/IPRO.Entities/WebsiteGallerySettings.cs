using System.Text.Json;
using System.Text.Json.Serialization;

namespace IPRO.Entities;

public class WebsiteGalleryImage
{
    public const int CaptionMaxLength = 200;

    // The full view (the viewer and the slideshow). From 535 the long edge is at most 1600 px and the
    // photo's metadata is gone; a photo uploaded before 535 is the original file.
    public string Url { get; set; } = string.Empty;
    // 535: the small copy the tiles show (the long edge at most 800 px). Empty when the full view is
    // already that small, or for a photo from before 535 until the backfill makes one.
    public string ThumbUrl { get; set; } = string.Empty;
    // 535: the full view's size in pixels, for the page's layout. 0 means not measured yet (a photo from
    // before 535, which the backfill measures); -1 means the backfill could not read the file.
    public int Width { get; set; }
    public int Height { get; set; }
    // Every stored file of this photo (the full view plus the small copy); the storage pool counts it.
    public long FileSizeBytes { get; set; }
    public string Caption { get; set; } = string.Empty;

    [JsonIgnore]
    public string TileUrl => string.IsNullOrWhiteSpace(ThumbUrl) ? Url : ThumbUrl;
}

public class WebsiteGallerySettings
{
    public List<WebsiteGalleryImage> Images { get; set; } = new();

    public static WebsiteGallerySettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<WebsiteGallerySettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    public long TotalBytes() => Images.Sum(i => i.FileSizeBytes);
}
