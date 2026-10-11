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
    // since (565): the start of the period the list is showing; null is every email.
    public static async Task<List<EmailActivityRow>> InvoiceRowsAsync(IPRODbContext db, int agentId, DateTime? since = null)
    {
        var emails = await db.ClientInvoiceEmails
            .AsNoTracking()
            .Where(e => e.AgentUserId == agentId)
            .Where(e => since == null || (e.SentAt ?? e.CreatedAt) >= since)
            .Select(e => new
            {
                e.Id, e.Kind, e.Status, e.Subject, e.ToEmail, e.SentAt, e.CreatedAt,
                e.DeliveredAt, e.OpenedAt,
                LastViewed = e.ClientInvoice.LastViewedAt,
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
                // 562: invoice mail carries no open pixel; what counts is the client viewing the invoice
                // itself after this email went out. The list shows that in the Opened column.
                InvoiceViewText.ViewedSince(e.SentAt, e.LastViewed) ? 1 : 0,
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

    // 562: the invoice behind one invoice email, and when the client last looked at it.
    // 565: the search box finds an email by who received it. Of the sends given (the adviser's own, in
    // the period on the screen), these reached someone whose name or address holds the text. An invoice
    // row already carries its client's name and address in its own line, so it needs no lookup here.
    public static async Task<HashSet<(string TypeKey, int Id)>> RecipientMatchesAsync(IPRODbContext db, int agentId, string text, IReadOnlyCollection<EmailActivityRow> rows)
    {
        var found = new HashSet<(string TypeKey, int Id)>();
        List<int> Ids(string typeKey) => rows.Where(r => r.TypeKey == typeKey).Select(r => r.Id).Distinct().ToList();
        void Add(string typeKey, IEnumerable<int> ids) { foreach (var id in ids) found.Add((typeKey, id)); }

        var newsletters = Ids("newsletter").Cast<int?>().ToList();
        if (newsletters.Count > 0)
        {
            Add("newsletter", (await db.NewsLetterRecipients.AsNoTracking()
                .Where(r => newsletters.Contains(r.NewsLetterSendId) && (r.RecipientName.Contains(text) || r.Email.Contains(text)))
                .Select(r => r.NewsLetterSendId).Distinct().ToListAsync()).Where(id => id.HasValue).Select(id => id!.Value));
        }

        var cards = Ids("ecard");
        if (cards.Count > 0)
        {
            Add("ecard", await db.ECardRecipients.AsNoTracking()
                .Where(r => cards.Contains(r.ECardId) && (r.RecipientName.Contains(text) || r.Email.Contains(text)))
                .Select(r => r.ECardId).Distinct().ToListAsync());
        }

        var letters = Ids("eletter");
        if (letters.Count > 0)
        {
            Add("eletter", await db.ELetterRecipients.AsNoTracking()
                .Where(r => letters.Contains(r.ELetterId) && (r.RecipientName.Contains(text) || r.Email.Contains(text)))
                .Select(r => r.ELetterId).Distinct().ToListAsync());
        }

        var polls = Ids("poll").Cast<int?>().ToList();
        if (polls.Count > 0)
        {
            Add("poll", (await db.PollRecipients.AsNoTracking()
                .Where(r => polls.Contains(r.PollSendId) && (r.RecipientName.Contains(text) || r.Email.Contains(text)))
                .Select(r => r.PollSendId).Distinct().ToListAsync()).Where(id => id.HasValue).Select(id => id!.Value));
        }

        var steps = Ids("drip");
        if (steps.Count > 0)
        {
            Add("drip", await db.DripCampaignStepSends.AsNoTracking()
                .Where(s => steps.Contains(s.DripCampaignStepId) && s.DripCampaignStep.DripCampaign.AgentUserId == agentId
                            && (s.RecipientName.Contains(text) || s.Email.Contains(text)))
                .Select(s => s.DripCampaignStepId).Distinct().ToListAsync());
        }

        var articles = Ids("didyouknow");
        if (articles.Count > 0)
        {
            Add("didyouknow", await db.DidYouKnowEmailQueueItems.AsNoTracking()
                .Where(q => articles.Contains(q.ArticleId) && db.Clients.Any(c => c.Id == q.ClientId && c.AgentUserId == agentId
                            && (c.FirstName.Contains(text) || c.LastName.Contains(text) || c.Email.Contains(text) || (c.FirstName + " " + c.LastName).Contains(text))))
                .Select(q => q.ArticleId).Distinct().ToListAsync());
        }

        return found;
    }

    public static async Task<InvoiceViewInfo?> InvoiceViewAsync(IPRODbContext db, int agentId, int emailId)
    {
        return await db.ClientInvoiceEmails
            .AsNoTracking()
            .Where(e => e.Id == emailId && e.AgentUserId == agentId)
            .Select(e => new InvoiceViewInfo(
                e.ClientInvoiceId, e.ClientInvoice.DocumentNumber, e.SentAt,
                e.ClientInvoice.FirstViewedAt, e.ClientInvoice.LastViewedAt, e.ClientInvoice.ViewCount))
            .FirstOrDefaultAsync();
    }
}

// 562 (2026-10-11). The owner, looking at an invoice email in Email Activity: "remind me again as why
// we do not track Opened?" and, of the explanation under the table, "it is as confusing if it was
// written by me". Invoice mail has no open pixel on purpose: the invoice page stamps a view each time
// the client opens the invoice (452), which says more than a mail app loading pictures. The screen
// said "not tracked" and never mentioned that. It now says whether the invoice was viewed.
public record InvoiceViewInfo(int InvoiceId, string DocumentNumber, DateTime? SentAt, DateTime? FirstViewedAt, DateTime? LastViewedAt, int ViewCount)
{
    public bool ViewedSinceThisEmail => InvoiceViewText.ViewedSince(SentAt, LastViewedAt);
}

public static class InvoiceViewText
{
    // An invoice can be emailed more than once (a resend, a reminder). A view counts for an email
    // when it happened after that email went out.
    public static bool ViewedSince(DateTime? sentAt, DateTime? lastViewedAt) =>
        sentAt.HasValue && lastViewedAt.HasValue && lastViewedAt.Value >= sentAt.Value;

    public static string Describe(InvoiceViewInfo? view, Func<DateTime, string> stamp)
    {
        if (view == null || view.ViewCount <= 0 || !view.LastViewedAt.HasValue) return "Not viewed yet";
        var when = stamp(view.LastViewedAt.Value);
        if (!view.ViewedSinceThisEmail) return $"Not since this email (last viewed {when})";
        return view.ViewCount == 1 ? $"Viewed {when}" : $"Viewed {when} ({view.ViewCount} views in all)";
    }
}
