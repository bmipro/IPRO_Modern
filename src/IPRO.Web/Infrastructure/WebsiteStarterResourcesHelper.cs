using System.Text.RegularExpressions;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// "Resources" is not a new navigation mechanism -- it is a real WebsitePage (parent) with one real
// child WebsitePage per starter article (or per Category, see below), using the same
// ParentPageId/ChildPages two-level dropdown _PublicNavigation.cshtml already renders for any page.
// Each ArticleContent block points at a real, per-agent Article row rather than a plain
// starter-block copy (the way EnsureStarterPagesAsync works for Home/About/etc.), because the whole
// point of these being Articles is that the same content can also be sent as a newsletter later --
// that only works against a real Article.
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
        var calculatorEntries = VerticalCalculatorCatalog.ForBusinessType(agent.BusinessType);
        // Calculators come from a code-level catalog, so Resources is worth building even for a
        // vertical that has no starter articles yet.
        if (candidates.Count == 0 && calculatorEntries.Count == 0) return;

        var selected = candidates
            .GroupBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(a => a.BusinessType == agent.BusinessType).First())
            .OrderBy(a => a.SortOrder)
            .ToList();

        // Phase 1: create the real Articles first. ArticleContent's SettingsJson needs each
        // article's real, database-assigned Id, which does not exist until after a SaveChanges --
        // it cannot be embedded into the block's JSON in the same pass that creates the Article.
        //
        // An article the agent already has under the same title is reused rather than duplicated, so
        // rebuilding the Resources tree (SuperAdmin's Rebuild Resources action) doesn't leave a
        // second copy of every article behind -- and doesn't discard edits made to the first copy.
        var starterTitles = selected.Select(starter => starter.Title).ToList();
        var reusable = await db.Articles
            .Where(a => a.AgentUserId == agentId && starterTitles.Contains(a.Title))
            .ToListAsync();

        var articles = new List<Article>();
        foreach (var starter in selected)
        {
            var existing = reusable.FirstOrDefault(a => a.Title == starter.Title);
            if (existing != null)
            {
                articles.Add(existing);
                continue;
            }

            var created = new Article
            {
                AgentUserId = agentId,
                Title = starter.Title,
                Summary = starter.Summary,
                Content = starter.Content,
                ImageUrl = starter.ImageUrl,
                IsPublished = true,
                PublishedAt = DateTime.UtcNow
            };
            db.Articles.Add(created);
            articles.Add(created);
        }
        await db.SaveChangesAsync();

        // Phase 2: the Resources page tree, now that every article above has a real Id to point at.
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
                },
                new()
                {
                    BlockType = WebsiteBlockTypes.SectionIndex,
                    SortOrder = 1,
                    IsVisible = true
                }
            }
        };
        db.WebsitePages.Add(resourcesPage);
        existingSlugs.Add(ResourcesSlug);

        // Nav renders three levels, so a Category is a real middle tier: Resources -> Category ->
        // one page per article. Uncategorized starters (every non-Accountants vertical today) keep
        // the original one-page-per-article behavior directly under Resources. Either way every
        // article is a real, independently editable Article row on its own page.
        var childOrder = 0;
        var pairs = selected.Zip(articles, (starter, article) => (starter, article)).ToList();
        foreach (var group in pairs.GroupBy(p => p.starter.Category))
        {
            if (string.IsNullOrWhiteSpace(group.Key))
            {
                foreach (var (starter, article) in group)
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
                continue;
            }

            var categorySlug = UniqueSlug(Slugify(group.Key), existingSlugs);
            existingSlugs.Add(categorySlug);
            var ordered = group.OrderBy(p => p.starter.SortOrder).ToList();

            // The category page lists what sits under it rather than holding the articles itself --
            // it stays correct as pages are added or renamed, and gives anywhere the third nav tier
            // isn't reachable (a narrow screen, a search landing) a real way through to each article.
            var categoryPage = new WebsitePage
            {
                AgentWebsiteId = website.Id,
                ParentPage = resourcesPage,
                Title = group.Key,
                Slug = categorySlug,
                NavigationLabel = group.Key,
                MetaTitle = group.Key,
                MetaDescription = $"{group.Key} articles and resources.",
                ShowInNavigation = true,
                IsPublished = true,
                SortOrder = childOrder++,
                Blocks = new List<WebsiteContentBlock>
                {
                    new()
                    {
                        BlockType = WebsiteBlockTypes.SectionIndex,
                        Heading = group.Key,
                        SortOrder = 0,
                        IsVisible = true
                    }
                }
            };
            db.WebsitePages.Add(categoryPage);

            var articleOrder = 0;
            foreach (var (starter, article) in ordered)
            {
                var articleSlug = UniqueSlug(Slugify(starter.Title), existingSlugs);
                existingSlugs.Add(articleSlug);
                db.WebsitePages.Add(new WebsitePage
                {
                    AgentWebsiteId = website.Id,
                    ParentPage = categoryPage,
                    Title = starter.Title,
                    Slug = articleSlug,
                    NavigationLabel = starter.Title,
                    MetaTitle = starter.Title,
                    MetaDescription = starter.Summary,
                    ShowInNavigation = true,
                    IsPublished = true,
                    SortOrder = articleOrder++,
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
        }

        // Calculators category -- the owner's cal1 library (2026-08-15), assigned per vertical by
        // VerticalCalculatorCatalog. Same Category -> child-page shape as articles, but each child
        // holds a Calculator block instead of an Article; the blurb doubles as the MetaDescription
        // the parent SectionIndex renders on its cards.
        if (calculatorEntries.Count > 0)
        {
            var calcCategorySlug = UniqueSlug("calculators", existingSlugs);
            existingSlugs.Add(calcCategorySlug);
            var calcCategoryPage = new WebsitePage
            {
                AgentWebsiteId = website.Id,
                ParentPage = resourcesPage,
                Title = "Calculators",
                Slug = calcCategorySlug,
                NavigationLabel = "Calculators",
                MetaTitle = "Calculators",
                MetaDescription = "Free financial calculators you can use any time.",
                ShowInNavigation = true,
                IsPublished = true,
                SortOrder = childOrder++,
                Blocks = new List<WebsiteContentBlock>
                {
                    new()
                    {
                        BlockType = WebsiteBlockTypes.SectionIndex,
                        Heading = "Calculators",
                        SortOrder = 0,
                        IsVisible = true
                    }
                }
            };
            db.WebsitePages.Add(calcCategoryPage);

            var calcOrder = 0;
            foreach (var entry in calculatorEntries)
            {
                var title = CalculatorKinds.DisplayName(entry.Kind);
                var calcSlug = UniqueSlug(Slugify(title), existingSlugs);
                existingSlugs.Add(calcSlug);
                db.WebsitePages.Add(new WebsitePage
                {
                    AgentWebsiteId = website.Id,
                    ParentPage = calcCategoryPage,
                    Title = title,
                    Slug = calcSlug,
                    NavigationLabel = title,
                    MetaTitle = title,
                    MetaDescription = entry.Blurb,
                    ShowInNavigation = true,
                    IsPublished = true,
                    SortOrder = calcOrder++,
                    Blocks = new List<WebsiteContentBlock>
                    {
                        new()
                        {
                            BlockType = WebsiteBlockTypes.Calculator,
                            Heading = title,
                            Subheading = entry.Blurb,
                            SettingsJson = new WebsiteCalculatorSettings { CalculatorKind = entry.Kind }.ToJson(),
                            SortOrder = 0,
                            IsVisible = true
                        }
                    }
                });
            }
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
