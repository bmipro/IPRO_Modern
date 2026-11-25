using System.Net;
using Hangfire;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

// 632 B-1 (2026-11-24): a newsletter, e-card, e-letter or poll that the email provider keeps refusing for
// a lasting reason (a bad key, a suspended account) pauses and resumes every minute, forever, and until
// now nobody was told -- the dispatchers only logged a warning. Deliberate (the audit of 2026-09-17): a
// send that waits and resumes is better than one retired as Failed, which invites a manual resend and
// duplicates. What was missing is the alert.
//
// Every hour this looks for sends that are due and have sent NOTHING for over an hour while recipients
// still wait (or none were ever built), from advisers whose mail may go (SendingAdvisers: a suspended or
// lapsed adviser's mail is held on purpose and is not stuck). A send the provider's hourly limit is
// spreading out still sends every hour, so it is never listed. iPro's mailbox gets one email when a send
// first counts as stuck, and one a day (13:00 UTC, the morning in Canada) while any still is. The email
// goes by the usual provider and, if that one refuses, by the other (Amazon SES's notify stream), so the
// alert does not depend on the provider that may be the problem. When it is due the job also fails, so
// the Job Scheduler page shows a red row even if no email could go.
[AutomaticRetry(Attempts = 0)]
public class StuckSendWatchJob
{
    public static readonly TimeSpan QuietFor = TimeSpan.FromHours(1);
    public const int DailyReminderHourUtc = 13;

    public sealed record StuckSend(string Kind, int Id, int AgentUserId, DateTime ScheduledAt, DateTime? LastSentAt, int Sent, int Waiting)
    {
        public DateTime StuckSince => LastSentAt is { } last && last > ScheduledAt ? last : ScheduledAt;
    }

    private readonly IPRODbContext _db;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StuckSendWatchJob> _logger;

    public StuckSendWatchJob(IPRODbContext db, IEmailService email, IConfiguration configuration, ILogger<StuckSendWatchJob> logger)
    {
        _db = db; _email = email; _configuration = configuration; _logger = logger;
    }

    public async Task RunAsync()
    {
        var now = DateTime.UtcNow;
        var stuck = await FindAsync(_db, now);
        if (stuck.Count == 0) return;

        // New this hour (the job runs hourly, so each send is "new" in exactly one run), or the daily reminder.
        var isNew = stuck.Any(s => s.StuckSince > now - QuietFor - TimeSpan.FromHours(1));
        if (!isNew && now.Hour != DailyReminderHourUtc)
        {
            _logger.LogWarning("{Count} scheduled send(s) still have sent nothing for over an hour; the daily email repeats at {Hour}:00 UTC.", stuck.Count, DailyReminderHourUtc);
            return;
        }

        var adviserIds = stuck.Select(s => s.AgentUserId).Distinct().ToList();
        var advisers = await _db.AgentUsers.AsNoTracking()
            .Where(a => adviserIds.Contains(a.Id))
            .Select(a => new { a.Id, a.UserName, a.CompanyName, a.FirstName, a.LastName })
            .ToListAsync();
        var names = advisers.ToDictionary(a => a.Id, a =>
            $"{(string.IsNullOrWhiteSpace(a.CompanyName) ? $"{a.FirstName} {a.LastName}".Trim() : a.CompanyName.Trim())} ({a.UserName})");

        var to = SignupNotice.Recipient(_configuration);
        var html = AlertHtml(stuck, names, id => SignupNotice.AdminLink(_configuration, id), now);
        var subject = stuck.Count == 1 ? "A scheduled send has stopped sending" : $"{stuck.Count} scheduled sends have stopped sending";
        var how = string.IsNullOrWhiteSpace(to) ? "no mailbox is set (Signups:NotificationEmail, Support:NotificationEmail)" : await SendAsync(to, subject, html);

        _logger.LogError("{Count} scheduled send(s) have sent nothing for over an hour ({Kinds}); alert email: {How}.",
            stuck.Count, string.Join(", ", stuck.Select(s => $"{s.Kind} {s.Id}")), how);
        throw new InvalidOperationException(
            $"{stuck.Count} scheduled send(s) have sent nothing for over an hour: {string.Join(", ", stuck.Select(s => $"{s.Kind} {s.Id}"))}. Alert email: {how}.");
    }

