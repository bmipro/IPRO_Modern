using System.Text.Json;

namespace IPRO.Entities;

// 470 (2026-09-09): a Did You Know STARTER block cannot hold agent Article ids (they do not exist
// until the agent is provisioned), so it holds the ids of the starter articles SuperAdmin chose.
// Provisioning turns them into the agent's own articles and writes a WebsiteDidYouKnowSettings.
public class WebsiteStarterDidYouKnowSettings
{
    public List<int> StarterArticleIds { get; set; } = new();
    public string LayoutStyle { get; set; } = "auto";

    public static WebsiteStarterDidYouKnowSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<WebsiteStarterDidYouKnowSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}
