using System.Net;
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
        var blocks = currentPage?.Blocks.Where(b => b.BlockType == WebsiteBlockTypes.DidYouKnow && b.IsVisible).ToList()
            ?? new List<WebsiteContentBlock>();
        if (blocks.Count == 0) return new Dictionary<int, DidYouKnowBlockData>();

        var wanted = blocks.SelectMany(b => WebsiteDidYouKnowSettings.FromJson(b.SettingsJson).ArticleIds).Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<int, DidYouKnowBlockData>();

        var articles = await db.Articles
            .Where(a => wanted.Contains(a.Id) && a.AgentUserId == agentUserId && a.IsPublished)
            .ToListAsync();
        return Build(blocks, articles);
    }

    // 470 (2026-09-09): the same teasers from articles already in hand. The prospect preview has no
    // rows -- its articles are built in memory from starter articles -- and uses this directly.
    public static Dictionary<int, DidYouKnowBlockData> Build(IEnumerable<WebsiteContentBlock> blocks, IReadOnlyCollection<Article> articles)
    {
        var result = new Dictionary<int, DidYouKnowBlockData>();
        foreach (var block in blocks)
        {
            if (block.BlockType != WebsiteBlockTypes.DidYouKnow || !block.IsVisible) continue;
            var settings = WebsiteDidYouKnowSettings.FromJson(block.SettingsJson);
            if (settings.ArticleIds.Count == 0) continue;

            // Preserve the chosen order rather than whatever order the query returned.
            var ordered = settings.ArticleIds
                .Select(id => articles.FirstOrDefault(a => a.Id == id && a.IsPublished))
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
        // 479: the body's entities (&mdash;, &rsquo;, &amp;) read as the characters they stand for; the
        // page encodes the excerpt again, so left in they showed as literal "&mdash;" on the site.
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length <= ExcerptLength) return text;

        var cut = text.LastIndexOf(' ', ExcerptLength);
        if (cut <= 0) cut = ExcerptLength;
        return text[..cut].TrimEnd() + "\u2026";
    }
}
