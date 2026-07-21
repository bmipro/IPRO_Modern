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

        var totalAiCalls = 0;
        var totalInputTokens = 0L;
        var totalOutputTokens = 0L;

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
                string? actionUrl, aiSituation;

                if (mostOverdueFollowUp != null)
                {
                    var daysOverdue = (today - mostOverdueFollowUp.DueAt.Date).Days;
                    actionType = AgentDailyInsightActionTypes.OverdueFollowUp;
                    actionUrl = $"/Clients/Details/{mostOverdueFollowUp.ClientId}";
                    actionText = $"Call {mostOverdueFollowUp.Client.FirstName} {mostOverdueFollowUp.Client.LastName} first — \"{mostOverdueFollowUp.Title}\" is {daysOverdue} day{(daysOverdue == 1 ? "" : "s")} overdue.";
                    aiSituation = $"A client follow-up task titled \"{mostOverdueFollowUp.Title}\" is {daysOverdue} day{(daysOverdue == 1 ? "" : "s")} overdue.";
                }
                else if (oldestStaleLead != null)
                {
                    var hoursOld = (int)(DateTime.UtcNow - oldestStaleLead.CreatedAt).TotalHours;
                    actionType = AgentDailyInsightActionTypes.StaleLead;
                    actionUrl = "/WebsiteLeads?status=new";
                    actionText = $"Call {oldestStaleLead.FirstName} {oldestStaleLead.LastName} first — lead has been waiting {hoursOld} hours.";
                    aiSituation = $"A new website lead (a contact request, not yet an existing client) has gone unanswered for {hoursOld} hours.";
                }
                else if (noFollowUpClient != null)
                {
                    actionType = AgentDailyInsightActionTypes.NoFollowUp;
                    actionUrl = $"/Clients/Details/{noFollowUpClient.Id}";
                    actionText = $"Schedule a follow-up with {noFollowUpClient.FirstName} {noFollowUpClient.LastName} — nothing is on the books.";
                    aiSituation = "A client currently has no follow-up task scheduled at all.";
                }
                else
                {
                    actionType = AgentDailyInsightActionTypes.None;
                    actionUrl = null;
                    actionText = "You're all caught up — no urgent actions today.";
                    aiSituation = null;
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
                insight.SuggestedActionReason = actionReason;
                insight.GeneratedAt = DateTime.UtcNow;
                insight.UpdatedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI daily digest failed for agent {AgentId}", agentId);
            }
        }

        if (totalAiCalls > 0)
        {
            await RecordAiUsageAsync(today, totalAiCalls, totalInputTokens, totalOutputTokens);
        }

        await _db.SaveChangesAsync();
    }

    // Haiku 4.5 base pricing as of 2026-07-21 (platform.claude.com/docs/en/about-claude/pricing): $1/MTok input, $5/MTok output.
    private const decimal InputCostPerMillionTokens = 1.00m;
    private const decimal OutputCostPerMillionTokens = 5.00m;

    private async Task RecordAiUsageAsync(DateTime date, int callCount, long inputTokens, long outputTokens)
    {
        var estimatedCost = (inputTokens / 1_000_000m) * InputCostPerMillionTokens
                           + (outputTokens / 1_000_000m) * OutputCostPerMillionTokens;

        var log = await _db.AiUsageDailyLogs.FirstOrDefaultAsync(l => l.Date == date);
        if (log == null)
        {
            log = new AiUsageDailyLog { Date = date };
            _db.AiUsageDailyLogs.Add(log);
        }

        log.CallCount += callCount;
        log.InputTokens += inputTokens;
        log.OutputTokens += outputTokens;
        log.EstimatedCostUsd += estimatedCost;
        log.UpdatedAt = DateTime.UtcNow;
    }
}
