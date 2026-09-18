using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// 498: the profile switch behind "Email me my follow-ups each morning" (AgentFollowUpReminder).
// No row means ON, so switching it on for an adviser who never switched it off writes nothing.
// Set-based on purpose: nothing is tracked, so it cannot collide with whatever else the calling
// page's DbContext is in the middle of, and saving the same answer twice is not an error.
public static class FollowUpReminderPreference
{
    public static async Task<bool> IsEnabledAsync(IPRODbContext db, int agentUserId) =>
        !await db.AgentFollowUpReminders.AsNoTracking().AnyAsync(r => r.AgentUserId == agentUserId && !r.IsEnabled);

    public static async Task SetAsync(IPRODbContext db, int agentUserId, bool enabled)
    {
        var now = DateTime.UtcNow;
        var updated = await db.AgentFollowUpReminders
            .Where(r => r.AgentUserId == agentUserId)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.IsEnabled, enabled).SetProperty(r => r.UpdatedAt, now));
        if (updated > 0 || enabled) return;

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO `AgentFollowUpReminders` (`AgentUserId`, `IsEnabled`, `UpdatedAt`) VALUES ({agentUserId}, 0, {now}) ON DUPLICATE KEY UPDATE `IsEnabled` = 0, `UpdatedAt` = {now}");
    }
}
