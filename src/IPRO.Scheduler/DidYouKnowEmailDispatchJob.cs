using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
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
        var now = DateTime.UtcNow;
        // A claim older than this is assumed dead -- the process that took it was killed (deploy,
        // crash, worker recycle) between claiming and sending. Generous enough that a slow SendGrid
        // call can never be mistaken for a dead claim.
        var staleClaimCutoff = now.AddMinutes(-15);

        var due = await _db.DidYouKnowEmailQueueItems
            .AsNoTracking()
            .Where(q => q.SentAtUtc == null
                     && q.ScheduledForUtc <= now
                     && (q.ClaimedAtUtc == null || q.ClaimedAtUtc < staleClaimCutoff))
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
                // CLAIMING IS NOT THE SAME AS COMPLETING (revised 2026-08-06)
                //
                // The first version of this used SentAtUtc itself as the claim, which made the job
                // at-most-once: a crash or an ordinary deploy restart between the claim and the send
                // destroyed that email permanently, with no retry and no record. That is a poor trade
                // for a system that deploys several times a day, and it is suspected of eating part
                // of a real test run on 2026-08-05.
                //
                // ClaimedAtUtc is now a separate marker. Concurrent runs still cannot both take the
                // same item -- MySQL settles that -- but an item claimed and never confirmed sent
                // becomes eligible again after the stale cutoff, so a killed process loses nothing.
                // The only duplicate risk left is a send that genuinely succeeded but whose
                // confirmation never got written, which needs a crash inside a specific sub-second
                // window; a bounded, rare duplicate is a much better failure than a silent loss.
                var claimed = await _db.DidYouKnowEmailQueueItems
                    .Where(q => q.Id == item.Id
                             && q.SentAtUtc == null
                             && (q.ClaimedAtUtc == null || q.ClaimedAtUtc < staleClaimCutoff))
                    .ExecuteUpdateAsync(s => s.SetProperty(q => q.ClaimedAtUtc, DateTime.UtcNow));

                if (claimed == 0)
                {
                    // Another run (or an overlapping retry) already owns this one.
                    continue;
                }

                var article = await _db.Articles.AsNoTracking().FirstOrDefaultAsync(a => a.Id == item.ArticleId && a.IsPublished);
                var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == item.ClientId);
                if (article == null || client == null || string.IsNullOrWhiteSpace(client.Email))
                {
                    // Nothing sendable (article unpublished/deleted, client gone). This is a DEFINITIVE
                    // outcome, so retire the item properly instead of leaving it claimed -- otherwise
                    // the stale-claim sweep would pick it up again every 15 minutes forever.
                    await MarkRetiredAsync(item.Id, EmailSendResult.Failed(
                        "Article was unpublished or deleted, or the client no longer has an email address."));
                    _logger.LogInformation(
                        "Did You Know queued email {ItemId} dropped: article {ArticleId} or client {ClientId} is missing/unpublished.",
                        item.Id, item.ArticleId, item.ClientId);
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
                    // Tagging so the SendGrid event webhook can attribute delivered/open/click/bounce
                    // back to this queue item. Every other sender in the system already did this;
                    // Did You Know was the one that did not, which made its mail invisible on the
                    // Email Activity screen.
                    customArgs: new Dictionary<string, string>
                    {
                        ["ipro_entity"] = "didyouknow",
                        ["dyk_queue_item_id"] = item.Id.ToString(),
                        ["article_id"] = article.Id.ToString(),
                        ["client_id"] = client.Id.ToString(),
                        ["agent_user_id"] = client.AgentUserId.ToString()
                    },
                    replyToEmail: agent?.Email,
                    replyToName: companyName);

                // SendGridEmailService catches everything and RETURNS a failure rather than throwing,
                // so the catch below never sees a rejected send. The result was previously discarded
                // entirely, which meant a bad API key or a rate-limit rejection still looked like a
                // delivered email.
                //
                // Either way this is a DEFINITIVE outcome -- SendGrid answered -- so the item is
                // retired. A rejection is usually permanent (bad address), and retrying it every 15
                // minutes forever would be worse than dropping it loudly.
                await MarkRetiredAsync(item.Id, result);

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
                //
                // Note what is deliberately NOT done here: the item is left claimed-but-unsent. That
                // is the recoverable state -- the stale-claim sweep will pick it up again in 15
                // minutes. An unexpected exception is exactly the case where a retry is wanted, in
                // contrast to a SendGrid rejection, which is answered and final.
                _logger.LogError(ex, "Did You Know queued email {ItemId} (article {ArticleId}) failed; it will be retried after the stale-claim window.", item.Id, item.ArticleId);
            }
        }
    }

    // Retires an item: SentAtUtc is the terminal marker, so the row is never selected again. Named
    // "retired" rather than "sent" because it also covers definitively-undeliverable items -- which
    // is exactly why Status is recorded alongside it. SentAtUtc alone could not tell the two apart,
    // so a rejected Did You Know email showed up on the activity screen as if it had gone out.
    private Task MarkRetiredAsync(int itemId, EmailSendResult result) =>
        _db.DidYouKnowEmailQueueItems
            .Where(q => q.Id == itemId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.SentAtUtc, DateTime.UtcNow)
                .SetProperty(q => q.Status, result.Success
                    ? DidYouKnowQueueStatuses.Sent
                    : DidYouKnowQueueStatuses.Failed)
                .SetProperty(q => q.SendGridMessageId, result.ProviderMessageId ?? string.Empty)
                .SetProperty(q => q.FailureReason, result.Success ? string.Empty : result.Message));
}
