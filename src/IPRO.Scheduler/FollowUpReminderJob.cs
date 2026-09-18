using System.Globalization;
using System.Net;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

// 498 (2026-09-18): the "Email reminder" the plan table always promised.
//
// The row was ticked on every plan, but its only job (CalendarReminderJob) read a table no page
// wrote and was removed on 2026-09-09; the truth sweep of 2026-09-18 found the row selling nothing
// and 496 withdrew it. The owner's answer the same afternoon: build the real thing, now. This is it:
// one email in the adviser's own morning listing the follow-ups due today and the ones overdue, with
// a link to the follow-up list.
//
// WHEN IT GOES. Between 7 a.m. and noon in the adviser's time zone, once a day, and only when
// something is due today or fell due within the last week. An item nobody ever ticks off is
// reminded for a week and then left alone -- it is still LISTED whenever a mail goes, it just stops
// causing one -- so an account carrying years of imported, never-completed follow-ups is not mailed
// every morning for ever. After noon nothing goes: a "good morning" at five in the afternoon (the
// first pass after a deploy, or a long deferral) is worse than no mail.
//
// WHY HOURLY. "Morning" is a different UTC hour in each of the six Canadian time zones; an hourly
// pass is its own retry when the send gate defers (the subscription is capped at 100 sends an hour --
// see EmailSendGate); and the mails are tagged as bulk-class, so a morning's reminders queue BEHIND
// the transactional reserve and can never crowd out a password reset or a lead notification.
//
// ONCE A DAY. The adviser's local day is CLAIMED with a conditional UPDATE before the mail is sent,
// so two overlapping passes cannot both mail them, and the claim is handed back when the send was
// only "not right now".
//
// WHAT "DUE TODAY" MEANS. ClientFollowUp.DueAt is the adviser's OWN calendar date, stored exactly
// as typed: the follow-up form posts a date with no time, the appointment scheduler a local date
// and time, and nothing converts either to UTC (the follow-up list compares DueAt.Date with today).
// So it is compared here with the adviser's LOCAL date and never run through a time-zone
// conversion: read as a UTC instant, "due 21 September" is 20 September at 8 p.m. in Toronto, and
// an item due today would be mailed as overdue. The time zone decides only WHEN the adviser's
// morning is and WHICH date is their today.
//
// TWO SWITCHES THAT NEED NO DEPLOY (App Service settings, read on every pass):
//   FollowUpReminders__Enabled=false      the job does nothing at all
//   FollowUpReminders__NotBefore=<date>   no mail while the adviser's local date is earlier
// A NotBefore that cannot be read holds the mail instead of sending it: whoever typed it meant
// to wait. Held is not "decided" -- no day is claimed, so lifting the hold in the morning still
// sends that morning's mail.
//
// Backed by a real check, unlike the row it replaces: PackageFeatureCodes.EmailReminder is read
// here, lapsed and unpaid accounts (IsAccessGatedAsync) get no mail, and the adviser can turn it off
// on their profile (AgentFollowUpReminder.IsEnabled; no row means "on").
public class FollowUpReminderJob
{
    public const int SendFromLocalHour = 7;
    public const int SendUntilLocalHour = 12;
    public const int RemindForDays = 7;
    public const int MaxListed = 15;
    public const string BulkEntity = "follow_up_reminder";

    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FollowUpReminderJob> _logger;

    public FollowUpReminderJob(IPRODbContext db, IPackageEntitlementService entitlements, IEmailService email, IConfiguration configuration, ILogger<FollowUpReminderJob> logger)
    {
        _db = db;
        _entitlements = entitlements;
        _email = email;
        _configuration = configuration;
        _logger = logger;
    }

    // The tests drive the clock; production is DateTime.UtcNow.
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    private sealed record Candidate(int Id, string Email, string FirstName, string LastName, string TimeZone, DateTime? LastDecidedOn);
    private sealed record Item(string Title, DateTime DueAt, string ClientFirstName, string ClientLastName);

