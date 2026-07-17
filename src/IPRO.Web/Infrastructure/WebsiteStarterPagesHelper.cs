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
        foreach (var starter in selected)
        {
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
                Blocks = starter.Blocks.OrderBy(b => b.SortOrder).Select(b => new WebsiteContentBlock
                {
                    BlockType = b.BlockType, Heading = b.Heading, Subheading = b.Subheading, Body = b.Body,
                    ImageUrl = b.ImageUrl, ButtonText = b.ButtonText, ButtonUrl = b.ButtonUrl,
                    SettingsJson = b.SettingsJson, SortOrder = b.SortOrder, IsVisible = b.IsVisible
                }).ToList()
            });
        }
        await db.SaveChangesAsync();
    }
}
