using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;

namespace IPRO.Email;

public class PollDispatcher
{
    private readonly IPRODbContext _db;
    private readonly IEmailService _email;
    private readonly IPRO.Business.Services.IEmailConsentService _consent;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PollDispatcher> _logger;

    public PollDispatcher(IPRODbContext db, IEmailService email, IPRO.Business.Services.IEmailConsentService consent, IConfiguration configuration, ILogger<PollDispatcher> logger)
    {
        _db = db;
        _email = email;
        _consent = consent;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task DispatchSendAsync(int sendId)
    {
        // CLAIM FIRST, LOAD SECOND -- see the note in SendClaims. PollsController materialises this
        // row and calls us on the same scoped context, so any read before the claim is stale.
        var heldAttempts = await SendClaims.TryClaimPollSendAsync(_db, sendId, DateTime.UtcNow);
        if (heldAttempts == null) return;
        SendClaims.ForgetTracked<PollSend>(_db, sendId);

        var send = await _db.PollSends.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sendId);
        if (send == null) return;

        // The survey stays TRACKED -- unlike the send row it is only ever written here and its
        // columns are not touched by the webhook.
        var survey = await _db.PollSurveys.FirstOrDefaultAsync(s => s.Id == send.PollSurveyId);
        if (survey == null)
        {
            await FailAndReleaseAsync(sendId, heldAttempts.Value, "its poll survey no longer exists");
            return;
        }

        survey.Status = PollSurveyStatus.Sending;
        await _db.SaveChangesAsync();

        // RESUME GATE, and it sits ABOVE the audience query on purpose. Re-resolving the audience on
        // a resumed claim would (a) add recipients for anyone added to the target category since the
        // first pass, and (b) let the audience-failure branch below stamp Failed on a send that had
        // already delivered mail. If recipient rows exist, the audience was decided already.
        var existing = await _db.PollRecipients.Where(r => r.PollSendId == send.Id).ToListAsync();
        var isResume = existing.Count > 0;
        List<PollRecipient> recipients;
        var suppressedCount = 0;

        if (isResume)
        {
            // SentAt is tested FIRST and Status second, and that order is load-bearing:
            // PollVoteController overwrites Status with Responded when someone votes, so a
            // Status-only skip would re-mail everyone who had already answered.
            recipients = existing
                .Where(r => r.SentAt == null
                         && r.Status != PollRecipientStatus.Sent
                         && r.Status != PollRecipientStatus.Responded
                         && r.Status != PollRecipientStatus.Failed)
                .ToList();

            _logger.LogInformation(
                "Poll send {SendId} resumed after an interrupted run: {Remaining} of {Total} recipients still to mail.",
                send.Id, recipients.Count, existing.Count);
        }
        else
        {
            var audience = await GetAudienceClientsAsync(send);
            if (audience == null)
            {
                // The client or category this poll was aimed at has been deleted. Fail loudly instead
                // of reporting a successful send to nobody (category path) or blasting every
                // subscriber (individual-client path) -- both of which this code did before 2026-08-15.
                //
                // The survey is unwound to Draft as well. Leaving it Sending would lock the agent out
                // of Edit and AddQuestion, which both gate on Draft -- so the poll they need to fix
                // would be the one poll they could no longer touch.
                survey.Status = PollSurveyStatus.Draft;
                await _db.SaveChangesAsync();

                await FailAndReleaseAsync(sendId, heldAttempts.Value,
                    $"its {send.AudienceType} audience no longer exists (the client or category was " +
                    "deleted after the send was scheduled). Nothing was emailed");
                return;
            }

            // Consent is applied at AUDIENCE SELECTION here, not after the fact: poll recipient rows
            // are created by this method, so an unsubscribed client simply never becomes a recipient
            // rather than becoming one that is immediately failed. It is ALSO re-checked per
            // recipient in the loop below, which is what covers a resume -- somebody can unsubscribe
            // between the first pass and the second.
            var eligible = audience.Where(c => !_consent.IsSuppressed(c, IPRO.Business.Services.EmailChannel.Poll)).ToList();
            suppressedCount = audience.Count - eligible.Count;
            if (suppressedCount > 0)
            {
                _logger.LogInformation(
                    "Poll send {SendId}: {Suppressed} of {Total} clients skipped -- unsubscribed.",
                    send.Id, suppressedCount, audience.Count);
            }

            recipients = eligible
                .Select(c => new PollRecipient
                {
                    PollSurveyId = survey.Id,
                    PollSendId = send.Id,
                    ClientId = c.Id,
                    Email = c.Email.Trim().ToLowerInvariant(),
                    RecipientName = $"{c.FirstName} {c.LastName}".Trim(),
                    Status = PollRecipientStatus.Queued,
                    VoteToken = Guid.NewGuid().ToString("N")
                })
                .ToList();

            _db.PollRecipients.AddRange(recipients);
            await _db.SaveChangesAsync();

            // Only on the first build. A resume must not overwrite the original audience size.
            await _db.PollSends.Where(s => s.Id == send.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.TotalRecipients, recipients.Count));
        }

