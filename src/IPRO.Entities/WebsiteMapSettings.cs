using System.Text.Json;
using System.Text.Json.Serialization;

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

    [JsonIgnore]
    public int HeightPixels => Height switch
    {
        "compact" => 260,
        "tall" => 520,
        _ => 380
    };

    public static string BuildEmbedUrl(string address) =>
        $"https://www.google.com/maps?q={Uri.EscapeDataString(address)}&output=embed";

    private static string Normalize(string? value, IEnumerable<string> allowed, string fallback)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return allowed.Contains(normalized, StringComparer.OrdinalIgnoreCase) ? normalized : fallback;
    }
}
