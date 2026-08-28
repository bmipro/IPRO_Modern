using System.Text.Json;

namespace IPRO.Entities;

// Settings for the Blog block (2026-08-28). The block lists the agent's PUBLISHED articles newest
// first and, when a visitor picks one, renders that post in full on the same page.
//
// Why no per-post route: this product's public routing is single-segment (`/{slug}`) with
// site-wide-unique slugs, and the /portal collision that shape produced took four separate fixes
// (INVARIANTS rule 1). A `?post=` query parameter on the page the agent already owns needs no new
// route, no new column and no slug, and reads to a visitor exactly like a blog. Pretty per-post
// URLs remain an additive change later, on a calm week.
public class WebsiteBlogSettings
{
    // How many posts the list shows. Clamped on read so a hand-edited value cannot ask for
    // thousands of rows on a public page.
    public int PostCount { get; set; } = 6;

    // Cover images in the list. The full post always shows its image.
    public bool ShowImages { get; set; } = true;

    public const int MinPostCount = 1;
    public const int MaxPostCount = 50;

    public int EffectivePostCount => PostCount < MinPostCount
        ? MinPostCount
        : PostCount > MaxPostCount ? MaxPostCount : PostCount;

    public static WebsiteBlogSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<WebsiteBlogSettings>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}
