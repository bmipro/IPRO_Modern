using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

public class AiDailyDigestJob
{
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IAiSuggestionService _aiSuggestions;
    private readonly ILogger<AiDailyDigestJob> _logger;

    public AiDailyDigestJob(IPRODbContext db, IPackageEntitlementService entitlements, IAiSuggestionService aiSuggestions, ILogger<AiDailyDigestJob> logger)
    {
        _db = db;
        _entitlements = entitlements;
        _aiSuggestions = aiSuggestions;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        var today = DateTime.UtcNow.Date;
        var staleCutoff = DateTime.UtcNow.AddHours(-24);

        var agentIds = await _db.AgentUsers.Where(a => a.IsActive).Select(a => a.Id).ToListAsync();

        // Batched instead of one HasAccessAsync call per agent - see
        // PackageEntitlementService.HasAccessBulkAsync.
        var accessByAgent = await _entitlements.HasAccessBulkAsync(agentIds, PackageFeatureCodes.AiDailyAssistant);
        var eligibleAgentIds = agentIds.Where(id => accessByAgent.GetValueOrDefault(id)).ToList();

        var totalAiCalls = 0;
        var totalInputTokens = 0L;
        var totalOutputTokens = 0L;

        if (eligibleAgentIds.Count > 0)
        {
            // Each of these used to be its own query run once per agent (5-6 queries x every
            // active agent, every day). Now each is a single query across every eligible agent;
            // the per-agent value is picked out of the resulting in-memory groups inside the loop.
            // Every grouping below preserves the same OrderBy the original per-agent query used,
            // since Enumerable.GroupBy keeps each group's elements in source order.
            var newLeads = await _db.WebsiteLeads
                .Where(l => eligibleAgentIds.Contains(l.AgentUserId) && l.Status == WebsiteLeadStatuses.New)
                .OrderBy(l => l.CreatedAt)
                .ToListAsync();
            var newLeadsByAgent = newLeads.GroupBy(l => l.AgentUserId).ToDictionary(g => g.Key, g => g.ToList());
            var staleLeadsByAgent = newLeads.Where(l => l.CreatedAt < staleCutoff)
                .GroupBy(l => l.AgentUserId).ToDictionary(g => g.Key, g => g.ToList());

            var noFollowUpClients = await _db.Clients
                .Where(c => eligibleAgentIds.Contains(c.AgentUserId) && !_db.ClientFollowUps.Any(f => f.ClientId == c.Id && !f.IsCompleted))
                .OrderBy(c => c.CreatedAt)
                .ToListAsync();
            var noFollowUpClientsByAgent = noFollowUpClients.GroupBy(c => c.AgentUserId).ToDictionary(g => g.Key, g => g.ToList());

            var overdueFollowUps = await _db.ClientFollowUps
                .Include(f => f.Client)
                .Where(f => eligibleAgentIds.Contains(f.Client.AgentUserId) && !f.IsCompleted && f.DueAt.Date < today)
                .OrderBy(f => f.DueAt)
                .ToListAsync();
            var overdueFollowUpsByAgent = overdueFollowUps.GroupBy(f => f.Client.AgentUserId).ToDictionary(g => g.Key, g => g.ToList());

            var existingInsightsByAgent = (await _db.AgentDailyInsights
                    .Where(i => eligibleAgentIds.Contains(i.AgentUserId))
                    .ToListAsync())
                .GroupBy(i => i.AgentUserId)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var agentId in eligibleAgentIds)
            {
                try
                {
                    var newLeadCount = newLeadsByAgent.GetValueOrDefault(agentId)?.Count ?? 0;
                    var staleLeadCount = staleLeadsByAgent.GetValueOrDefault(agentId)?.Count ?? 0;
                    var noFollowUpCount = noFollowUpClientsByAgent.GetValueOrDefault(agentId)?.Count ?? 0;

                    var mostOverdueFollowUp = overdueFollowUpsByAgent.GetValueOrDefault(agentId)?.FirstOrDefault();

                    var oldestStaleLead = mostOverdueFollowUp == null
                        ? staleLeadsByAgent.GetValueOrDefault(agentId)?.FirstOrDefault()
                        : null;

                    var noFollowUpClient = mostOverdueFollowUp == null && oldestStaleLead == null
                        ? noFollowUpClientsByAgent.GetValueOrDefault(agentId)?.FirstOrDefault()
                        : null;

                    string actionType, actionText;
                    string? actionUrl, aiSituation;
                    int? relatedEntityId;

                    if (mostOverdueFollowUp != null)
                    {
                        var daysOverdue = (today - mostOverdueFollowUp.DueAt.Date).Days;
                        actionType = AgentDailyInsightActionTypes.OverdueFollowUp;
                        actionUrl = IPRO.Utility.PortalPaths.To($"/Clients/Details/{mostOverdueFollowUp.ClientId}");
                        actionText = $"Call {mostOverdueFollowUp.Client.FirstName} {mostOverdueFollowUp.Client.LastName} first — \"{mostOverdueFollowUp.Title}\" is {daysOverdue} day{(daysOverdue == 1 ? "" : "s")} overdue.";
                        aiSituation = $"A client follow-up task titled \"{mostOverdueFollowUp.Title}\" is {daysOverdue} day{(daysOverdue == 1 ? "" : "s")} overdue.";
                        relatedEntityId = mostOverdueFollowUp.Id;
                    }
                    else if (oldestStaleLead != null)
                    {
                        var hoursOld = (int)(DateTime.UtcNow - oldestStaleLead.CreatedAt).TotalHours;
                        actionType = AgentDailyInsightActionTypes.StaleLead;
                        actionUrl = IPRO.Utility.PortalPaths.To("/WebsiteLeads?status=new");
                        actionText = $"Call {oldestStaleLead.FirstName} {oldestStaleLead.LastName} first — lead has been waiting {hoursOld} hours.";
                        aiSituation = $"A new website lead (a contact request, not yet an existing client) has gone unanswered for {hoursOld} hours.";
                        relatedEntityId = oldestStaleLead.Id;
                    }
                    else if (noFollowUpClient != null)
                    {
                        actionType = AgentDailyInsightActionTypes.NoFollowUp;
                        actionUrl = IPRO.Utility.PortalPaths.To($"/Clients/Details/{noFollowUpClient.Id}");
                        actionText = $"Schedule a follow-up with {noFollowUpClient.FirstName} {noFollowUpClient.LastName} — nothing is on the books.";
                        aiSituation = "A client currently has no follow-up task scheduled at all.";
                        relatedEntityId = noFollowUpClient.Id;
                    }
                    else
                    {
                        actionType = AgentDailyInsightActionTypes.None;
                        actionUrl = null;
                        actionText = "You're all caught up — no urgent actions today.";
                        aiSituation = null;
                        relatedEntityId = null;
                    }

                    string? actionReason = null;
                    if (aiSituation != null)
                    {
                        var aiResult = await _aiSuggestions.GenerateActionReasonAsync(aiSituation);
                        actionReason = aiResult.Reason;
                        if (aiResult.InputTokens > 0 || aiResult.OutputTokens > 0)
                        {
                            totalAiCalls++;
                            totalInputTokens += aiResult.InputTokens;
                            totalOutputTokens += aiResult.OutputTokens;
                        }
                    }

                    if (!existingInsightsByAgent.TryGetValue(agentId, out var insight))
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
                    insight.SuggestedActionReason = actionReason;
                    insight.GeneratedAt = DateTime.UtcNow;
                    insight.UpdatedAt = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "AI daily digest failed for agent {AgentId}", agentId);
                }
            }
        }

        await AiUsageRecorder.RecordAsync(_db, totalAiCalls, totalInputTokens, totalOutputTokens);

        await _db.SaveChangesAsync();
    }
}
