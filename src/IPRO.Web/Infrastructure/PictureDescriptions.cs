using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

/// <summary>
/// 578: what a website picture shows, for people who cannot see it (WCAG 1.1.1). The owner describes a
/// picture once, in the page editor's Image Library (WebsiteMediaAsset.Description), and every block
/// that uses it says so in its alt text; an article's picture has its own (Article.ImageDescription).
/// An empty description means the picture is decoration, which a screen reader skips (alt="").
/// The description belongs to the picture rather than to a block so that it survives a trip through
/// the Template Builder, whose sections come back rebuilt but whose pictures keep their addresses.
/// </summary>
public static class PictureDescriptions
{
    public const int MaxLength = 300;

    /// <summary>One line, at most 300 characters.</summary>
    public static string Clean(string? text)
    {
        var line = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > MaxLength ? line[..MaxLength].TrimEnd() : line;
    }

    /// <summary>The descriptions of the pictures this page shows, by address. Nothing is asked of the
    /// database for a page without pictures.</summary>
    public static async Task<Dictionary<string, string>> ForPageAsync(IPRODbContext db, int websiteId, WebsitePage? page)
    {
        var urls = PictureUrls(page);
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (urls.Count == 0) return found;
        var rows = await db.WebsiteMediaAssets.AsNoTracking()
            .Where(a => a.AgentWebsiteId == websiteId && urls.Contains(a.BlobUrl) && a.Description != "")
            .Select(a => new { a.BlobUrl, a.Description })
            .ToListAsync();
        foreach (var row in rows) found.TryAdd(row.BlobUrl, row.Description);
        return found;
    }

    private static List<string> PictureUrls(WebsitePage? page)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in page?.Blocks.Where(b => b.IsVisible) ?? Enumerable.Empty<WebsiteContentBlock>())
        {
            if (!string.IsNullOrWhiteSpace(block.ImageUrl)) urls.Add(block.ImageUrl);
            if (block.BlockType == WebsiteBlockTypes.PriceList)
            {
                foreach (var section in WebsitePriceListSettings.FromJson(block.SettingsJson).Sections)
                {
                    if (!string.IsNullOrWhiteSpace(section.ImageUrl)) urls.Add(section.ImageUrl);
                }
            }
        }
        return urls.ToList();
    }
}
