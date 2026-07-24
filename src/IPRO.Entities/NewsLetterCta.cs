using System.Text.Json;

namespace IPRO.Entities;

public class NewsLetterCta
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

public static class NewsLetterSidebarCtas
{
    public static List<NewsLetterCta> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var value = JsonSerializer.Deserialize<List<NewsLetterCta>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return value ?? new();
        }
        catch (JsonException) { return new(); }
    }

    public static string ToJson(List<NewsLetterCta> ctas) => JsonSerializer.Serialize(ctas);
}
