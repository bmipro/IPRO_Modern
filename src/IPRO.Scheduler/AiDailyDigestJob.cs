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
            // The counts are counted by the database, one grouped query each across every eligible
            // agent. 671 (2026-11-23): these used to load every new lead, every client with no open
            // follow-up and every overdue follow-up of every eligible agent into memory, only to count
            // them and keep the first of each; that grows with the whole platform. Now the one row an
            // agent's suggestion names is fetched alone, in the loop, and only when it is needed
            // (an indexed query with LIMIT 1), in the same order the lists were sorted in.
            var leadCountsByAgent = await _db.WebsiteLeads
                .Where(l => eligibleAgentIds.Contains(l.AgentUserId) && l.Status == WebsiteLeadStatuses.New)
                .GroupBy(l => l.AgentUserId)
                .Select(g => new { AgentId = g.Key, New = g.Count(), Stale = g.Sum(l => l.CreatedAt < staleCutoff ? 1 : 0) })
                .ToDictionaryAsync(x => x.AgentId);

            var noFollowUpCountByAgent = await _db.Clients
                .Where(c => eligibleAgentIds.Contains(c.AgentUserId) && !_db.ClientFollowUps.Any(f => f.ClientId == c.Id && !f.IsCompleted))
                .GroupBy(c => c.AgentUserId)
                .Select(g => new { AgentId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.AgentId, x => x.Count);

            // DueAt.Date < today is DueAt < today (today is midnight), which an index can use.
            var agentsWithOverdue = (await _db.ClientFollowUps
                    .Where(f => eligibleAgentIds.Contains(f.Client.AgentUserId) && !f.IsCompleted && f.DueAt < today)
                    .Select(f => f.Client.AgentUserId)
                    .Distinct()
                    .ToListAsync())
                .ToHashSet();

            var existingInsightsByAgent = (await _db.AgentDailyInsights
                    .Where(i => eligibleAgentIds.Contains(i.AgentUserId))
                    .ToListAsync())
                .GroupBy(i => i.AgentUserId)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var agentId in eligibleAgentIds)
            {
                try
                {
                    var leadCounts = leadCountsByAgent.GetValueOrDefault(agentId);
                    var newLeadCount = leadCounts?.New ?? 0;
                    var staleLeadCount = leadCounts?.Stale ?? 0;
                    var noFollowUpCount = noFollowUpCountByAgent.GetValueOrDefault(agentId);

                    var mostOverdueFollowUp = agentsWithOverdue.Contains(agentId)
                        ? await _db.ClientFollowUps.AsNoTracking()
                            .Include(f => f.Client)
                            .Where(f => f.Client.AgentUserId == agentId && !f.IsCompleted && f.DueAt < today)
                            .OrderBy(f => f.DueAt)
                            .FirstOrDefaultAsync()
                        : null;

                    var oldestStaleLead = mostOverdueFollowUp == null && staleLeadCount > 0
                        ? await _db.WebsiteLeads.AsNoTracking()
                            .Where(l => l.AgentUserId == agentId && l.Status == WebsiteLeadStatuses.New && l.CreatedAt < staleCutoff)
                            .OrderBy(l => l.CreatedAt)
                            .FirstOrDefaultAsync()
                        : null;

                    var noFollowUpClient = mostOverdueFollowUp == null && oldestStaleLead == null && noFollowUpCount > 0
                        ? await _db.Clients.AsNoTracking()
                            .Where(c => c.AgentUserId == agentId && !_db.ClientFollowUps.Any(f => f.ClientId == c.Id && !f.IsCompleted))
                            .OrderBy(c => c.CreatedAt)
                            .FirstOrDefaultAsync()
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