        var sentCount = 0;
        var failedCount = 0;
        var lastHeartbeat = DateTime.UtcNow;
        foreach (var recipient in recipients)
        {
            if (DateTime.UtcNow - lastHeartbeat > SendClaims.HeartbeatInterval)
            {
                lastHeartbeat = DateTime.UtcNow;
                if (!await SendClaims.HeartbeatPollSendAsync(_db, send.Id, heldAttempts.Value, lastHeartbeat))
                {
                    _logger.LogWarning(
                        "Poll send {SendId} was re-claimed by another run; abandoning this one after {Sent} sends.",
                        send.Id, sentCount);
                    return;
                }
            }

            try
            {
                var voteUrl = BuildVoteUrl(recipient.VoteToken);

                // Re-checked at SEND time, not just at selection. On a resume the selection happened
                // in an earlier run and the person may have unsubscribed since; without this, the
                // resume gate above would quietly reopen the consent hole it was added to close.
                var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == recipient.ClientId);
                if (client == null || _consent.IsSuppressed(client, IPRO.Business.Services.EmailChannel.Poll))
                {
                    recipient.Status = PollRecipientStatus.Failed;
                    recipient.FailedAt = DateTime.UtcNow;
                    recipient.FailureReason = client == null
                        ? "Client no longer exists."
                        : "Recipient has unsubscribed from these emails.";
                    recipient.UpdatedAt = DateTime.UtcNow;
                    suppressedCount++;
                    await _db.SaveChangesAsync();
                    continue;
                }

                var preferencesUrl = _consent.BuildPreferencesUrl(await _consent.GetOrCreateTokenAsync(client));

                var result = await _email.SendDetailedAsync(
                    recipient.Email,
                    recipient.RecipientName,
                    survey.Subject,
                    BuildEmailHtml(survey, voteUrl),
                    BuildEmailText(survey, voteUrl),
                    new Dictionary<string, string>
                    {
                        ["ipro_entity"] = "poll",
                        ["poll_id"] = survey.Id.ToString(),
                        ["poll_send_id"] = send.Id.ToString(),
                        ["poll_recipient_id"] = recipient.Id.ToString(),
                        ["client_id"] = recipient.ClientId?.ToString() ?? string.Empty,
                        ["agent_user_id"] = send.AgentUserId.ToString()
                    },
                    listUnsubscribeUrl: preferencesUrl);

                recipient.Status = result.Success ? PollRecipientStatus.Sent : PollRecipientStatus.Failed;
                recipient.SendGridMessageId = result.ProviderMessageId ?? string.Empty;
                recipient.SentAt = result.Success ? DateTime.UtcNow : null;
                recipient.FailedAt = result.Success ? null : DateTime.UtcNow;
                recipient.FailureReason = result.Success ? string.Empty : result.Message;
                recipient.UpdatedAt = DateTime.UtcNow;

                if (result.Success) sentCount++;
                else failedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Poll send {SendId} failed for recipient {RecipientId}", send.Id, recipient.Id);
                recipient.Status = PollRecipientStatus.Failed;
                recipient.FailedAt = DateTime.UtcNow;
                recipient.FailureReason = ex.Message;
                recipient.UpdatedAt = DateTime.UtcNow;
                failedCount++;
            }

