using System;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// 646: "keep my own website" (AgentOwnWebsite). Set-based like FollowUpReminderPreference, so it cannot collide
// with whatever the calling page's DbContext is tracking. No row means the professional uses iPro's website.
public static class OwnWebsitePreference
{
    public sealed record Choice(string? WebsiteUrl, string FormKey);

    public static async Task<Choice?> GetAsync(IPRODbContext db, int agentUserId)
    {
        var row = await db.AgentOwnWebsites.AsNoTracking()
            .Where(r => r.AgentUserId == agentUserId)
            .Select(r => new { r.WebsiteUrl, r.FormKey })
            .FirstOrDefaultAsync();
        return row == null ? null : new Choice(row.WebsiteUrl, row.FormKey);
    }

    public static Task<bool> IsChosenAsync(IPRODbContext db, int agentUserId) =>
        db.AgentOwnWebsites.AsNoTracking().AnyAsync(r => r.AgentUserId == agentUserId);

    // Keeps their own website. Choosing again only updates the address; the form key stays, so a form already
    // placed on their site keeps working.
    public static async Task ChooseAsync(IPRODbContext db, int agentUserId, string? websiteUrl)
    {
        var now = DateTime.UtcNow;
        var url = AgentOwnWebsite.NormalizeUrl(websiteUrl);
        var key = AgentOwnWebsite.NewFormKey();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO `AgentOwnWebsites` (`AgentUserId`, `WebsiteUrl`, `FormKey`, `ChosenAt`, `UpdatedAt`) VALUES ({agentUserId}, {url}, {key}, {now}, {now}) ON DUPLICATE KEY UPDATE `WebsiteUrl` = {url}, `UpdatedAt` = {now}");
    }

    // Back to iPro's website: the row goes, and with it the lead form's address.
    public static Task<int> ClearAsync(IPRODbContext db, int agentUserId) =>
        db.AgentOwnWebsites.Where(r => r.AgentUserId == agentUserId).ExecuteDeleteAsync();

    public static async Task<(int AgentUserId, string? WebsiteUrl)?> FindByFormKeyAsync(IPRODbContext db, string? formKey)
    {
        var key = formKey?.Trim() ?? string.Empty;
        if (key.Length != 32 || !key.All(Uri.IsHexDigit)) return null;
        var row = await db.AgentOwnWebsites.AsNoTracking()
            .Where(r => r.FormKey == key)
            .Select(r => new { r.AgentUserId, r.WebsiteUrl })
            .FirstOrDefaultAsync();
        return row == null ? null : (row.AgentUserId, row.WebsiteUrl);
    }
}
