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
            .Take(200)
            .ToListAsync();

        foreach (var invoice in overdue)
        {
            try
            {
                if (!await _entitlements.HasAccessAsync(invoice.AgentUserId, PackageFeatureCodes.ClientInvoicing)) continue;
                if (string.IsNullOrWhiteSpace(invoice.Client?.Email)) continue;

                var url = BuildInvoiceUrl(invoice.ViewToken);
                var companyName = invoice.AgentUser.CompanyName;
                var html = $"""
                    <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
                      <div style="padding:22px;background:#1457d9;color:white"><h1 style="margin:0;font-size:24px">{System.Net.WebUtility.HtmlEncode(companyName)}</h1></div>
                      <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                        <p>This is a reminder that invoice <strong>{System.Net.WebUtility.HtmlEncode(invoice.DocumentNumber)}</strong> for <strong>${invoice.Total:N2} {invoice.Currency}</strong> is now overdue.</p>
                        <p><a href="{url}" style="display:inline-block;padding:11px 18px;background:#1457d9;color:white;text-decoration:none;border-radius:6px">View Invoice</a></p>
                      </div>
                    </div>
                    """;
                await _email.SendDetailedAsync(invoice.Client.Email, $"{invoice.Client.FirstName} {invoice.Client.LastName}".Trim(),
                    $"Reminder: Invoice {invoice.DocumentNumber} is overdue", html);
                invoice.LastReminderSentAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Overdue reminder for invoice {InvoiceId} failed", invoice.Id);
            }
        }

        await _db.SaveChangesAsync();
    }

    private string BuildInvoiceUrl(string token)
    {
        var baseUrl = _configuration["App:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl) || baseUrl.Contains("yourdomain.com", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = "https://ipro-prod-web.azurewebsites.net";
        }

        return $"{baseUrl.TrimEnd('/')}/invoice/{token}";
    }
}
