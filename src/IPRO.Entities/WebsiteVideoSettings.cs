using System.Text.Json;
using System.Text.RegularExpressions;

namespace IPRO.Entities;

public class WebsiteVideoSettings
{
    public string VideoUrl { get; set; } = string.Empty;

    public static WebsiteVideoSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var value = JsonSerializer.Deserialize<WebsiteVideoSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            value.VideoUrl = value.VideoUrl?.Trim() ?? string.Empty;
            return value;
        }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    private static readonly Regex VideoIdPattern = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    // youtube-nocookie.com is YouTube's own privacy-enhanced embed domain -- same player, doesn't set
    // tracking cookies until the visitor actually presses play.
    //
    // Parses the URL structurally (host + path segments + query params) rather than a single regex --
    // real share links vary a lot: youtu.be/<id>, youtube.com/watch?v=<id> with other tracking params
    // before or after v=, m.youtube.com and music.youtube.com subdomains, /embed/, /shorts/, /live/.
    public string? BuildEmbedUrl()
    {
        if (string.IsNullOrWhiteSpace(VideoUrl)) return null;
        if (!Uri.TryCreate(VideoUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;

        var host = uri.Host;
        var isYouTubeHost = host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);
        var isShortHost = host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase);
        if (!isYouTubeHost && !isShortHost) return null;

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        string? id = null;
        if (isShortHost && segments.Length >= 1)
        {
            id = segments[0];
        }
        else if (isYouTubeHost && segments.Length >= 2 && segments[0] is "embed" or "shorts" or "live")
        {
            id = segments[1];
        }
        else if (isYouTubeHost && segments.Length >= 1 && segments[0] == "watch")
        {
            id = GetQueryParam(uri.Query, "v");
        }

        return id != null && VideoIdPattern.IsMatch(id) ? $"https://www.youtube-nocookie.com/embed/{id}" : null;
    }

    private static string? GetQueryParam(string query, string key)
    {
        query = query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && Uri.UnescapeDataString(parts[0]) == key)
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }
        return null;
    }
}
