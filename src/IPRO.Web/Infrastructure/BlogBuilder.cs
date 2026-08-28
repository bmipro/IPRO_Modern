using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// Data for the Blog block, mirroring ArticleContentBuilder's shape.
//
// The two rules that matter here are the same two that protect every other public block:
//   - Only this agent's own articles, and only PUBLISHED ones. `selectedPostId` arrives from a
//     query string a visitor controls, so it is filtered by AgentUserId and IsPublished exactly
//     like the list is -- a guessed id from another agent's site resolves to nothing and the block
//     falls back to the list.
//   - Nothing unbounded reaches SQL. The list is capped by the block's EffectivePostCount, which
//     clamps a hand-edited settings value to at most 50.
public static class BlogBuilder
{
    public static async Task<Dictionary<int, BlogBlockData>> BuildAsync(
        IPRODbContext db, int agentUserId, WebsitePage? currentPage, int selectedPostId)
    {
        var result = new Dictionary<int, BlogBlockData>();
        var blocks = currentPage?.Blocks.Where(b => b.BlockType == WebsiteBlockTypes.Blog && b.IsVisible).ToList()
            ?? new List<WebsiteContentBlock>();
        if (blocks.Count == 0) return result;

        // One query serves every Blog block on the page: take the largest count any block asks for,
        // then slice per block. A page with two blogs is unusual but must not cost two scans.
        var settingsByBlock = blocks.ToDictionary(b => b.Id, b => WebsiteBlogSettings.FromJson(b.SettingsJson));
        var maxCount = settingsByBlock.Values.Max(s => s.EffectivePostCount);

        var posts = await db.Articles
            .AsNoTracking()
            .Where(a => a.AgentUserId == agentUserId && a.IsPublished)
            .OrderByDescending(a => a.PublishedAt ?? a.CreatedAt)
            .Take(maxCount)
            .ToListAsync();

        Article? selected = null;
        if (selectedPostId > 0)
        {
            selected = await db.Articles
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == selectedPostId && a.AgentUserId == agentUserId && a.IsPublished);
        }

        foreach (var block in blocks)
        {
            var settings = settingsByBlock[block.Id];
            result[block.Id] = new BlogBlockData
            {
                Posts = posts.Take(settings.EffectivePostCount).ToList(),
                SelectedPost = selected,
                ShowImages = settings.ShowImages
            };
        }

        return result;
    }
}
