using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// 470 (2026-09-09): the one way to turn starter articles into an agent's own Articles. An article
// the agent already has under the same title is reused, never duplicated -- the rule
// WebsiteStarterResourcesHelper already applies -- so a Did You Know block filled while Home is
// provisioned and the Resources tree built a moment later point at the same rows.
public static class WebsiteStarterArticleCopier
{
    public static async Task<Dictionary<int, Article>> EnsureAgentArticlesAsync(IPRODbContext db, int agentId, IReadOnlyCollection<WebsiteStarterArticle> starters)
    {
        var result = new Dictionary<int, Article>();
        if (starters.Count == 0) return result;

        var titles = starters.Select(s => s.Title).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existing = await db.Articles.Where(a => a.AgentUserId == agentId && titles.Contains(a.Title)).ToListAsync();
        var created = new List<Article>();
        foreach (var starter in starters)
        {
            var article = existing.FirstOrDefault(a => string.Equals(a.Title, starter.Title, StringComparison.OrdinalIgnoreCase))
                          ?? created.FirstOrDefault(a => string.Equals(a.Title, starter.Title, StringComparison.OrdinalIgnoreCase));
            if (article == null)
            {
                article = new Article
                {
                    AgentUserId = agentId, Title = starter.Title, Summary = starter.Summary, Content = starter.Content,
                    ImageUrl = starter.ImageUrl, IsPublished = true, PublishedAt = DateTime.UtcNow
                };
                db.Articles.Add(article);
                created.Add(article);
            }
            result[starter.Id] = article;
        }
        if (created.Count > 0) await db.SaveChangesAsync();
        return result;
    }
}
