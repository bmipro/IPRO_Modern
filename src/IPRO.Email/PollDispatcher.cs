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
        var send = await _db.PollSends.FirstOrDefaultAsync(s => s.Id == sendId);
        if (send == null || send.Status != PollSendStatus.Scheduled) return;

        var survey = await _db.PollSurveys.FirstOrDefaultAsync(s => s.Id == send.PollSurveyId);
        if (survey == null) return;

        send.Status = PollSendStatus.Sending;
        survey.Status = PollSurveyStatus.Sending;
        await _db.SaveChangesAsync();

        var audience = await GetAudienceClientsAsync(send);

        // Consent is applied at AUDIENCE SELECTION here, not after the fact: poll recipient rows are
        // created by this method, so an unsubscribed client simply never becomes a recipient rather
        // than becoming one that is immediately failed. (Cards and letters differ -- their recipient
        // rows already exist by the time the dispatcher runs, so they filter inside the send loop.)
        var eligible = audience.Where(c => !_consent.IsSuppressed(c, IPRO.Business.Services.EmailChannel.Poll)).ToList();
        var suppressedCount = audience.Count - eligible.Count;
        if (suppressedCount > 0)
        {
            _logger.LogInformation(
                "Poll send {SendId}: {Suppressed} of {Total} clients skipped -- unsubscribed.",
                send.Id, suppressedCount, audience.Count);
        }

        var recipients = eligible
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

        send.TotalRecipients = recipients.Count;
        _db.PollRecipients.AddRange(recipients);
        await _db.SaveChangesAsync();

        var sentCount = 0;
        var failedCount = 0;
        foreach (var recipient in recipients)
        {
            try
            {
                var voteUrl = BuildVoteUrl(recipient.VoteToken);

                // Eligibility was settled above, so this is only fetching the client to mint the
                // unsubscribe token.
                var client = eligible.First(c => c.Id == recipient.ClientId);
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
        }

        send.Status = sentCount > 0 ? PollSendStatus.Sent : PollSendStatus.Failed;
        send.SentAt = DateTime.UtcNow;
        send.TotalSent = sentCount;
        send.TotalFailed = failedCount;

        survey.Status = PollSurveyStatus.Sent;
        survey.SentAt = DateTime.UtcNow;
        survey.TotalRecipients += recipients.Count;
        survey.TotalSent += sentCount;
        survey.TotalFailed += failedCount;

        await _db.SaveChangesAsync();

        _logger.LogInformation("Poll send {SendId} for poll {PollId} dispatched to {Count} recipients. Sent: {Sent}, Failed: {Failed}",
            send.Id, survey.Id, recipients.Count, sentCount, failedCount);
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

    private async Task<List<Client>> GetAudienceClientsAsync(PollSend send)
    {
        var query = _db.Clients
            .Include(c => c.Categories)
            .Where(c => c.AgentUserId == send.AgentUserId && c.IsNewsletterSubscribed);

        query = send.AudienceType switch
        {
            NewsLetterAudienceType.AccountType when send.ClientCategoryId.HasValue =>
                query.Where(c => c.Categories.Any(cat => cat.Id == send.ClientCategoryId.Value)),
            NewsLetterAudienceType.IndividualClient when send.ClientId.HasValue =>
                query.Where(c => c.Id == send.ClientId.Value),
            _ => query
        };

        return await query.ToListAsync();
    }
}
