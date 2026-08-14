using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

public class TrialReminderJob
{
    private readonly IPRODbContext _db;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TrialReminderJob> _logger;

    public TrialReminderJob(IPRODbContext db, IEmailService email, IConfiguration configuration, ILogger<TrialReminderJob> logger)
    {
        _db = db;
        _email = email;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        var today = DateTime.UtcNow.Date;

        var trialAgents = await _db.AgentUsers
            .Where(a => a.IsActive && a.TrialEndsAt != null)
            .ToListAsync();
        if (trialAgents.Count == 0) return;

        var agentIds = trialAgents.Select(a => a.Id).ToList();
        var subscribedAgentIds = (await _db.Billings
            .Where(b => agentIds.Contains(b.AgentUserId) && b.Status == BillingStatus.Active)
            .Select(b => b.AgentUserId)
            .ToListAsync())
            .ToHashSet();

        var settings = await _db.TrialSettings.FirstOrDefaultAsync(s => s.Id == 1);
        var graceDays = settings?.GracePeriodDays ?? 1;

        var packagesById = (await _db.BillingRules.ToListAsync()).ToDictionary(p => p.Id);

        foreach (var agent in trialAgents)
        {
            if (subscribedAgentIds.Contains(agent.Id)) continue;

            try
            {
                var trialEndsAt = agent.TrialEndsAt!.Value.Date;
                var daysRemaining = (trialEndsAt - today).Days;

                // One shared, monotonically-increasing "stage" counter covers all three kinds of
                // notification (pre-expiry reminders, trial-ended, grace-ended) using the same
                // catch-up-safe comparison already used for the reminders alone: if a run is missed
                // and multiple stages become due at once, exactly one email fires -- reflecting the
                // most current state -- rather than none (a bare "==" check would silently skip it
                // forever) or a replay of every missed intermediate stage.
                var offsets = ParseOffsets(packagesById.TryGetValue(agent.PackageId, out var package) ? package.TrialReminderDayOffsets : null);
                var reminderDue = offsets.Count(o => daysRemaining <= o);
                var trialEndedDue = daysRemaining <= 0 ? 1 : 0;
                var graceEndedDue = -daysRemaining >= graceDays ? 1 : 0;
                var dueCount = reminderDue + trialEndedDue + graceEndedDue;

                if (dueCount > agent.TrialRemindersSentCount)
                {
                    if (graceEndedDue == 1) await SendGraceEndedEmailAsync(agent);
                    else if (trialEndedDue == 1) await SendTrialEndedEmailAsync(agent, graceDays);
                    else await SendReminderEmailAsync(agent, daysRemaining);
                    agent.TrialRemindersSentCount = dueCount;

                    // Persist THIS agent's counter immediately. Saving once after the loop meant a
                    // transient failure at the end discarded every increment in the batch, and
                    // Hangfire's default retry (up to 10 attempts) then re-sent trial reminders to
                    // everyone already mailed -- the counter is the only thing preventing a replay
                    // (2026-08-14 ultra-audit).
                    await _db.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Trial reminder processing failed for agent {AgentId}", agent.Id);
            }
        }
    }

    private static List<int> ParseOffsets(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<int>();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : (int?)null)
            .Where(n => n.HasValue && n.Value > 0)
            .Select(n => n!.Value)
            .Distinct()
            .OrderByDescending(n => n)
            .ToList();
    }

    private async Task SendReminderEmailAsync(AgentUser agent, int daysRemaining)
    {
        var url = BuildBillingUrl();
        var dayWord = daysRemaining == 1 ? "day" : "days";
        var html = $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              <div style="padding:22px;background:#1457d9;color:white"><h1 style="margin:0;font-size:24px">IPRO Advisers</h1></div>
              <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                <p>Your free trial ends in <strong>{daysRemaining} {dayWord}</strong>.</p>
                <p>Subscribe any time before then to keep uninterrupted access to your portal.</p>
                <p><a href="{url}" style="display:inline-block;padding:11px 18px;background:#1457d9;color:white;text-decoration:none;border-radius:6px">Subscribe Now</a></p>
              </div>
            </div>
            """;
        await _email.SendDetailedAsync(agent.Email, $"{agent.FirstName} {agent.LastName}".Trim(),
            $"Your IPRO trial ends in {daysRemaining} {dayWord}", html);
    }

    private async Task SendTrialEndedEmailAsync(AgentUser agent, int graceDays)
    {
        var url = BuildBillingUrl();
        var graceWord = graceDays == 1 ? "day" : "days";
        var html = $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              <div style="padding:22px;background:#1457d9;color:white"><h1 style="margin:0;font-size:24px">IPRO Advisers</h1></div>
              <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                <p>Your free trial has ended. You have <strong>{graceDays} {graceWord}</strong> to subscribe before your portal access is limited to Billing &amp; Subscription.</p>
                <p><a href="{url}" style="display:inline-block;padding:11px 18px;background:#1457d9;color:white;text-decoration:none;border-radius:6px">Subscribe Now</a></p>
              </div>
            </div>
            """;
        await _email.SendDetailedAsync(agent.Email, $"{agent.FirstName} {agent.LastName}".Trim(),
            "Your IPRO trial has ended", html);
    }

    private async Task SendGraceEndedEmailAsync(AgentUser agent)
    {
        var url = BuildBillingUrl();
        var html = $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              <div style="padding:22px;background:#c9182b;color:white"><h1 style="margin:0;font-size:24px">IPRO Advisers</h1></div>
              <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                <p>Your portal access is now limited until you subscribe. Everything else is paused, but your data is safe and waiting.</p>
                <p><a href="{url}" style="display:inline-block;padding:11px 18px;background:#1457d9;color:white;text-decoration:none;border-radius:6px">Subscribe Now</a></p>
              </div>
            </div>
            """;
        await _email.SendDetailedAsync(agent.Email, $"{agent.FirstName} {agent.LastName}".Trim(),
            "Your IPRO portal access is now limited", html);
    }

    private string BuildBillingUrl()
    {
        var baseUrl = _configuration["App:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl) || baseUrl.Contains("yourdomain.com", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = "https://ipro-prod-web.azurewebsites.net";
        }

        return $"{baseUrl.TrimEnd('/')}/Billing";
    }
}
