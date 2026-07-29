using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Email;

public class ECardDispatcher
{
    private readonly IPRODbContext _db;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ECardDispatcher> _logger;

    public ECardDispatcher(IPRODbContext db, IEmailService email, IConfiguration configuration, ILogger<ECardDispatcher> logger)
    {
        _db = db;
        _email = email;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task DispatchAsync(int ecardId)
    {
        var card = await _db.ECards.FirstOrDefaultAsync(c => c.Id == ecardId);
        if (card == null || card.Status != ECardStatuses.Scheduled) return;

        var agent = await _db.AgentUsers.FirstOrDefaultAsync(a => a.Id == card.AgentUserId);
        if (agent == null) return;

        // Resolve the design even if SuperAdmin has since retired it -- a scheduled card must
        // still send with the artwork the agent picked, not silently fall back to something else.
        var design = await _db.ECardDesigns.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Key == card.Occasion);
        if (design == null)
        {
            _logger.LogError("E-card {ECardId} references unknown design '{Key}' -- not sending.", card.Id, card.Occasion);
            card.Status = ECardStatuses.Failed;
            card.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return;
        }

        card.Status = ECardStatuses.Sending;
        await _db.SaveChangesAsync();

        // Card artwork lives in the web app's wwwroot, so the email needs absolute URLs.
        var html = ECardHtmlComposer.Wrap(card, agent, design, IPRO.Utility.WebAppUrlHelper.GetWebAppBaseUrl(_configuration));
        var replyToName = $"{agent.FirstName} {agent.LastName}".Trim();

        var recipients = await _db.ECardRecipients
            .Where(r => r.ECardId == card.Id && r.Status == ECardRecipientStatuses.Queued)
            .ToListAsync();

        var sentCount = 0;
        foreach (var recipient in recipients)
        {
            try
            {
                var result = await _email.SendDetailedAsync(
                    recipient.Email,
                    recipient.RecipientName,
                    card.Subject,
                    html,
                    customArgs: new Dictionary<string, string>
                    {
                        ["ipro_entity"] = "ecard",
                        ["ecard_id"] = card.Id.ToString(),
                        ["ecard_recipient_id"] = recipient.Id.ToString(),
                        ["client_id"] = recipient.ClientId.ToString(),
                        ["agent_user_id"] = card.AgentUserId.ToString()
                    },
                    replyToEmail: agent.Email,
                    replyToName: replyToName);

                recipient.Status = result.Success ? ECardRecipientStatuses.Sent : ECardRecipientStatuses.Failed;
                recipient.SendGridMessageId = result.ProviderMessageId ?? string.Empty;
                recipient.SentAt = result.Success ? DateTime.UtcNow : null;
                recipient.FailureReason = result.Success ? string.Empty : result.Message;
                recipient.UpdatedAt = DateTime.UtcNow;

                if (result.Success) sentCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "E-card {ECardId} failed for recipient {RecipientId}", card.Id, recipient.Id);
                recipient.Status = ECardRecipientStatuses.Failed;
                recipient.FailureReason = ex.Message;
                recipient.UpdatedAt = DateTime.UtcNow;
            }
        }

        card.Status = ECardStatuses.Sent;
        card.SentAt = DateTime.UtcNow;
        card.TotalSent = sentCount;
        card.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("E-card {ECardId} dispatched to {Count} recipients. Sent: {Sent}", card.Id, recipients.Count, sentCount);
    }
}
