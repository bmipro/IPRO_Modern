using System;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// 661: the professional's Google review link and its switch (AgentGoogleReviewSettings). Set-based like
// FollowUpReminderPreference, so it cannot collide with whatever the calling page's DbContext is tracking.
public static class GoogleReviewPreference
{
    public sealed record Settings(string? ReviewUrl, bool AskEnabled, bool Saved)
    {
        // The button and the thank-you link show only when both hold.
        public bool CanAsk => AskEnabled && !string.IsNullOrWhiteSpace(ReviewUrl);
    }

    public static async Task<Settings> GetAsync(IPRODbContext db, int agentUserId, string? businessType)
    {
        var row = await db.AgentGoogleReviewSettings.AsNoTracking()
            .Where(r => r.AgentUserId == agentUserId)
            .Select(r => new { r.ReviewUrl, r.AskEnabled })
            .FirstOrDefaultAsync();
        return row == null
            ? new Settings(null, GoogleReviews.DefaultAskEnabled(businessType), false)
            : new Settings(row.ReviewUrl, row.AskEnabled, true);
    }

    // 667: the link the adviser's website may use for "Review us on Google" in a Reviews block whose own
    // link is empty. Only under the same rule as the client button (link saved AND the switch on), so a
    // financial adviser who has not turned the switch on gets no review request on their site either.
    public static async Task<string?> WebsiteLinkAsync(IPRODbContext db, int agentUserId, string? businessType)
    {
        var settings = await GetAsync(db, agentUserId, businessType);
        return settings.CanAsk ? settings.ReviewUrl : null;
    }

    public static async Task SetAsync(IPRODbContext db, int agentUserId, string? reviewUrl, bool askEnabled)
    {
        var now = DateTime.UtcNow;
        var url = GoogleReviews.NormalizeUrl(reviewUrl);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO `AgentGoogleReviewSettings` (`AgentUserId`, `ReviewUrl`, `AskEnabled`, `UpdatedAt`) VALUES ({agentUserId}, {url}, {askEnabled}, {now}) ON DUPLICATE KEY UPDATE `ReviewUrl` = {url}, `AskEnabled` = {askEnabled}, `UpdatedAt` = {now}");
    }

    public static Task<DateTime?> LastAskedAsync(IPRODbContext db, int agentUserId, int clientId) =>
        db.ClientGoogleReviewRequests.AsNoTracking()
            .Where(r => r.AgentUserId == agentUserId && r.ClientId == clientId)
            .OrderByDescending(r => r.SentAt)
            .Select(r => (DateTime?)r.SentAt)
            .FirstOrDefaultAsync();
}
