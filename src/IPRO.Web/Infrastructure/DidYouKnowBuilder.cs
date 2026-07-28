using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

public static class DidYouKnowBuilder
{
    // Only exposes each step's Subject as a teaser - never HtmlBody. The full article is the
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
            if (settings.DripCampaignId <= 0) continue;

            var campaign = await db.DripCampaigns.FirstOrDefaultAsync(c => c.Id == settings.DripCampaignId && c.AgentUserId == agentUserId && c.IsActive);
            if (campaign == null) continue;

            var teasers = await db.DripCampaignSteps
                .Where(s => s.DripCampaignId == campaign.Id)
                .OrderBy(s => s.SortOrder)
                .Select(s => s.Subject)
                .ToListAsync();
            if (teasers.Count == 0) continue;

            result[block.Id] = new DidYouKnowBlockData
            {
                CampaignName = campaign.Name,
                Teasers = teasers,
                LayoutStyle = settings.LayoutStyle
            };
        }

        return result;
    }
}
