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

    // 452: invoices in Email Activity. One row per email (a resend or a reminder is its own row),
    // because each is its own delivery with its own outcome.
    public static async Task<List<EmailActivityRow>> InvoiceRowsAsync(IPRODbContext db, int agentId)
    {
        var emails = await db.ClientInvoiceEmails
            .AsNoTracking()
            .Where(e => e.AgentUserId == agentId)
            .Select(e => new
            {
                e.Id, e.Kind, e.Status, e.Subject, e.ToEmail, e.SentAt, e.CreatedAt,
                e.DeliveredAt, e.OpenedAt,
                Number = e.ClientInvoice.DocumentNumber,
                ClientFirst = e.ClientInvoice.Client.FirstName,
                ClientLast = e.ClientInvoice.Client.LastName
            })
            .ToListAsync();

        return emails
            .Select(e => new EmailActivityRow(
                e.Kind == ClientInvoiceEmailKind.Reminder ? "Invoice reminder" : "Invoice", "invoice", e.Id,
                e.Subject,
                $"{e.Number} · {($"{e.ClientFirst} {e.ClientLast}").Trim()} · {e.ToEmail}",
                e.Status.ToString(),
                e.SentAt ?? e.CreatedAt,
                1,
                e.SentAt != null ? 1 : 0,
                e.DeliveredAt != null ? 1 : 0,
                e.OpenedAt != null ? 1 : 0,
                e.Status is ClientInvoiceEmailStatus.Failed or ClientInvoiceEmailStatus.Bounced ? 1 : 0))
            .ToList();
    }

    public static async Task<List<EmailRecipientRow>> InvoiceRecipientsAsync(IPRODbContext db, int agentId, int emailId)
    {
        var rows = await db.ClientInvoiceEmails
            .AsNoTracking()
            .Where(e => e.Id == emailId && e.AgentUserId == agentId)
            .Select(e => new
            {
                e.ToEmail, e.Status, e.SentAt, e.DeliveredAt, e.OpenedAt, e.ClickedAt, e.FailureReason,
                ClientFirst = e.ClientInvoice.Client.FirstName,
                ClientLast = e.ClientInvoice.Client.LastName
            })
            .ToListAsync();

        return rows
            .Select(r => new EmailRecipientRow(
                ($"{r.ClientFirst} {r.ClientLast}").Trim(), r.ToEmail, r.Status.ToString(),
                r.SentAt, r.DeliveredAt, r.OpenedAt, r.ClickedAt, r.FailureReason))
            .ToList();
    }
}
