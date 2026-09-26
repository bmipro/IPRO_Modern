using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

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
        var today = DateTime.UtcNow.Date;
        var resendCutoff = DateTime.UtcNow.AddDays(-7);

        var overdue = await _db.ClientInvoices
            .Include(i => i.Client)
            .Include(i => i.AgentUser)
            .Where(i => i.DocumentType == ClientInvoiceDocumentType.Invoice
                     && i.Status == ClientInvoiceStatus.Sent
                     && i.DueDate != null && i.DueDate < today
                     && (i.LastReminderSentAt == null || i.LastReminderSentAt < resendCutoff))
            .OrderBy(i => i.LastReminderSentAt ?? DateTime.MinValue)
            .Take(200)
            .ToListAsync();

        // One entitlement resolution for the whole batch instead of one per invoice. The per-invoice
        // HasAccessAsync inside this loop was M-9's "hundreds of round trips per run" -- the batched
        // pattern below is the same one AiDailyDigestJob and ClientLifeEventReminderJob already use.
        var accessByAgent = await _entitlements.HasAccessBulkAsync(
            overdue.Select(i => i.AgentUserId), PackageFeatureCodes.ClientInvoicing);

        foreach (var invoice in overdue)
        {
            try
            {
                if (!accessByAgent.TryGetValue(invoice.AgentUserId, out var hasAccess) || !hasAccess) continue;
                if (string.IsNullOrWhiteSpace(invoice.Client?.Email)) continue;

                var url = BuildInvoiceUrl(invoice.ViewToken);
                // 523 (slice 2): the words live in ClientInvoiceReminderEmail, shared with the
                // "Send reminder" button on the adviser's aging page.
                var (subject, html) = ClientInvoiceReminderEmail.Build(invoice, url);
                var result = await _email.SendDetailedAsync(invoice.Client.Email, $"{invoice.Client.FirstName} {invoice.Client.LastName}".Trim(), subject, html);

                // 452: every reminder is recorded on the invoice like the original send. A permanent
                // rejection still stamps the marker -- retrying a dead address every run is a bounce
                // a day against our sending reputation -- while a transient failure leaves it clear
                // so the next run tries again.
                await ClientInvoiceEmailLog.RecordAsync(_db, invoice, ClientInvoiceEmailKind.Reminder, invoice.Client.Email, subject, result.Success, result.ProviderMessageId, result.Message);
                if (result.Success || !result.IsTransient)
                {
                    invoice.LastReminderSentAt = DateTime.UtcNow;
                }

                // Persist THIS invoice's marker immediately. Saving once after the loop meant a
                // transient failure at the end discarded every marker in the batch, and Hangfire's
                // default retry (up to 10 attempts) then re-sent overdue notices to clients who had
                // already received them -- LastReminderSentAt is the only thing gating a resend
                // (2026-08-14 ultra-audit).
                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Overdue reminder for invoice {InvoiceId} failed", invoice.Id);
            }
        }
    }

    private string BuildInvoiceUrl(string token) =>
        $"{IPRO.Utility.WebAppUrlHelper.GetWebAppBaseUrl(_configuration)}/invoice/{token}";
}
