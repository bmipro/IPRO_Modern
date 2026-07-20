using System.Text.Json;

namespace IPRO.Entities;

public class WebsiteLeadMagnetSettings
{
    public int AgentDocumentId { get; set; }

    public static WebsiteLeadMagnetSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<WebsiteLeadMagnetSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}
