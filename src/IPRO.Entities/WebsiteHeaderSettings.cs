using System.Text.Json;

namespace IPRO.Entities;

public class WebsiteHeaderSettings
{
    public string Style { get; set; } = "standard";
    public string LogoPosition { get; set; } = "left";
    public string LogoSize { get; set; } = "medium";
    public bool Sticky { get; set; }
    public bool ShowPhone { get; set; }
    public bool ShowEmail { get; set; }
    public string ButtonText { get; set; } = string.Empty;
    public string ButtonUrl { get; set; } = string.Empty;
    public List<WebsiteCustomNavigationLink> CustomLinks { get; set; } = new();

    public static WebsiteHeaderSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var value = JsonSerializer.Deserialize<WebsiteHeaderSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            value.Style = Normalize(value.Style, new[] { "standard", "compact", "transparent" }, "standard");
            value.LogoPosition = Normalize(value.LogoPosition, new[] { "left", "center" }, "left");
            value.LogoSize = Normalize(value.LogoSize, new[] { "small", "medium", "large" }, "medium");
            value.ButtonText = value.ButtonText?.Trim() ?? string.Empty;
            value.ButtonUrl = value.ButtonUrl?.Trim() ?? string.Empty;
            value.CustomLinks ??= new();
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

public class WebsiteCustomNavigationLink
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}
