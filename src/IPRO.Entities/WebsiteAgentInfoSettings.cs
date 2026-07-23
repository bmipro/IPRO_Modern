using System.Text.Json;

namespace IPRO.Entities;

public class WebsiteAgentInfoSettings
{
    public bool ShowPhoto { get; set; } = true;
    public bool ShowDesignation { get; set; } = true;
    public bool ShowAddress { get; set; } = true;
    public bool ShowPhone { get; set; } = true;
    public bool ShowEmail { get; set; } = true;

    public static WebsiteAgentInfoSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<WebsiteAgentInfoSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}
