using System.Text.Json;

namespace IPRO.Entities;

public class WebsiteHeroSettings
{
    public string Layout { get; set; } = "split";
    public string ImagePosition { get; set; } = "center";
    public string TextAlignment { get; set; } = "left";
    public string Height { get; set; } = "standard";
    public int OverlayStrength { get; set; } = 45;

    public static WebsiteHeroSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var value = JsonSerializer.Deserialize<WebsiteHeroSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            value.Layout = Normalize(value.Layout, new[] { "split", "image-left", "centered", "full-image", "compact" }, "split");
            value.ImagePosition = Normalize(value.ImagePosition, new[] { "center", "left", "right", "top", "bottom" }, "center");
            value.TextAlignment = Normalize(value.TextAlignment, new[] { "left", "center" }, "left");
            value.Height = Normalize(value.Height, new[] { "compact", "standard", "tall" }, "standard");
            value.OverlayStrength = Math.Clamp(value.OverlayStrength, 10, 85);
            return value;
        }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    private static string Normalize(string? value, IEnumerable<string> allowed, string fallback)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return allowed.Contains(normalized, StringComparer.OrdinalIgnoreCase) ? normalized : fallback;
    }
}