    public async Task RunAsync()
    {
        var nowUtc = Clock();

        if (!_configuration.GetValue("FollowUpReminders:Enabled", true)) return;
        DateTime? notBefore = null;
        var notBeforeSetting = _configuration["FollowUpReminders:NotBefore"];
        if (!string.IsNullOrWhiteSpace(notBeforeSetting))
        {
            if (!DateTime.TryParse(notBeforeSetting, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                _logger.LogWarning("FollowUpReminders:NotBefore is set to '{Value}', which is not a date (expected yyyy-MM-dd); follow-up reminders are HELD until it is fixed or removed.", notBeforeSetting);
                return;
            }
            notBefore = parsed.Date;
        }

        var candidates = await (
            from a in _db.AgentUsers.AsNoTracking()
            where a.IsActive && a.Email != ""
            join r in _db.AgentFollowUpReminders.AsNoTracking() on a.Id equals r.AgentUserId into preferences
            from r in preferences.DefaultIfEmpty()
            where r == null || r.IsEnabled
            select new Candidate(a.Id, a.Email, a.FirstName, a.LastName, a.TimeZone, r == null ? (DateTime?)null : r.LastDecidedOn)
        ).ToListAsync();

        // Whose morning it is, and who has not had today's decision made yet.
        var due = candidates
            .Select(c => (Agent: c, Local: AgentLocalTime.FromUtc(nowUtc, c.TimeZone)))
            .Where(x => x.Local.Hour >= SendFromLocalHour && x.Local.Hour < SendUntilLocalHour
                     && (notBefore == null || x.Local.Date >= notBefore.Value)
                     && (x.Agent.LastDecidedOn == null || x.Agent.LastDecidedOn.Value.Date < x.Local.Date))
            .ToList();
        if (due.Count == 0) return;

        var access = await _entitlements.HasAccessBulkAsync(due.Select(x => x.Agent.Id), PackageFeatureCodes.EmailReminder);

        foreach (var (agent, local) in due)
        {
            try
            {
                if (!access.TryGetValue(agent.Id, out var hasAccess) || !hasAccess) continue;
                if (await _entitlements.IsAccessGatedAsync(agent.Id)) continue;

                // Claim the adviser's day. A row first if there is none (no row means "wants it, never
                // decided"); then whichever pass flips the date owns today's decision, and an
                // overlapping pass -- or an adviser who switched it off a second ago -- matches nothing.
                var today = local.Date;
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT IGNORE INTO `AgentFollowUpReminders` (`AgentUserId`, `IsEnabled`, `UpdatedAt`) VALUES ({agent.Id}, 1, {nowUtc})");
                var claimed = await _db.AgentFollowUpReminders
                    .Where(r => r.AgentUserId == agent.Id && r.IsEnabled && (r.LastDecidedOn == null || r.LastDecidedOn < today))
                    .ExecuteUpdateAsync(u => u.SetProperty(r => r.LastDecidedOn, (DateTime?)today));
                if (claimed == 0) continue;

                // DueAt is the adviser's own calendar date -- see the note at the top. No conversion.
                var tomorrow = today.AddDays(1);
                var remindFrom = today.AddDays(-RemindForDays);
                var open = _db.ClientFollowUps.AsNoTracking()
                    .Where(f => f.Client.AgentUserId == agent.Id && !f.IsCompleted && f.DueAt < tomorrow);

                // Nothing due today and nothing newly overdue: today is decided, and no mail goes.
                if (!await open.AnyAsync(f => f.DueAt >= remindFrom)) continue;

                var dueToday = await open.CountAsync(f => f.DueAt >= today);
                var overdue = await open.CountAsync(f => f.DueAt < today);
                var todayItems = await open.Where(f => f.DueAt >= today)
                    .OrderBy(f => f.DueAt).Take(MaxListed)
                    .Select(f => new Item(f.Title, f.DueAt, f.Client.FirstName, f.Client.LastName))
                    .ToListAsync();
                // The most recently overdue first: the week-old item matters more than the one from 2019.
                var overdueItems = todayItems.Count >= MaxListed
                    ? new List<Item>()
                    : await open.Where(f => f.DueAt < today)
                        .OrderByDescending(f => f.DueAt).Take(MaxListed - todayItems.Count)
                        .Select(f => new Item(f.Title, f.DueAt, f.Client.FirstName, f.Client.LastName))
                        .ToListAsync();

                var name = $"{agent.FirstName} {agent.LastName}".Trim();
                var result = await _email.SendDetailedAsync(
                    agent.Email,
                    string.IsNullOrWhiteSpace(name) ? agent.Email : name,
                    Subject(dueToday, overdue),
                    BuildHtml(agent, todayItems, overdueItems, dueToday, overdue),
                    BuildText(agent, todayItems, overdueItems, dueToday, overdue),
                    customArgs: new Dictionary<string, string> { ["ipro_entity"] = BulkEntity });
                if (result.Success) continue;

                if (result.IsTransient)
                {
                    // "Not right now" is not today's answer: hand the day back so the next hourly pass
                    // tries again. Guarded on the value this pass wrote.
                    await _db.AgentFollowUpReminders
                        .Where(r => r.AgentUserId == agent.Id && r.LastDecidedOn == today)
                        .ExecuteUpdateAsync(u => u.SetProperty(r => r.LastDecidedOn, agent.LastDecidedOn));

                    if (result.IsDeferred)
                    {
                        // No send slot inside the gate's bound: everyone behind this adviser would get
                        // the same answer. The next pass is an hour away and picks them all up.
                        _logger.LogInformation("Follow-up reminders paused at agent {AgentId}: {Reason}", agent.Id, result.Message);
                        break;
                    }
                    _logger.LogWarning("Follow-up reminder for agent {AgentId} was not sent and will be retried next hour: {Reason}", agent.Id, result.Message);
                }
                else
                {
                    _logger.LogError("Follow-up reminder for agent {AgentId} was rejected and will not be retried today: {Reason}", agent.Id, result.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Follow-up reminder failed for agent {AgentId}", agent.Id);
            }
        }
    }

    public static string Subject(int dueToday, int overdue)
    {
        static string Count(int n) => n == 1 ? "1 follow-up" : $"{n} follow-ups";
        if (overdue == 0) return $"{Count(dueToday)} due today";
        if (dueToday == 0) return $"{Count(overdue)} overdue";
        return $"{Count(dueToday)} due today, {overdue} overdue";
    }

    // The list is attribute-routed at Clients/FollowUps (the ACTION is called FollowUpQueue, and
    // /Clients/FollowUpQueue is a 404); under /portal it is the portal on every host, and the sign-in
    // page keeps it as the return address.
    private string FollowUpListUrl() =>
        $"{IPRO.Utility.WebAppUrlHelper.GetWebAppBaseUrl(_configuration).TrimEnd('/')}{IPRO.Utility.PortalPaths.To("/Clients/FollowUps?status=open")}";

    private string ProfileUrl() =>
        $"{IPRO.Utility.WebAppUrlHelper.GetWebAppBaseUrl(_configuration).TrimEnd('/')}/Account/Profile";

    // Invariant culture on purpose: en-CA prints "p.m." under some ICU versions and "PM" under others.
    // An appointment keeps the time it was booked for; a date typed without a time has none to show.
    private static string TimeToday(Item item) =>
        item.DueAt.TimeOfDay == TimeSpan.Zero ? "today" : item.DueAt.ToString("h:mm tt", CultureInfo.InvariantCulture);

    private static string DueSince(Item item) =>
        "since " + item.DueAt.ToString("MMM d", CultureInfo.InvariantCulture);

    private static string Summary(int dueToday, int overdue)
    {
        static string Count(int n) => n == 1 ? "1 follow-up" : $"{n} follow-ups";
        if (overdue == 0) return $"You have {Count(dueToday)} due today.";
        if (dueToday == 0) return $"You have {Count(overdue)} overdue.";
        return $"You have {Count(dueToday)} due today and {overdue} overdue.";
    }

    private string BuildHtml(Candidate agent, List<Item> todayItems, List<Item> overdueItems, int dueToday, int overdue)
    {
        string Rows(IEnumerable<Item> items, bool late) => string.Concat(items.Select(item =>
        {
            var client = WebUtility.HtmlEncode($"{item.ClientFirstName} {item.ClientLastName}".Trim());
            var when = WebUtility.HtmlEncode(late ? DueSince(item) : TimeToday(item));
            return $"""
                <tr>
                  <td style="padding:10px 12px;border-bottom:1px solid #e6ecf5"><strong>{client}</strong><br><span style="color:#475569">{WebUtility.HtmlEncode(item.Title)}</span></td>
                  <td style="padding:10px 12px;border-bottom:1px solid #e6ecf5;white-space:nowrap;text-align:right;color:{(late ? "#b42318" : "#17223a")}">{when}</td>
                </tr>
                """;
        }));

        string Section(string heading, List<Item> items, int total, bool late)
        {
            if (total == 0) return string.Empty;
            var more = total > items.Count
                ? $"<p style=\"color:#475569;margin:8px 0 0\">...and {total - items.Count} more on your list.</p>"
                : string.Empty;
            var table = items.Count == 0 ? string.Empty : $"<table style=\"width:100%;border-collapse:collapse\">{Rows(items, late)}</table>";
            return $"""
                <h2 style="font-size:15px;margin:22px 0 6px;color:{(late ? "#b42318" : "#17223a")}">{heading} ({total})</h2>
                {table}
                {more}
                """;
        }

        var firstName = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(agent.FirstName) ? "there" : agent.FirstName);
        return $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              <div style="padding:22px;background:#1457d9;color:white"><h1 style="margin:0;font-size:22px">Your follow-ups for today</h1></div>
              <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                <p style="margin-top:0">Good morning {firstName}. {Summary(dueToday, overdue)}</p>
                {Section("Due today", todayItems, dueToday, late: false)}
                {Section("Overdue", overdueItems, overdue, late: true)}
                <p style="margin-top:24px"><a href="{FollowUpListUrl()}" style="display:inline-block;padding:11px 18px;background:#1457d9;color:white;text-decoration:none;border-radius:6px">Open your follow-up list</a></p>
                <p style="color:#64748b;font-size:13px;margin-top:24px">This email goes out on mornings when a follow-up is due or newly overdue. To stop it, untick "Email me my follow-ups each morning" on <a href="{ProfileUrl()}" style="color:#1457d9">your profile</a>.</p>
              </div>
            </div>
            """;
    }

    private string BuildText(Candidate agent, List<Item> todayItems, List<Item> overdueItems, int dueToday, int overdue)
    {
        var lines = new List<string> { Summary(dueToday, overdue) };
        if (dueToday > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"DUE TODAY ({dueToday})");
            lines.AddRange(todayItems.Select(i => $"- {i.ClientFirstName} {i.ClientLastName}: {i.Title} ({TimeToday(i)})"));
            if (dueToday > todayItems.Count) lines.Add($"...and {dueToday - todayItems.Count} more.");
        }
        if (overdue > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"OVERDUE ({overdue})");
            lines.AddRange(overdueItems.Select(i => $"- {i.ClientFirstName} {i.ClientLastName}: {i.Title} ({DueSince(i)})"));
            if (overdue > overdueItems.Count) lines.Add($"...and {overdue - overdueItems.Count} more.");
        }
        lines.Add(string.Empty);
        lines.Add($"Open your follow-up list: {FollowUpListUrl()}");
        lines.Add($"To stop this email, untick it on your profile: {ProfileUrl()}");
        return string.Join("\n", lines);
    }
}
