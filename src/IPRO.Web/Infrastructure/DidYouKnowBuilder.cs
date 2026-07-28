using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

public static class DidYouKnowBuilder
{
    // Only exposes each Article's Summary as a teaser - never Content. The full article is the
    // whole point of the email-gate this block exists to drive; leaking it into the public page
    // markup before a visitor submits their email would defeat the mechanism entirely.
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
                .Select(a => string.IsNullOrWhiteSpace(a.Summary) ? a.Title : a.Summary)
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
}
