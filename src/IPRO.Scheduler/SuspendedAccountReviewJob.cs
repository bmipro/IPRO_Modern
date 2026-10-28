using System.Net;
using IPRO.DataAccess;
using IPRO.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

// 599 (the owner's decision of 2026-10-24): a suspended account (SuperAdmin -> Deactivate) keeps all its
// data. A year after the suspension -- and a year after each "Keep another year" -- iPro's mailbox gets one
// email per due account, asking to keep the data another year or delete the account (Delete keeps the
// invoices, as for any adviser). Nothing is ever deleted by this job.
public class SuspendedAccountReviewJob
{
    private readonly IPRODbContext _db;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SuspendedAccountReviewJob> _logger;

    public SuspendedAccountReviewJob(IPRODbContext db, IEmailService email, IConfiguration configuration, ILogger<SuspendedAccountReviewJob> logger)
    {
        _db = db; _email = email; _configuration = configuration; _logger = logger;
    }

    public async Task<int> RunAsync()
    {
        var now = DateTime.UtcNow;
        var due = await _db.AgentUsers
            .Where(a => !a.IsActive && a.SuspendedAt != null && a.DataReviewDueAt != null && a.DataReviewDueAt <= now && a.DataReviewNoticeSentAt == null)
            .OrderBy(a => a.DataReviewDueAt)
            .ToListAsync();
        if (due.Count == 0) return 0;

        var to = SignupNotice.Recipient(_configuration);
        if (string.IsNullOrWhiteSpace(to))
        {
            _logger.LogWarning("Suspended-account review: {Count} account(s) due, but no Signups:NotificationEmail or Support:NotificationEmail is set.", due.Count);
            return 0;
        }

        var sent = 0;
        foreach (var agent in due)
        {
            var company = string.IsNullOrWhiteSpace(agent.CompanyName) ? $"{agent.FirstName} {agent.LastName}".Trim() : agent.CompanyName.Trim();
            var link = SignupNotice.AdminLink(_configuration, agent.Id);
            var html = $"""
                <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
                  <div style="padding:22px;background:#0f3f8f;color:white"><h1 style="margin:0;font-size:22px">Suspended account: time to decide</h1></div>
                  <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                    <p><strong>{WebUtility.HtmlEncode(company)}</strong> ({WebUtility.HtmlEncode(agent.UserName)}) has been suspended since {agent.SuspendedAt:MMMM d, yyyy}, and its data has been kept for a year.</p>
                    <p>In SuperAdmin you can <strong>Keep another year</strong> (you'll be asked again in a year) or <strong>Delete</strong> the account. Delete erases their data but keeps their invoices for your records.</p>
                    <p><a href="{link}" style="display:inline-block;padding:11px 18px;background:#0f3f8f;color:white;text-decoration:none;border-radius:6px">Open in SuperAdmin</a></p>
                    <p style="color:#5b6b7a;font-size:13px">Nothing is deleted unless you choose to. This is the only reminder for this year.</p>
                  </div>
                </div>
                """;
            var result = await _email.SendDetailedAsync(to, "iPro", $"Suspended account review: {company}", html);
            if (!result.Success)
            {
                _logger.LogWarning("Suspended-account review email for agent {AgentId} was not sent: {Message}", agent.Id, result.Message);
                continue;
            }
            agent.DataReviewNoticeSentAt = now;
            await _db.SaveChangesAsync();
            sent++;
        }
        return sent;
    }
}
