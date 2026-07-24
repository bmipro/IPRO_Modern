using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IPRO.Entities;

public class WebsiteMapSettings
{
    public string Address { get; set; } = string.Empty;
    public string Height { get; set; } = "standard";

    // Cached geocode result for GeocodedAddress (the effective address -- override or agent profile --
    // as of the last save). Re-geocoded only when the effective address changes, never on a public page view.
    public string GeocodedAddress { get; set; } = string.Empty;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? BboxSouth { get; set; }
    public double? BboxNorth { get; set; }
    public double? BboxWest { get; set; }
    public double? BboxEast { get; set; }

    public static WebsiteMapSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var value = JsonSerializer.Deserialize<WebsiteMapSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            value.Address = value.Address?.Trim() ?? string.Empty;
            value.Height = Normalize(value.Height, new[] { "compact", "standard", "tall" }, "standard");
            return value;
        }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    [JsonIgnore]
    public int HeightPixels => Height switch
    {
        "compact" => 260,
        "tall" => 520,
        _ => 380
    };

    // OpenStreetMap's embed frame takes a bounding box + marker, not a free-text address -- unlike
    // Google's (unreliable, no-key) embed, so the address must already be geocoded into Latitude/Longitude.
    public string? BuildEmbedUrl()
    {
        if (Latitude is null || Longitude is null) return null;
        var hasBbox = BboxSouth is not null && BboxNorth is not null && BboxWest is not null && BboxEast is not null;
        var south = hasBbox ? BboxSouth!.Value : Latitude.Value - 0.01;
        var north = hasBbox ? BboxNorth!.Value : Latitude.Value + 0.01;
        var west = hasBbox ? BboxWest!.Value : Longitude.Value - 0.01;
        var east = hasBbox ? BboxEast!.Value : Longitude.Value + 0.01;
        var bbox = $"{F(west)},{F(south)},{F(east)},{F(north)}";
        return $"https://www.openstreetmap.org/export/embed.html?bbox={Uri.EscapeDataString(bbox)}&layer=mapnik&marker={F(Latitude.Value)},{F(Longitude.Value)}";
    }

    private static string F(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Normalize(string? value, IEnumerable<string> allowed, string fallback)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return allowed.Contains(normalized, StringComparer.OrdinalIgnoreCase) ? normalized : fallback;
    }
}
