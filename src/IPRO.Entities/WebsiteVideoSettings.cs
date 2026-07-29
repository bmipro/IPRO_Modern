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

    private static readonly Regex YouTubeIdPattern = new(
        @"(?:youtube\.com/(?:watch\?v=|embed/|shorts/)|youtu\.be/)([A-Za-z0-9_-]{11})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // youtube-nocookie.com is YouTube's own privacy-enhanced embed domain -- same player, doesn't set
    // tracking cookies until the visitor actually presses play.
    public string? BuildEmbedUrl()
    {
        if (string.IsNullOrWhiteSpace(VideoUrl)) return null;
        var match = YouTubeIdPattern.Match(VideoUrl);
        return match.Success ? $"https://www.youtube-nocookie.com/embed/{match.Groups[1].Value}" : null;
    }
}
