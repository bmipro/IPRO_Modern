using System.Text.Json;

namespace IPRO.Entities;

public class WebsiteFormSettings
{
    public int WebsiteFormId { get; set; }

    public static WebsiteFormSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<WebsiteFormSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}
