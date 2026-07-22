using System.Text.Json;

namespace IPRO.Entities;

public static class ReviewPlatforms
{
    public const string Google = "Google";
    public const string Facebook = "Facebook";
}

public class WebsiteReviewSettings
{
    public string Platform { get; set; } = ReviewPlatforms.Google;
    public string ReviewUrl { get; set; } = string.Empty;
    public decimal Rating { get; set; } = 5.0m;
    public int ReviewCount { get; set; }

    public static WebsiteReviewSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var value = JsonSerializer.Deserialize<WebsiteReviewSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            value.Platform = value.Platform?.Trim().Equals(ReviewPlatforms.Facebook, StringComparison.OrdinalIgnoreCase) == true
                ? ReviewPlatforms.Facebook
                : ReviewPlatforms.Google;
            value.Rating = Math.Clamp(value.Rating, 0m, 5m);
            value.ReviewCount = Math.Max(0, value.ReviewCount);
            return value;
        }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}
