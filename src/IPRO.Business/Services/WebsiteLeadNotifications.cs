using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Business.Services;

// 493 (2026-09-17): a public lead form sends the adviser one email per submission, inline and
// transactional, and the subscription is capped at 100 sends an hour. Left alone, one machine
// working one form could spend the platform's whole hourly allowance -- every adviser's newsletters,
// invoices and password resets queued behind it. Ten notifications an hour per website is more than
// any real site receives; past that the lead is still saved and shown under Website Leads, only the
// email is held, and the row says so.
//
// 632 H-2 (2026-11-24), the two halves the audit left for after launch:
//  - a limit across ALL websites as well. Ten an hour per site did not stop a flood spread over many
//    sites from taking the whole hour; the send gate keeps 20 of the 100 for iPro's own mail (password
//    resets, invoices, invitations). Every website's first AlwaysPerWebsitePerHour an hour still always
//    go (WithinSharedLimitAsync); past that a site sends only while all websites together are under
//    PlatformPerHour. Without that floor (the first build, caught by the 2026-11-24 audit, D-3) one
//    outsider flooding ONE site's form filled the shared hour and held every other adviser's lead
//    emails -- worse than before, when a flood silenced only the site it hit.
//  - a held notification is SENT LATER. It used to stay held for good ("notifications resume within
//    the hour" meant the next lead's, not this one's). HeldLeadNotificationJob sends held ones, oldest
//    first, as soon as both caps have room, for a day; after a day the row says it was not emailed.
// NotificationSentAt records when the email went, so one sent late counts in the hour it was sent.
public static class WebsiteLeadNotifications
{
    public const int PerWebsitePerHour = 10;
    public const int AlwaysPerWebsitePerHour = 3;
    public const int PlatformPerHour = 15;

    // How long a held notification still goes out. A day-old lead is already on the Website Leads
    // list the adviser checks; an email about it then would only arrive out of order.
    public static readonly TimeSpan HoldFor = TimeSpan.FromDays(1);

    // The row's NotificationError while the email waits. HeldLeadNotificationJob finds held rows by this
    // exact text, so change it only together with a step that rewrites the rows already holding it.
    public static readonly string HeldMessage =
        "Not emailed yet: lead notifications are limited to a few an hour, and this hour's are used. The lead is saved here, and the email goes out as soon as there is room.";

    // 493's wording, on any row held before 632 went live: the job sends those too.
    public static readonly string HeldMessageBefore632 =
        $"Not emailed: this website has already sent {PerWebsitePerHour} lead notifications in the last hour. The lead is saved here; notifications resume within the hour.";

    public static readonly string ExpiredMessage =
        "Not emailed: lead notifications were at their limit for a day. The lead is saved here.";

    public static bool IsHeld(string? notificationError) =>
        notificationError == HeldMessage || notificationError == HeldMessageBefore632;

    // The row while HeldLeadNotificationJob is sending its email (its claim).
    public const string SendingNow = "Sending the held notification now.";

    // Held or being sent: Website Leads says the email is on its way rather than that it failed.
    public static bool IsWaiting(string? notificationError) => IsHeld(notificationError) || notificationError == SendingNow;

    public static async Task<bool> IsHeldAsync(IPRODbContext db, int websiteId, DateTime now)
    {
        var since = now.AddHours(-1);
        var sent = await db.WebsiteLeads.CountAsync(l => l.AgentWebsiteId == websiteId && l.NotificationSent
                                                         && (l.NotificationSentAt ?? l.CreatedAt) >= since);
        return sent >= PerWebsitePerHour;
    }

    // 632 H-2: every website's notifications together.
    public static async Task<bool> PlatformFullAsync(IPRODbContext db, DateTime now)
    {
        var since = now.AddHours(-1);
        var sent = await db.WebsiteLeads.CountAsync(l => l.NotificationSent && (l.NotificationSentAt ?? l.CreatedAt) >= since);
        return sent >= PlatformPerHour;
    }

    // 632 H-2 (audit D-3): may this website send one more past its own limit's check? Its first
    // AlwaysPerWebsitePerHour an hour always may, whatever other sites do; after that only while all
    // websites together are under PlatformPerHour. Asked after IsHeldAsync, by the form and the later job.
    public static async Task<bool> WithinSharedLimitAsync(IPRODbContext db, int websiteId, DateTime now)
    {
        var since = now.AddHours(-1);
        var sent = await db.WebsiteLeads.CountAsync(l => l.AgentWebsiteId == websiteId && l.NotificationSent
                                                         && (l.NotificationSentAt ?? l.CreatedAt) >= since);
        return sent < AlwaysPerWebsitePerHour || !await PlatformFullAsync(db, now);
    }

    // The email itself, one wording for the form's inline send and the later one.
    public static string Subject(WebsiteLead lead) => $"New website lead: {lead.FirstName} {lead.LastName}".Trim();

    public static string Html(WebsiteLead lead, string portalBaseUrl)
    {
        var name = WebUtility.HtmlEncode($"{lead.FirstName} {lead.LastName}".Trim());
        var type = lead.SubmissionType == WebsiteLeadTypes.Newsletter ? "newsletter signup" : "website inquiry";
        return $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              <div style="padding:22px;background:#1457d9;color:white"><h1 style="margin:0;font-size:24px">New IPRO website lead</h1></div>
              <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                <p>You received a new <strong>{type}</strong> from <strong>{name}</strong>.</p>
                <p><strong>Email:</strong> {WebUtility.HtmlEncode(lead.Email)}<br><strong>Phone:</strong> {WebUtility.HtmlEncode(lead.Phone)}<br><strong>Source:</strong> {WebUtility.HtmlEncode(lead.SourceDomain + lead.SourcePage)}</p>
                <p>{WebUtility.HtmlEncode(lead.Message)}</p>
                <p><a href="{portalBaseUrl}/WebsiteLeads" style="display:inline-block;padding:11px 18px;background:#1457d9;color:white;text-decoration:none;border-radius:6px">Open Website Leads</a></p>
              </div>
            </div>
            """;
    }
}
