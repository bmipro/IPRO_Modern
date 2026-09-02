using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Controllers;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// Email Activity said "everything you have sent" and listed newsletters, cards, letters, polls and
// Did-You-Know -- but not drip campaign steps, which were recorded (DripCampaignStepSends) and
// simply never surfaced. (TODO 448, 2026-09-02.) Kept as static queries, outside the controller, so
// they can be exercised against real MySQL; the controller has no test harness of its own.
public static class EmailActivityQueries
{
    // One row per campaign step that has reached at least one recipient, so a step reads like a
    // newsletter send: how many, how many arrived, how many opened, how many failed.
    public static async Task<List<EmailActivityRow>> DripStepRowsAsync(IPRODbContext db, int agentId)
    {
        var sends = await db.DripCampaignStepSends
            .AsNoTracking()
            .Where(s => s.DripCampaignStep.DripCampaign.AgentUserId == agentId)
            .Select(s => new
            {
                s.DripCampaignStepId,
                s.StepIndex,
                StepSubject = s.DripCampaignStep.Subject,
                Campaign = s.DripCampaignStep.DripCampaign.Name,
                s.Status,
                s.SentAt,
                s.DeliveredAt,
                s.OpenedAt,
                s.CreatedAt
            })
            .ToListAsync();

        return sends
            .GroupBy(s => s.DripCampaignStepId)
            .Select(g =>
            {
                var first = g.First();
                return new EmailActivityRow(
                    "Campaign", "drip", g.Key,
                    first.StepSubject,
                    $"{first.Campaign} · step {first.StepIndex + 1}",
                    g.Any(s => s.SentAt == null && s.Status == NewsLetterRecipientStatus.Queued) ? "Sending" : "Sent",
                    g.Max(s => s.SentAt ?? s.CreatedAt),
                    g.Count(),
                    g.Count(s => s.SentAt != null),
                    g.Count(s => s.DeliveredAt != null),
                    g.Count(s => s.OpenedAt != null),
                    g.Count(s => s.Status == NewsLetterRecipientStatus.Failed
                              || s.Status == NewsLetterRecipientStatus.Bounced
                              || s.Status == NewsLetterRecipientStatus.Dropped));
            })
            .ToList();
    }

    // Recipient-by-recipient detail for one step, scoped to the agent who owns the campaign.
    public static Task<List<EmailRecipientRow>> DripStepRecipientsAsync(IPRODbContext db, int agentId, int stepId) =>
        db.DripCampaignStepSends
            .AsNoTracking()
            .Where(s => s.DripCampaignStepId == stepId && s.DripCampaignStep.DripCampaign.AgentUserId == agentId)
            .OrderBy(s => s.RecipientName)
            .Select(s => new EmailRecipientRow(
                s.RecipientName, s.Email, s.Status.ToString(),
                s.SentAt, s.DeliveredAt, s.OpenedAt, s.ClickedAt, s.FailureReason))
            .ToListAsync();
}
