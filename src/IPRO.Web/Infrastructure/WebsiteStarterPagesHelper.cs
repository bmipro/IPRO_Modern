using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

public static class WebsiteStarterPagesHelper
{
    public static async Task EnsureStarterPagesAsync(IPRODbContext db, AgentWebsite website, int agentId)
    {
        if (await db.WebsitePages.AnyAsync(p => p.AgentWebsiteId == website.Id)) return;
        var agent = await db.AgentUsers.AsNoTracking().FirstAsync(a => a.Id == agentId);
        var candidates = await db.WebsiteStarterPages
            .AsNoTracking()
            .Include(p => p.Blocks)
            .Where(p => p.IsActive &&
                        (p.BusinessType == agent.BusinessType || p.BusinessType == "All") &&
                        (!p.BillingRuleId.HasValue || p.BillingRuleId == agent.PackageId))
            .ToListAsync();
        var selected = candidates
            .GroupBy(p => p.Slug, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(p => p.BusinessType == agent.BusinessType)
                .ThenByDescending(p => p.BillingRuleId == agent.PackageId)
                .First())
            .OrderBy(p => p.SortOrder)
            .ToList();
        // The Request Meeting page gets the vertical's real "Request a Meeting" form (the owner's
        // 2026-08-15 redesign) instead of the plain contact block it was seeded with. The form must
        // exist BEFORE the page blocks reference its id -- same two-phase shape as EnsureResourcesAsync.
        // If the starter form is ever deactivated or renamed, this quietly falls back to the seeded
        // contact block rather than provisioning a broken page.
        var meetingTemplate = (await db.WebsiteStarterForms
                .AsNoTracking()
                .Where(f => f.IsActive && f.Title == WebsiteStarterFormSeeder.MeetingFormTitle &&
                            (f.BusinessType == agent.BusinessType || f.BusinessType == "All"))
                .ToListAsync())
            .OrderByDescending(f => f.BusinessType == agent.BusinessType)
            .FirstOrDefault();
        string? meetingFormSettingsJson = null;
        if (meetingTemplate != null && selected.Any(p => p.Slug.Equals("request-meeting", StringComparison.OrdinalIgnoreCase)))
        {
            var meetingForm = await WebsiteFormTemplateCopier.CopyToAgentAsync(db, meetingTemplate, agentId);
            meetingFormSettingsJson = new WebsiteFormSettings { WebsiteFormId = meetingForm.Id }.ToJson();
        }

        // 470 (2026-09-09): a Did You Know starter block names STARTER articles. The agent's own
        // Articles are created first (or reused by title), then the block is written with their real
        // ids -- the same two-phase shape as the meeting form above and the Resources tree.
        var didYouKnowStarterIds = selected.SelectMany(p => p.Blocks)
            .Where(b => b.BlockType == WebsiteBlockTypes.DidYouKnow)
            .SelectMany(b => WebsiteStarterDidYouKnowSettings.FromJson(b.SettingsJson).StarterArticleIds)
            .Distinct()
            .ToList();
        var articlesByStarterId = new Dictionary<int, Article>();
        if (didYouKnowStarterIds.Count > 0)
        {
            var starterArticles = await db.WebsiteStarterArticles.AsNoTracking()
                .Where(a => a.IsActive && didYouKnowStarterIds.Contains(a.Id))
                .ToListAsync();
            articlesByStarterId = await WebsiteStarterArticleCopier.EnsureAgentArticlesAsync(db, agentId, starterArticles);
        }

        foreach (var starter in selected)
        {
            var isMeetingPage = starter.Slug.Equals("request-meeting", StringComparison.OrdinalIgnoreCase);
            db.WebsitePages.Add(new WebsitePage
            {
                AgentWebsiteId = website.Id,
                Title = starter.Title,
                Slug = starter.Slug,
                NavigationLabel = starter.NavigationLabel,
                MetaTitle = starter.MetaTitle,
                MetaDescription = starter.MetaDescription,
                IsHomePage = starter.IsHomePage,
                ShowInNavigation = starter.ShowInNavigation,
                IsPublished = true,
                SortOrder = starter.SortOrder,
                Blocks = starter.Blocks.OrderBy(b => b.SortOrder).Select(b =>
                    isMeetingPage && meetingFormSettingsJson != null && b.BlockType == WebsiteBlockTypes.ContactForm
                        ? new WebsiteContentBlock
                        {
                            BlockType = WebsiteBlockTypes.Form,
                            Heading = b.Heading, Subheading = b.Subheading, Body = b.Body,
                            SettingsJson = meetingFormSettingsJson, SortOrder = b.SortOrder, IsVisible = b.IsVisible
                        }
                        : new WebsiteContentBlock
                        {
                            BlockType = b.BlockType, Heading = b.Heading, Subheading = b.Subheading, Body = b.Body,
                            ImageUrl = b.ImageUrl, ButtonText = b.ButtonText, ButtonUrl = b.ButtonUrl,
                            SettingsJson = b.BlockType == WebsiteBlockTypes.DidYouKnow
                                ? ProvisionedDidYouKnow(b.SettingsJson, articlesByStarterId)
                                : b.SettingsJson,
                            SortOrder = b.SortOrder, IsVisible = b.IsVisible
                        }).ToList()
            });
        }
        await db.SaveChangesAsync();
    }

    // The starter block's chosen starter articles, as the agent's own article ids, in the chosen
    // order; a starter article that has since been retired simply drops out.
    private static string ProvisionedDidYouKnow(string starterSettingsJson, Dictionary<int, Article> articlesByStarterId)
    {
        var starter = WebsiteStarterDidYouKnowSettings.FromJson(starterSettingsJson);
        return new WebsiteDidYouKnowSettings
        {
            ArticleIds = starter.StarterArticleIds
                .Where(articlesByStarterId.ContainsKey)
                .Select(id => articlesByStarterId[id].Id)
                .Distinct()
                .ToList(),
            LayoutStyle = starter.LayoutStyle == "grid-2x3" ? "grid-2x3" : "auto"
        }.ToJson();
    }
}
