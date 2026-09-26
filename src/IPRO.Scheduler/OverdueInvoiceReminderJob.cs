using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

// The client-invoice reminder engine, daily. Until 523 slice 3 (2026-09-26) it sent one fixed
// "overdue" email to every unpaid invoice past its due date, again every seven days, for ever.
// Now it runs the adviser's own schedule (ClientInvoiceReminderSchedule): a set number of days
// before the due date, on it, the day after, then 7, 14 and 30 days overdue -- each stage a switch
// with its own wording, each sent at most once per invoice (ClientInvoiceReminderSend), never two
// within five days of each other (the adviser's own button counts), and all of it in the adviser's
// own day (INVARIANTS rule 10), which is why the job runs in the morning and not at midnight UTC.
// The Hangfire name stays "overdue-invoice-reminders" so there is one job, not a new one beside
// the old.
public class OverdueInvoiceReminderJob
{
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OverdueInvoiceReminderJob> _logger;

    public OverdueInvoiceReminderJob(IPRODbContext db, IPackageEntitlementService entitlements, IEmailService email, IConfiguration configuration, ILogger<OverdueInvoiceReminderJob> logger)
    {
        _db = db;
        _entitlements = entitlements;
        _email = email;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        var nowUtc = DateTime.UtcNow;

        // Every unpaid invoice whose due date is within reach of a stage: 30 days ahead for the
        // "before" stage, 59 days back for the last overdue one, a day either side for time zones.
        var earliest = nowUtc.Date.AddDays(-61);
        var latest = nowUtc.Date.AddDays(32);
        var candidates = await _db.ClientInvoices
            .Include(i => i.Client)
            .Include(i => i.AgentUser)
            .Where(i => i.DocumentType == ClientInvoiceDocumentType.Invoice
                     && (i.Status == ClientInvoiceStatus.Sent || i.Status == ClientInvoiceStatus.Approved)
                     && i.DueDate != null && i.DueDate >= earliest && i.DueDate <= latest)
            .OrderBy(i => i.DueDate)
            .Take(500)
            .ToListAsync();
        if (candidates.Count == 0) return;

        // One entitlement resolution, one settings read and one sent-stages read for the whole
        // batch instead of one per invoice (M-9's "hundreds of round trips per run").
        var accessByAgent = await _entitlements.HasAccessBulkAsync(
            candidates.Select(i => i.AgentUserId), PackageFeatureCodes.ClientInvoicing);
        var agentIds = candidates.Select(i => i.AgentUserId).Distinct().ToList();
        var settingsByAgent = await _db.ClientInvoiceReminderSettings.AsNoTracking()
            .Where(s => agentIds.Contains(s.AgentUserId))
            .ToDictionaryAsync(s => s.AgentUserId);
        var invoiceIds = candidates.Select(i => i.Id).ToList();
        var sentStages = (await _db.ClientInvoiceReminderSends.AsNoTracking()
                .Where(r => invoiceIds.Contains(r.ClientInvoiceId))
                .Select(r => new { r.ClientInvoiceId, r.Stage })
                .ToListAsync())
            .GroupBy(r => r.ClientInvoiceId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Stage).ToHashSet());

        foreach (var invoice in candidates)
        {
            try
            {
                if (!accessByAgent.TryGetValue(invoice.AgentUserId, out var hasAccess) || !hasAccess) continue;
                if (string.IsNullOrWhiteSpace(invoice.Client?.Email)) continue;

                var settings = settingsByAgent.TryGetValue(invoice.AgentUserId, out var own)
                    ? own
                    : ClientInvoiceReminderSchedule.Defaults(invoice.AgentUserId);
                var today = AgentLocalTime.FromUtc(nowUtc, invoice.AgentUser?.TimeZone).Date;
                var sent = sentStages.TryGetValue(invoice.Id, out var set) ? set : new HashSet<string>();
                var stage = ClientInvoiceReminderSchedule.StageDue(settings, invoice.DueDate!.Value, today, sent);
                if (stage == null) continue;

                // Never two reminders within days of each other, whichever sent the last one.
                if (invoice.LastReminderSentAt.HasValue
                    && invoice.LastReminderSentAt.Value > nowUtc.AddDays(-ClientInvoiceReminderSchedule.MinimumDaysBetweenReminders)) continue;

                var url = BuildInvoiceUrl(invoice.ViewToken);
                var (subject, html) = ClientInvoiceReminderEmail.Build(invoice, url, stage, settings, today);
                var result = await _email.SendDetailedAsync(invoice.Client.Email, $"{invoice.Client.FirstName} {invoice.Client.LastName}".Trim(), subject, html);

                // 452: every reminder is recorded on the invoice like the original send. A permanent
                // rejection still stamps the marker and the stage -- retrying a dead address every run
                // is a bounce a day against our sending reputation -- while a transient failure leaves
                // both clear so the next run tries again.
                await ClientInvoiceEmailLog.RecordAsync(_db, invoice, ClientInvoiceEmailKind.Reminder, invoice.Client.Email, subject, result.Success, result.ProviderMessageId, result.Message);
                if (result.Success || !result.IsTransient)
                {
                    invoice.LastReminderSentAt = nowUtc;
                    _db.ClientInvoiceReminderSends.Add(new ClientInvoiceReminderSend
                    {
                        ClientInvoiceId = invoice.Id,
                        AgentUserId = invoice.AgentUserId,
                        Stage = stage,
                        SentAt = nowUtc
                    });
                    sent.Add(stage);
                }

                // Persist THIS invoice's marker immediately (2026-08-14 ultra-audit): saving once after
                // the loop meant a transient failure at the end discarded every marker in the batch, and
                // Hangfire's retry then re-sent notices to clients who had already received them.
                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Invoice reminder for invoice {InvoiceId} failed", invoice.Id);
            }
        }
    }

    private string BuildInvoiceUrl(string token) =>
        $"{IPRO.Utility.WebAppUrlHelper.GetWebAppBaseUrl(_configuration)}/invoice/{token}";
}
