using System.Text.Json;

namespace IPRO.Entities;

public class WebsiteDidYouKnowSettings
{
    public int DripCampaignId { get; set; }

    public static WebsiteDidYouKnowSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<WebsiteDidYouKnowSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}
