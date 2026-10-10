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

    // 560: reviews the owner picked and pasted in (the block showed stars and a count, never a
    // review). Shown as cards under the rating. They are the owner's choice and do not update
    // themselves; the rating and the count above are typed by hand as before.
    public const int MaxQuotes = 6;
    public const int QuoteNameMaxLength = 80;
    public const int QuoteTextMaxLength = 600;
    public List<WebsiteReviewQuote> Quotes { get; set; } = new();
    // Where "Review us on Google" sends a visitor (the business's own write-a-review link).
    public string WriteReviewUrl { get; set; } = string.Empty;

    // What the editor posts: up to six rows. A row without text is no review; stars are 1 to 5.
    public static List<WebsiteReviewQuote> QuotesFromEntries(IEnumerable<(string? Name, int Stars, string? Text)> entries)
    {
        var quotes = new List<WebsiteReviewQuote>();
        foreach (var entry in entries ?? Array.Empty<(string?, int, string?)>())
        {
            var text = (entry.Text ?? string.Empty).Trim();
            if (text.Length == 0) continue;
            var name = (entry.Name ?? string.Empty).Trim();
            quotes.Add(new WebsiteReviewQuote
            {
                Name = name.Length > QuoteNameMaxLength ? name[..QuoteNameMaxLength].TrimEnd() : name,
                Stars = entry.Stars is >= 1 and <= 5 ? entry.Stars : 5,
                Text = text.Length > QuoteTextMaxLength ? text[..QuoteTextMaxLength].TrimEnd() : text
            });
            if (quotes.Count == MaxQuotes) break;
        }
        return quotes;
    }

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
            value.Quotes = QuotesFromEntries((value.Quotes ?? new()).Select(q => ((string?)q.Name, q.Stars, (string?)q.Text)));
            value.WriteReviewUrl = value.WriteReviewUrl?.Trim() ?? string.Empty;
            if (!value.WriteReviewUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !value.WriteReviewUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                value.WriteReviewUrl = string.Empty;
            return value;
        }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}

public class WebsiteReviewQuote
{
    public string Name { get; set; } = string.Empty;
    public int Stars { get; set; } = 5;
    public string Text { get; set; } = string.Empty;
}
