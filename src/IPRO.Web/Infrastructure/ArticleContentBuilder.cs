using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

public static class ArticleContentBuilder
{
    public static async Task<Dictionary<int, Article>> BuildAsync(IPRODbContext db, int agentUserId, WebsitePage? currentPage)
    {
        var result = new Dictionary<int, Article>();
        var blocks = currentPage?.Blocks.Where(b => b.BlockType == WebsiteBlockTypes.ArticleContent && b.IsVisible).ToList()
            ?? new List<WebsiteContentBlock>();
        if (blocks.Count == 0) return result;

        var articleIds = blocks
            .Select(b => WebsiteArticleContentSettings.FromJson(b.SettingsJson).ArticleId)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        if (articleIds.Count == 0) return result;

        var articles = await db.Articles
            .Where(a => articleIds.Contains(a.Id) && a.AgentUserId == agentUserId && a.IsPublished)
            .ToDictionaryAsync(a => a.Id);

        foreach (var block in blocks)
        {
            var articleId = WebsiteArticleContentSettings.FromJson(block.SettingsJson).ArticleId;
            if (articleId > 0 && articles.TryGetValue(articleId, out var article))
            {
                result[block.Id] = article;
            }
        }

        return result;
    }
}
