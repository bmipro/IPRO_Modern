using System.Text.Json;

namespace IPRO.Entities;

public class WebsiteDidYouKnowSettings
{
    public List<int> ArticleIds { get; set; } = new();
    public string LayoutStyle { get; set; } = "auto";

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
