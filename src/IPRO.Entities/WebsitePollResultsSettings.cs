using System.Text.Json;

namespace IPRO.Entities;

public class WebsitePollResultsSettings
{
    public int PollSurveyId { get; set; }

    public static WebsitePollResultsSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<WebsitePollResultsSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}
