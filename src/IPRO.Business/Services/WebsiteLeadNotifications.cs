using System;
using System.Threading.Tasks;
using IPRO.DataAccess;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Business.Services;

// 493 (2026-09-17): a public lead form sends the adviser one email per submission, inline and
// transactional, and the subscription is capped at 100 sends an hour. Left alone, one machine
// working one form could spend the platform's whole hourly allowance -- every adviser's newsletters,
// invoices and password resets queued behind it. Ten notifications an hour per website is more than
// any real site receives; past that the lead is still saved and shown under Website Leads, only the
// email is held, and the row says so.
public static class WebsiteLeadNotifications
{
    public const int PerWebsitePerHour = 10;

    public static readonly string HeldMessage =
        $"Not emailed: this website has already sent {PerWebsitePerHour} lead notifications in the last hour. The lead is saved here; notifications resume within the hour.";

    public static async Task<bool> IsHeldAsync(IPRODbContext db, int websiteId, DateTime now)
    {
        var since = now.AddHours(-1);
        var sent = await db.WebsiteLeads.CountAsync(l => l.AgentWebsiteId == websiteId && l.NotificationSent && l.CreatedAt >= since);
        return sent >= PerWebsitePerHour;
    }
}
