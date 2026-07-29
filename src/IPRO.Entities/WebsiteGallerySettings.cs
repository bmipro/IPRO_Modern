using System.Text.Json;

namespace IPRO.Entities;

public class WebsiteGalleryImage
{
    public string Url { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string Caption { get; set; } = string.Empty;
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
