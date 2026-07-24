using System.Text.Json;

namespace IPRO.Entities;

public class WebsiteContactFormSettings
{
    public bool ShowPhoto { get; set; } = true;

    public static WebsiteContactFormSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<WebsiteContactFormSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}