            // Outside the try -- see the matching note in ECardDispatcher. Persisting each recipient
            // as it is handled is what makes the Queued filter above a real resume guard.
            await _db.SaveChangesAsync();
        }

        // Every total below is COUNTED from the recipient rows rather than accumulated in this run.
        // The survey counters used += , which double-counted on a resumed send; and the per-send
        // totals were this pass's numbers, which under-report a send that was finished in two parts.
        var sendSent = await _db.PollRecipients.CountAsync(r => r.PollSendId == send.Id
            && (r.Status == PollRecipientStatus.Sent || r.Status == PollRecipientStatus.Responded));
        var sendFailed = await _db.PollRecipients.CountAsync(r => r.PollSendId == send.Id
            && r.Status == PollRecipientStatus.Failed);

        var now = DateTime.UtcNow;
        var applied = await _db.PollSends
            .Where(s => s.Id == send.Id && s.ClaimAttempts == heldAttempts.Value)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Status, sendSent > 0 ? PollSendStatus.Sent : PollSendStatus.Failed)
                // ??= so a resume does not re-date a send that first went out an hour ago; the
                // Email Activity screen reads this as the delivery time.
                .SetProperty(s => s.SentAt, s => s.SentAt ?? now)
                .SetProperty(s => s.TotalSent, sendSent)
                .SetProperty(s => s.TotalFailed, sendFailed)
                .SetProperty(s => s.ClaimedAt, (DateTime?)null));

        if (applied != 1)
        {
            _logger.LogWarning("Poll send {SendId} finished but was already re-claimed; leaving the new owner's state alone.", send.Id);
            return;
        }

        // A survey can be sent more than once, so its totals span every send.
        survey.Status = PollSurveyStatus.Sent;
        survey.SentAt ??= now;
        survey.TotalRecipients = await _db.PollRecipients.CountAsync(r => r.PollSurveyId == survey.Id);
        survey.TotalSent = await _db.PollRecipients.CountAsync(r => r.PollSurveyId == survey.Id
            && (r.Status == PollRecipientStatus.Sent || r.Status == PollRecipientStatus.Responded));
        survey.TotalFailed = await _db.PollRecipients.CountAsync(r => r.PollSurveyId == survey.Id
            && r.Status == PollRecipientStatus.Failed);
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Poll send {SendId} for poll {PollId}: {Count} handled this pass ({Sent} sent, {Failed} failed, {Suppressed} suppressed). Send totals now {TotalSent}/{TotalFailed}.",
            send.Id, survey.Id, recipients.Count, sentCount, failedCount, suppressedCount, sendSent, sendFailed);
    }

    // A send that cannot proceed. Writes the terminal status and clears the claim together, so it is
    // never left Sending-and-claimed for the sweep to retry three times before reporting something
    // that was never going to work.
    private async Task FailAndReleaseAsync(int sendId, int heldAttempts, string reason)
    {
        _logger.LogError("Poll send {SendId} was cancelled because {Reason}.", sendId, reason);

        var applied = await _db.PollSends
            .Where(s => s.Id == sendId && s.ClaimAttempts == heldAttempts)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Status, PollSendStatus.Failed)
                .SetProperty(s => s.ClaimedAt, (DateTime?)null));
        if (applied != 1) return;

        // See ECardDispatcher.FailAndReleaseAsync -- the same fan-out, same reason (441).
        var failureReason = $"Not sent: {reason}";
        var now = DateTime.UtcNow;
        await _db.PollRecipients
            .Where(r => r.PollSendId == sendId && r.Status == PollRecipientStatus.Queued)
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.Status, PollRecipientStatus.Failed)
                .SetProperty(r => r.FailureReason, failureReason)
                .SetProperty(r => r.UpdatedAt, now));
    }

    private string BuildVoteUrl(string token)
    {
        var baseUrl = _configuration["App:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl) || baseUrl.Contains("yourdomain.com", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = "https://ipro-prod-web.azurewebsites.net";
        }

        return $"{baseUrl.TrimEnd('/')}/Poll/Vote?token={WebUtility.UrlEncode(token)}";
    }

    private static string BuildEmailHtml(PollSurvey survey, string voteUrl)
    {
        var encodedUrl = WebUtility.HtmlEncode(voteUrl);
        var intro = WebUtility.HtmlEncode(survey.IntroText);
        return $"""
            <div style="font-family:Arial,sans-serif;color:#17223a;">
              <h2 style="margin-bottom:8px;">{WebUtility.HtmlEncode(survey.Title)}</h2>
              <p style="color:#475569;line-height:1.5;">{intro}</p>
              <p style="margin-top:24px;">
                <a href="{encodedUrl}" style="display:inline-block;padding:12px 24px;background:#1457d9;color:#fff;text-decoration:none;border-radius:6px;font-weight:700;">Take the poll</a>
              </p>
            </div>
            """;
    }

    private static string BuildEmailText(PollSurvey survey, string voteUrl)
    {
        return $"""
            {survey.Title}

            {survey.IntroText}

            Take the poll: {voteUrl}
            """;
    }

    // Returns null -- NOT an empty list -- when this send's audience no longer resolves. Same
    // fail-closed contract as NewsLetterDispatcher; see the longer note there.
    //
    // PollSends.ClientCategoryId has no FK at all (this is one of the repair-created tables, which
    // declare none), so deleting a category leaves the id pointing at nothing: the Categories.Any
    // filter then matched nobody and the poll reported success having emailed zero people. The
    // ClientId path is worse -- it took the `_ => query` fall-through and went to every subscriber.
    private async Task<List<Client>?> GetAudienceClientsAsync(PollSend send)
    {
        // Deliberately NOT filtered on IsNewsletterSubscribed. That flag is the newsletter's own
        // preference; a poll is a different channel, and EmailConsentService.IsSuppressed (applied
        // at :59) is the one place allowed to decide who may be mailed. Filtering here also made
        // the "N of M suppressed" log at :61 always report zero, because the suppressed clients had
        // already been removed before the count was taken -- the audit trail said nobody was
        // skipped on exactly the sends where people were.
        var query = _db.Clients
            .Include(c => c.Categories)
            .Where(c => c.AgentUserId == send.AgentUserId);

        var targeted = send.AudienceType switch
        {
            NewsLetterAudienceType.AccountType =>
                send.ClientCategoryId.HasValue
                && await _db.ClientCategories.AnyAsync(cat => cat.Id == send.ClientCategoryId.Value)
                    ? query.Where(c => c.Categories.Any(cat => cat.Id == send.ClientCategoryId.Value))
                    : null,
            NewsLetterAudienceType.IndividualClient =>
                send.ClientId.HasValue
                && await _db.Clients.AnyAsync(c => c.Id == send.ClientId.Value)
                    ? query.Where(c => c.Id == send.ClientId.Value)
                    : null,
            _ => query
        };

        if (targeted == null) return null;

        return await targeted.ToListAsync();
    }
}
