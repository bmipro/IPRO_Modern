using System.Text.RegularExpressions;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

public static class DidYouKnowBuilder
{
    private const int ExcerptLength = 150;

    // Only exposes a short excerpt of each Article's Content as a teaser - never the full body.
    // The full article is the whole point of the email-gate this block exists to drive; leaking
    // it into the public page markup before a visitor submits their email would defeat it.
    public static async Task<Dictionary<int, DidYouKnowBlockData>> BuildAsync(IPRODbContext db, int agentUserId, WebsitePage? currentPage)
    {
        var result = new Dictionary<int, DidYouKnowBlockData>();
        var blocks = currentPage?.Blocks.Where(b => b.BlockType == WebsiteBlockTypes.DidYouKnow && b.IsVisible).ToList()
            ?? new List<WebsiteContentBlock>();
        if (blocks.Count == 0) return result;

        foreach (var block in blocks)
        {
            var settings = WebsiteDidYouKnowSettings.FromJson(block.SettingsJson);
            if (settings.ArticleIds.Count == 0) continue;

            var articles = await db.Articles
                .Where(a => settings.ArticleIds.Contains(a.Id) && a.AgentUserId == agentUserId && a.IsPublished)
                .ToListAsync();
            if (articles.Count == 0) continue;

            // Preserve the agent's chosen order rather than whatever order the query returned.
            var ordered = settings.ArticleIds
                .Select(id => articles.FirstOrDefault(a => a.Id == id))
                .Where(a => a != null)
                .Select(a => a!)
                .ToList();

            var teasers = ordered
                .Select(a => new DidYouKnowTeaser
                {
                    ArticleId = a.Id,
                    Title = a.Title,
                    Excerpt = BuildExcerpt(a.Content)
                })
                .ToList();
            if (teasers.Count == 0) continue;

            result[block.Id] = new DidYouKnowBlockData
            {
                Teasers = teasers,
                LayoutStyle = settings.LayoutStyle
            };
        }

        return result;
    }

    private static string BuildExcerpt(string html)
    {
        var text = Regex.Replace(html ?? string.Empty, "<.*?>", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length <= ExcerptLength) return text;

        var cut = text.LastIndexOf(' ', ExcerptLength);
        if (cut <= 0) cut = ExcerptLength;
        return text[..cut].TrimEnd() + "…";
    }
}