    // The usual provider first; if it refuses, Amazon SES's notify stream (RoutingEmailService sends a
    // message tagged with that stream by SES whenever SES is set up). Says which one carried it.
    private async Task<string> SendAsync(string to, string subject, string html)
    {
        try
        {
            var first = await _email.SendDetailedAsync(to, "iPro", subject, html);
            if (first.Success) return $"sent to {to}";
            _logger.LogWarning("The stuck-send alert was refused by the usual provider ({Message}); trying Amazon SES.", first.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The stuck-send alert could not go by the usual provider; trying Amazon SES.");
        }

        try
        {
            var tags = new Dictionary<string, string> { [AdviserSender.StreamTag] = EmailStreams.Notify };
            var second = await _email.SendDetailedAsync(to, "iPro", subject, html, customArgs: tags);
            return second.Success ? $"sent to {to} by the other provider" : $"refused by both providers ({second.Message})";
        }
        catch (Exception ex)
        {
            return $"could not be sent ({ex.Message})";
        }
    }

    // Due, still scheduled or sending, nothing sent for QuietFor, recipients waiting (or never built), and
    // the adviser's mail may go. Public and static so a test runs it against a real database.
    public static async Task<List<StuckSend>> FindAsync(IPRODbContext db, DateTime now)
    {
        var quietSince = now - QuietFor;
        var senders = await SendingAdvisers.WhoMaySendAsync(db, now);
        var found = new List<StuckSend>();

        found.AddRange((await db.NewsLetterSends.AsNoTracking()
                .Where(s => (s.Status == NewsLetterSendStatus.Scheduled || s.Status == NewsLetterSendStatus.Sending)
                            && s.ScheduledAt <= quietSince && senders.Contains(s.AgentUserId))
                .Select(s => new
                {
                    s.Id, s.AgentUserId, s.ScheduledAt,
                    Last = db.NewsLetterRecipients.Where(r => r.NewsLetterSendId == s.Id).Max(r => r.SentAt),
                    Sent = db.NewsLetterRecipients.Count(r => r.NewsLetterSendId == s.Id && r.SentAt != null),
                    Waiting = db.NewsLetterRecipients.Count(r => r.NewsLetterSendId == s.Id && r.Status == NewsLetterRecipientStatus.Queued),
                    Rows = db.NewsLetterRecipients.Count(r => r.NewsLetterSendId == s.Id)
                }).ToListAsync())
            .Where(s => s.Waiting > 0 || s.Rows == 0)
            .Select(s => new StuckSend("Newsletter", s.Id, s.AgentUserId, s.ScheduledAt, s.Last, s.Sent, s.Waiting)));

        found.AddRange((await db.ECards.AsNoTracking()
                .Where(c => (c.Status == ECardStatuses.Scheduled || c.Status == ECardStatuses.Sending)
                            && c.ScheduledAt <= quietSince && senders.Contains(c.AgentUserId))
                .Select(c => new
                {
                    c.Id, c.AgentUserId, c.ScheduledAt,
                    Last = db.ECardRecipients.Where(r => r.ECardId == c.Id).Max(r => r.SentAt),
                    Sent = db.ECardRecipients.Count(r => r.ECardId == c.Id && r.SentAt != null),
                    Waiting = db.ECardRecipients.Count(r => r.ECardId == c.Id && r.Status == ECardRecipientStatuses.Queued)
                }).ToListAsync())
            .Where(c => c.Waiting > 0)
            .Select(c => new StuckSend("E-card", c.Id, c.AgentUserId, c.ScheduledAt, c.Last, c.Sent, c.Waiting)));

        found.AddRange((await db.ELetters.AsNoTracking()
                .Where(l => (l.Status == ELetterStatuses.Scheduled || l.Status == ELetterStatuses.Sending)
                            && l.ScheduledAt <= quietSince && senders.Contains(l.AgentUserId))
                .Select(l => new
                {
                    l.Id, l.AgentUserId, l.ScheduledAt,
                    Last = db.ELetterRecipients.Where(r => r.ELetterId == l.Id).Max(r => r.SentAt),
                    Sent = db.ELetterRecipients.Count(r => r.ELetterId == l.Id && r.SentAt != null),
                    Waiting = db.ELetterRecipients.Count(r => r.ELetterId == l.Id && r.Status == ELetterRecipientStatuses.Queued)
                }).ToListAsync())
            .Where(l => l.Waiting > 0)
            .Select(l => new StuckSend("E-letter", l.Id, l.AgentUserId, l.ScheduledAt, l.Last, l.Sent, l.Waiting)));

        found.AddRange((await db.PollSends.AsNoTracking()
                .Where(p => (p.Status == PollSendStatus.Scheduled || p.Status == PollSendStatus.Sending)
                            && p.ScheduledAt <= quietSince && senders.Contains(p.AgentUserId))
                .Select(p => new
                {
                    p.Id, p.AgentUserId, p.ScheduledAt,
                    Last = db.PollRecipients.Where(r => r.PollSendId == p.Id).Max(r => r.SentAt),
                    Sent = db.PollRecipients.Count(r => r.PollSendId == p.Id && r.SentAt != null),
                    Waiting = db.PollRecipients.Count(r => r.PollSendId == p.Id && r.Status == PollRecipientStatus.Queued),
                    Rows = db.PollRecipients.Count(r => r.PollSendId == p.Id)
                }).ToListAsync())
            .Where(p => p.Waiting > 0 || p.Rows == 0)
            .Select(p => new StuckSend("Poll", p.Id, p.AgentUserId, p.ScheduledAt, p.Last, p.Sent, p.Waiting)));

        return found.Where(s => s.StuckSince <= quietSince).OrderBy(s => s.StuckSince).ToList();
    }

    // The email's body. Static, so a test reads it without a network.
    public static string AlertHtml(IReadOnlyCollection<StuckSend> stuck, IReadOnlyDictionary<int, string> advisers, Func<int, string> adminLink, DateTime now)
    {
        var rows = string.Concat(stuck.Select(s =>
        {
            var who = WebUtility.HtmlEncode(advisers.TryGetValue(s.AgentUserId, out var name) ? name : $"adviser {s.AgentUserId}");
            var quiet = now - s.StuckSince;
            var since = quiet.TotalHours >= 48 ? $"{Math.Floor(quiet.TotalDays):0} days" : $"{Math.Floor(quiet.TotalHours):0} hour{(Math.Floor(quiet.TotalHours) == 1 ? "" : "s")}";
            return $"<li style=\"margin-bottom:8px\"><strong>{s.Kind} {s.Id}</strong> from <a href=\"{adminLink(s.AgentUserId)}\">{who}</a>: " +
                   $"{s.Sent} sent, {s.Waiting} waiting, nothing sent for {since} (due {s.ScheduledAt:yyyy-MM-dd HH:mm} UTC).</li>";
        }));
        return $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              <div style="padding:22px;background:#8a1c1c;color:white"><h1 style="margin:0;font-size:22px">Scheduled mail has stopped sending</h1></div>
              <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                <p>These sends are due but have not sent anything for over an hour:</p>
                <ul>{rows}</ul>
                <p>Usually the email provider is refusing every message for a lasting reason: a wrong or expired key, or the sending account paused. The provider's hourly limit alone never stops a send for a whole hour.</p>
                <p>Nothing is lost. The waiting recipients stay queued and go out as soon as the provider accepts mail again; nobody gets a second copy. The reason the provider gave is in the app's log.</p>
                <p style="color:#5b6b7a;font-size:13px">You get this once when a send stops, then once a day while any is still stuck.</p>
              </div>
            </div>
            """;
    }
}
