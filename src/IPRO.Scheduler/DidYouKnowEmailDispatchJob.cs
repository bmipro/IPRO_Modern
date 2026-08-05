using IPRO.DataAccess;
using IPRO.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

// Each Did You Know submission queues one row per selected article (PublicWebsiteController),
// staggered several minutes apart so a visitor never receives 4-6 emails in the same instant --
// a burst like that is itself a spam signal to mail providers. This job just drains whatever
// is due each minute, one article per email, full content inline (no external links needed).
public class DidYouKnowEmailDispatchJob
{
    private readonly IPRODbContext _db;
    private readonly IEmailService _email;
    private readonly ILogger<DidYouKnowEmailDispatchJob> _logger;

    public DidYouKnowEmailDispatchJob(IPRODbContext db, IEmailService email, ILogger<DidYouKnowEmailDispatchJob> logger)
    {
        _db = db;
        _email = email;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        // AsNoTracking, and every write below goes through ExecuteUpdateAsync: this job deliberately
        // does not mix the EF change tracker with direct updates on the same rows.
        var due = await _db.DidYouKnowEmailQueueItems
            .AsNoTracking()
            .Where(q => q.SentAtUtc == null && q.ScheduledForUtc <= DateTime.UtcNow)
            .OrderBy(q => q.ScheduledForUtc)
            .Take(100)
            .ToListAsync();

        foreach (var item in due)
        {
            try
            {
                // CLAIM BEFORE SENDING (2026-08-05 audit, Critical)
                //
                // This used to set item.SentAtUtc in memory and persist every marker in a single
                // SaveChangesAsync AFTER the loop. The job runs Cron.Minutely and Hangfire does not
                // skip a tick while the previous run is still executing, so any run lasting longer
                // than a minute -- 100 items x (3 queries + a SendGrid round-trip) easily does --
                // had its rows re-selected and re-sent by the next run. A failure on that final save
                // was worse: nothing was marked, and Hangfire's default 10 retries re-sent the entire
                // batch each time.
                //
                // The conditional UPDATE is the fix: whichever run flips SentAtUtc from NULL first
                // owns the item, and every other run gets 0 rows and skips it. MySQL settles the
                // race, not application timing.
                //
                // This is deliberately at-most-once. A crash between the claim and the send loses
                // that one email. The alternative -- send first, mark after -- is at-least-once, and
                // for unsolicited-looking marketing mail a duplicate is far more damaging than a
                // miss: it draws spam complaints and hurts the domain reputation this system depends
                // on. A missed article is invisible; a doubled one is not.
                var claimed = await _db.DidYouKnowEmailQueueItems
                    .Where(q => q.Id == item.Id && q.SentAtUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(q => q.SentAtUtc, DateTime.UtcNow));

                if (claimed == 0)
                {
                    // Another run (or an overlapping retry) already owns this one.
                    continue;
                }

                var article = await _db.Articles.AsNoTracking().FirstOrDefaultAsync(a => a.Id == item.ArticleId && a.IsPublished);
                var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == item.ClientId);
                if (article == null || client == null || string.IsNullOrWhiteSpace(client.Email))
                {
                    // Nothing sendable (article unpublished/deleted, client gone) -- already claimed
                    // above, so it is dropped rather than retried forever. Same intent as before.
                    continue;
                }

                var agent = await _db.AgentUsers.FirstOrDefaultAsync(u => u.Id == article.AgentUserId);
                var companyName = agent == null || string.IsNullOrWhiteSpace(agent.CompanyName)
                    ? $"{agent?.FirstName} {agent?.LastName}".Trim()
                    : agent.CompanyName;

                var html = $"""
                    <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
                      <div style="padding:22px;background:#1457d9;color:white"><h1 style="margin:0;font-size:22px">Did You Know?</h1></div>
                      <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                        <h2 style="margin:0 0 10px;font-size:20px;color:#0f172a;">{System.Net.WebUtility.HtmlEncode(article.Title)}</h2>
                        <div style="font-family:Arial,sans-serif;font-size:15px;line-height:1.6;color:#334155;">{article.Content}</div>
                      </div>
                    </div>
                    """;

                var clientName = $"{client.FirstName} {client.LastName}".Trim();
                var result = await _email.SendDetailedAsync(
                    client.Email,
                    string.IsNullOrWhiteSpace(clientName) ? client.Email : clientName,
                    article.Title,
                    html,
                    replyToEmail: agent?.Email,
                    replyToName: companyName);

                // SendGridEmailService catches everything and RETURNS a failure rather than throwing,
                // so the catch below never sees a rejected send. The result was previously discarded
                // entirely, which meant a bad API key or a rate-limit rejection still looked like a
                // delivered email. The item stays claimed either way -- this is about the log telling
                // the truth, not about retrying.
                if (!result.Success)
                {
                    _logger.LogError(
                        "Did You Know queued email {ItemId} (article {ArticleId}) was rejected and will NOT be retried: {Error}",
                        item.Id, item.ArticleId, result.Message);
                }
            }
            catch (Exception ex)
            {
                // Per-item isolation: one bad row must not stop the rest of the batch.
                _logger.LogError(ex, "Did You Know queued email {ItemId} (article {ArticleId}) failed", item.Id, item.ArticleId);
            }
        }
    }
}
