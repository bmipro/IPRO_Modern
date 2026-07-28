using System.Text.Json;

namespace IPRO.Entities;

public class WebsiteArticleContentSettings
{
    public int ArticleId { get; set; }

    public static WebsiteArticleContentSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<WebsiteArticleContentSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}
