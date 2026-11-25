using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

// 632 H-2 (2026-11-24): a lead notification held by the hourly limits (WebsiteLeadNotifications) is sent
// later instead of never. Every five minutes the held leads of the last day go out, oldest first, each
// when its website has room (its own limit, then the shared one past its first three an hour); a site
// with no room is passed over, never the sites behind it. Each one is claimed by a conditional
// update before its email, so two runs never send the same notification twice. A provider "not right
// now" puts it back and ends the pass; a lasting refusal is written on the row, as at the form. Held
// longer than a day, the row says it was not emailed: the lead is on the Website Leads list by then.
public class HeldLeadNotificationJob
{
    private const string Claimed = WebsiteLeadNotifications.SendingNow;
    private const int PerPass = 50;

    private readonly IPRODbContext _db;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<HeldLeadNotificationJob> _logger;

    public HeldLeadNotificationJob(IPRODbContext db, IEmailService email, IConfiguration configuration, ILogger<HeldLeadNotificationJob> logger)
    {
        _db = db; _email = email; _configuration = configuration; _logger = logger;
    }

    // Returns how many held notifications went out this pass.
    public async Task<int> RunAsync()
    {
        var now = DateTime.UtcNow;
        var oldest = now - WebsiteLeadNotifications.HoldFor;
        var held = WebsiteLeadNotifications.HeldMessage;
        var heldBefore = WebsiteLeadNotifications.HeldMessageBefore632;

        // A day is the limit; after that the row says so and nothing more is tried.
        await _db.WebsiteLeads
            .Where(l => !l.NotificationSent && (l.NotificationError == held || l.NotificationError == heldBefore) && l.CreatedAt < oldest)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.NotificationError, WebsiteLeadNotifications.ExpiredMessage));

        // A run that stopped between its claim and its result leaves the claim behind: after a quarter of
        // an hour it is held again, so the next pass sends it.
        var staleClaim = now.AddMinutes(-15);
        await _db.WebsiteLeads
            .Where(l => !l.NotificationSent && l.NotificationError == Claimed && l.UpdatedAt < staleClaim)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.NotificationError, held));

        var waiting = await _db.WebsiteLeads.AsNoTracking()
            .Where(l => !l.NotificationSent && (l.NotificationError == held || l.NotificationError == heldBefore) && l.CreatedAt >= oldest)
            .OrderBy(l => l.CreatedAt).ThenBy(l => l.Id)
            .Take(PerPass)
            .Select(l => l.Id)
            .ToListAsync();
        if (waiting.Count == 0) return 0;

        var portal = IPRO.Utility.WebAppUrlHelper.GetWebAppBaseUrl(_configuration);
        var sent = 0;
        var fullWebsites = new HashSet<int>();
        foreach (var leadId in waiting)
        {
            now = DateTime.UtcNow;
            var lead = await _db.WebsiteLeads.AsNoTracking()
                .Include(l => l.AgentWebsite).ThenInclude(w => w.AgentUser)
                .FirstOrDefaultAsync(l => l.Id == leadId);
            if (lead == null || fullWebsites.Contains(lead.AgentWebsiteId)) continue;
            if (await WebsiteLeadNotifications.IsHeldAsync(_db, lead.AgentWebsiteId, now)
                || !await WebsiteLeadNotifications.WithinSharedLimitAsync(_db, lead.AgentWebsiteId, now))
            {
                fullWebsites.Add(lead.AgentWebsiteId);   // no room for this site this pass; the others still may
                continue;
            }

            var claimed = await _db.WebsiteLeads
                .Where(l => l.Id == leadId && !l.NotificationSent && (l.NotificationError == held || l.NotificationError == heldBefore))
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.NotificationError, Claimed).SetProperty(l => l.UpdatedAt, now));
            if (claimed != 1) continue;   // another run has it, or it was sent meanwhile

            var agent = lead.AgentWebsite.AgentUser;
            EmailSendResult result;
            try
            {
                result = await _email.SendDetailedAsync(agent.Email, $"{agent.FirstName} {agent.LastName}".Trim(),
                    WebsiteLeadNotifications.Subject(lead), WebsiteLeadNotifications.Html(lead, portal));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The held notification for website lead {LeadId} could not be sent; it stays held.", leadId);
                result = EmailSendResult.FailedTransient(ex.Message);
            }

            if (result.Success)
            {
                var at = DateTime.UtcNow;
                await _db.WebsiteLeads.Where(l => l.Id == leadId)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(l => l.NotificationSent, true)
                        .SetProperty(l => l.NotificationSentAt, (DateTime?)at)
                        .SetProperty(l => l.NotificationError, string.Empty)
                        .SetProperty(l => l.UpdatedAt, at));
                sent++;
                continue;
            }

            if (result.IsTransient)
            {
                // Not this lead's fault (the provider's limit, a timeout): held again, and the rest wait too.
                await _db.WebsiteLeads.Where(l => l.Id == leadId)
                    .ExecuteUpdateAsync(u => u.SetProperty(l => l.NotificationError, held));
                break;
            }

            var reason = result.Message.Length > 500 ? result.Message[..500] : result.Message;
            await _db.WebsiteLeads.Where(l => l.Id == leadId)
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.NotificationError, reason));
            _logger.LogWarning("The held notification for website lead {LeadId} was refused: {Reason}", leadId, reason);
        }

        if (sent > 0) _logger.LogInformation("Held lead notifications: {Sent} sent this pass.", sent);
        return sent;
    }
}
