using System.Text.Json;

namespace IPRO.Entities;

public class WebsiteMapSettings
{
    public string Address { get; set; } = string.Empty;
    public string Height { get; set; } = "standard";

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

    public int HeightPixels => Height switch
    {
        "compact" => 260,
        "tall" => 520,
        _ => 380
    };

    // Google's officially documented Maps Embed API -- unlike the free "output=embed" trick and OpenStreetMap's
    // embed frame (both confirmed unreliable/blocked when embedded on third-party sites), this is the one
    // actually designed and guaranteed to work for exactly this use case. Requires a valid API key.
    public static string? BuildEmbedUrl(string address, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(apiKey)) return null;
        return $"https://www.google.com/maps/embed/v1/place?key={Uri.EscapeDataString(apiKey)}&q={Uri.EscapeDataString(address)}";
    }

    private static string Normalize(string? value, IEnumerable<string> allowed, string fallback)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return allowed.Contains(normalized, StringComparer.OrdinalIgnoreCase) ? normalized : fallback;
    }
}
