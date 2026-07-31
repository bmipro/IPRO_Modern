using System.Text.RegularExpressions;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// "Resources" is not a new navigation mechanism -- it is a real WebsitePage (parent) with one real
// child WebsitePage per starter article, using the same ParentPageId/ChildPages two-level dropdown
// _PublicNavigation.cshtml already renders for any page. Each child page's block points at a real,
// per-agent Article row rather than a plain starter-block copy (the way EnsureStarterPagesAsync
// works for Home/About/etc.), because the whole point of these being Articles is that the same
// content can also be sent as a newsletter later -- that only works against a real Article.
public static class WebsiteStarterResourcesHelper
{
    private const string ResourcesSlug = "resources";

    public static async Task EnsureResourcesAsync(IPRODbContext db, AgentWebsite website, int agentId)
    {
        // Deliberately not gated on "agent has zero pages" like EnsureStarterPagesAsync -- Resources
        // is new, additive content that should backfill onto agents who already have real pages too,
        // not just brand-new signups.
        if (await db.WebsitePages.AnyAsync(p => p.AgentWebsiteId == website.Id && p.Slug == ResourcesSlug)) return;

        var agent = await db.AgentUsers.AsNoTracking().FirstAsync(a => a.Id == agentId);
        var candidates = await db.WebsiteStarterArticles
            .AsNoTracking()
            .Where(a => a.IsActive && (a.BusinessType == agent.BusinessType || a.BusinessType == "All"))
            .ToListAsync();
        if (candidates.Count == 0) return;

        var selected = candidates
            .GroupBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(a => a.BusinessType == agent.BusinessType).First())
            .OrderBy(a => a.SortOrder)
            .ToList();

        // Phase 1: create the real Articles first. ArticleContent's SettingsJson needs each
        // article's real, database-assigned Id, which does not exist until after a SaveChanges --
        // it cannot be embedded into the block's JSON in the same pass that creates the Article.
        var articles = selected.Select(starter => new Article
        {
            AgentUserId = agentId,
            Title = starter.Title,
            Summary = starter.Summary,
            Content = starter.Content,
            ImageUrl = starter.ImageUrl,
            IsPublished = true,
            PublishedAt = DateTime.UtcNow
        }).ToList();
        db.Articles.AddRange(articles);
        await db.SaveChangesAsync();

        // Phase 2: the Resources page tree, one child page per article, each with a single
        // ArticleContent block now that every article above has a real Id.
        var existingSlugs = (await db.WebsitePages
                .Where(p => p.AgentWebsiteId == website.Id)
                .Select(p => p.Slug)
                .ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextOrder = await db.WebsitePages.CountAsync(p => p.AgentWebsiteId == website.Id);

        var resourcesPage = new WebsitePage
        {
            AgentWebsiteId = website.Id,
            Title = "Resources",
            Slug = ResourcesSlug,
            NavigationLabel = "Resources",
            MetaTitle = "Resources",
            MetaDescription = "Helpful articles and resources.",
            ShowInNavigation = true,
            IsPublished = true,
            SortOrder = nextOrder,
            Blocks = new List<WebsiteContentBlock>
            {
                new()
                {
                    BlockType = WebsiteBlockTypes.Text,
                    Heading = "Resources",
                    Body = "Helpful articles and updates, added from time to time.",
                    SortOrder = 0,
                    IsVisible = true
                }
            }
        };
        db.WebsitePages.Add(resourcesPage);
        existingSlugs.Add(ResourcesSlug);

        var childOrder = 0;
        foreach (var (starter, article) in selected.Zip(articles))
        {
            var slug = UniqueSlug(Slugify(starter.Title), existingSlugs);
            existingSlugs.Add(slug);
            db.WebsitePages.Add(new WebsitePage
            {
                AgentWebsiteId = website.Id,
                ParentPage = resourcesPage,
                Title = starter.Title,
                Slug = slug,
                NavigationLabel = starter.Title,
                MetaTitle = starter.Title,
                MetaDescription = starter.Summary,
                ShowInNavigation = true,
                IsPublished = true,
                SortOrder = childOrder++,
                Blocks = new List<WebsiteContentBlock>
                {
                    new()
                    {
                        BlockType = WebsiteBlockTypes.ArticleContent,
                        SettingsJson = new WebsiteArticleContentSettings { ArticleId = article.Id }.ToJson(),
                        SortOrder = 0,
                        IsVisible = true
                    }
                }
            });
        }

        await db.SaveChangesAsync();
    }

    private static string Slugify(string title) =>
        Regex.Replace(title.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    private static string UniqueSlug(string baseSlug, HashSet<string> existingSlugs)
    {
        if (string.IsNullOrWhiteSpace(baseSlug)) baseSlug = "resource";
        if (!existingSlugs.Contains(baseSlug)) return baseSlug;
        var i = 2;
        while (existingSlugs.Contains($"{baseSlug}-{i}")) i++;
        return $"{baseSlug}-{i}";
    }
}
