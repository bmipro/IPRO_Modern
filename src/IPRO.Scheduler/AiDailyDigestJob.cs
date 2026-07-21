using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

public class AiDailyDigestJob
{
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private readonly ILogger<AiDailyDigestJob> _logger;

    public AiDailyDigestJob(IPRODbContext db, IPackageEntitlementService entitlements, ILogger<AiDailyDigestJob> logger)
    {
        _db = db;
        _entitlements = entitlements;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        var today = DateTime.UtcNow.Date;
        var staleCutoff = DateTime.UtcNow.AddHours(-24);

        var agentIds = await _db.AgentUsers.Where(a => a.IsActive).Select(a => a.Id).ToListAsync();

        foreach (var agentId in agentIds)
        {
            try
            {
                if (!await _entitlements.HasAccessAsync(agentId, PackageFeatureCodes.AiDailyAssistant)) continue;

                var newLeadCount = await _db.WebsiteLeads
                    .CountAsync(l => l.AgentUserId == agentId && l.Status == WebsiteLeadStatuses.New);

                var staleLeadCount = await _db.WebsiteLeads
                    .CountAsync(l => l.AgentUserId == agentId && l.Status == WebsiteLeadStatuses.New && l.CreatedAt < staleCutoff);

                var noFollowUpCount = await _db.Clients
                    .Where(c => c.AgentUserId == agentId)
                    .Where(c => !_db.ClientFollowUps.Any(f => f.ClientId == c.Id && !f.IsCompleted))
                    .CountAsync();

                var mostOverdueFollowUp = await _db.ClientFollowUps
                    .Include(f => f.Client)
                    .Where(f => f.Client.AgentUserId == agentId && !f.IsCompleted && f.DueAt.Date < today)
                    .OrderBy(f => f.DueAt)
                    .FirstOrDefaultAsync();

                var oldestStaleLead = mostOverdueFollowUp == null
                    ? await _db.WebsiteLeads
                        .Where(l => l.AgentUserId == agentId && l.Status == WebsiteLeadStatuses.New && l.CreatedAt < staleCutoff)
                        .OrderBy(l => l.CreatedAt)
                        .FirstOrDefaultAsync()
                    : null;

                var noFollowUpClient = mostOverdueFollowUp == null && oldestStaleLead == null
                    ? await _db.Clients
                        .Where(c => c.AgentUserId == agentId)
                        .Where(c => !_db.ClientFollowUps.Any(f => f.ClientId == c.Id && !f.IsCompleted))
                        .OrderBy(c => c.CreatedAt)
                        .FirstOrDefaultAsync()
                    : null;

                string actionType, actionText;
                string? actionUrl;

                if (mostOverdueFollowUp != null)
                {
                    var daysOverdue = (today - mostOverdueFollowUp.DueAt.Date).Days;
                    actionType = AgentDailyInsightActionTypes.OverdueFollowUp;
                    actionUrl = $"/Clients/Details/{mostOverdueFollowUp.ClientId}";
                    actionText = $"Call {mostOverdueFollowUp.Client.FirstName} {mostOverdueFollowUp.Client.LastName} first — \"{mostOverdueFollowUp.Title}\" is {daysOverdue} day{(daysOverdue == 1 ? "" : "s")} overdue.";
                }
                else if (oldestStaleLead != null)
                {
                    var hoursOld = (int)(DateTime.UtcNow - oldestStaleLead.CreatedAt).TotalHours;
                    actionType = AgentDailyInsightActionTypes.StaleLead;
                    actionUrl = "/WebsiteLeads?status=new";
                    actionText = $"Call {oldestStaleLead.FirstName} {oldestStaleLead.LastName} first — lead has been waiting {hoursOld} hours.";
                }
                else if (noFollowUpClient != null)
                {
                    actionType = AgentDailyInsightActionTypes.NoFollowUp;
                    actionUrl = $"/Clients/Details/{noFollowUpClient.Id}";
                    actionText = $"Schedule a follow-up with {noFollowUpClient.FirstName} {noFollowUpClient.LastName} — nothing is on the books.";
                }
                else
                {
                    actionType = AgentDailyInsightActionTypes.None;
                    actionUrl = null;
                    actionText = "You're all caught up — no urgent actions today.";
                }

                var insight = await _db.AgentDailyInsights.FirstOrDefaultAsync(i => i.AgentUserId == agentId);
                if (insight == null)
                {
                    insight = new AgentDailyInsight { AgentUserId = agentId, CreatedAt = DateTime.UtcNow };
                    _db.AgentDailyInsights.Add(insight);
                }

                insight.NewLeadCount = newLeadCount;
                insight.StaleLeadCount = staleLeadCount;
                insight.NoFollowUpClientCount = noFollowUpCount;
                insight.SuggestedActionType = actionType;
                insight.SuggestedActionText = actionText;
                insight.SuggestedActionUrl = actionUrl;
                insight.GeneratedAt = DateTime.UtcNow;
                insight.UpdatedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI daily digest failed for agent {AgentId}", agentId);
            }
        }

        await _db.SaveChangesAsync();
    }
}
