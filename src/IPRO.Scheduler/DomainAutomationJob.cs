using System.Net;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

public class DomainAutomationJob
{
    private readonly IPRODbContext _db;
    private readonly IDomainCheckService _domainCheck;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DomainAutomationJob> _logger;

    public DomainAutomationJob(
        IPRODbContext db,
        IDomainCheckService domainCheck,
        IEmailService email,
        IConfiguration configuration,
        ILogger<DomainAutomationJob> logger)
    {
        _db = db;
        _domainCheck = domainCheck;
        _email = email;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        var now = DateTime.UtcNow;
        var domains = await _db.AgentDomains
            .Where(d => (d.DnsStatus != AgentDomainStatus.Bound ||
                         d.AzureBindingStatus != AgentDomainStatus.Bound ||
                         d.SslStatus != AgentDomainStatus.Bound) &&
                        !d.AutoRetryExhausted &&
                        (d.NextRetryAt == null || d.NextRetryAt <= now))
            .OrderBy(d => d.LastCheckedAt ?? d.CreatedAt)
            .Take(50)
            .ToListAsync();

        foreach (var domain in domains)
        {
            try
            {
                await _domainCheck.CheckAsync(domain);

                // The agent is told in the portal that we have been alerted automatically. This is
                // what makes that true -- without it the promise is a lie and their only recourse is
                // a support call, which is exactly what the message is meant to prevent.
                await AlertIfBoundWithoutCertificateAsync(domain);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Domain automation check failed for domain {DomainId}", domain.Id);
            }
        }

        await _db.SaveChangesAsync();
    }

    // Bound + no certificate is the one state the agent cannot fix and automation cannot clear:
    // App Service is HTTPS-only, so the site serves a certificate for the wrong name and every
    // browser blocks it outright. It needs a human to run ops/New-AgentCert.ps1.
    private async Task AlertIfBoundWithoutCertificateAsync(AgentDomain domain)
    {
        var boundWithoutCertificate =
            domain.AzureBindingStatus == AgentDomainStatus.Bound &&
            domain.SslStatus != AgentDomainStatus.Bound;

        if (!boundWithoutCertificate)
        {
            // Re-arm, so a certificate that lapses later alerts again rather than staying silent.
            domain.CertificateAlertSentAt = null;
            return;
        }

        if (domain.CertificateAlertSentAt.HasValue)
        {
            return;
        }

        // Stamped before sending: this loop runs every 5 minutes, and a mail provider that is slow
        // or throwing must not turn one broken domain into a repeating alert. The log line below is
        // the durable record either way.
        domain.CertificateAlertSentAt = DateTime.UtcNow;

        _logger.LogWarning(
            "Domain {Domain} (agent {AgentUserId}) is bound with no certificate. The site is " +
            "unreachable until issued: ops/New-AgentCert.ps1 -Domain {Domain}",
            domain.DomainName, domain.AgentUserId, domain.DomainName);

        var to = _configuration["Email:NotificationEmail"];
        if (string.IsNullOrWhiteSpace(to) || to.StartsWith("CHANGE_THIS_", StringComparison.OrdinalIgnoreCase))
        {
            // Matches CertificateExpiryJob: the configured notification address ships as a
            // placeholder and is not set in Azure, so fall through to the verified From address.
            to = _configuration["Email:FromEmail"];
        }

        if (string.IsNullOrWhiteSpace(to))
        {
            _logger.LogWarning("Certificate alert for {Domain} had no deliverable recipient", domain.DomainName);
            return;
        }

        var agentName = await _db.AgentUsers
            .Where(a => a.Id == domain.AgentUserId)
            .Select(a => a.FirstName + " " + a.LastName + " (" + a.Email + ")")
            .FirstOrDefaultAsync() ?? ("agent #" + domain.AgentUserId);

        var host = WebUtility.HtmlEncode(domain.DomainName);
        var html = $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              <div style="padding:22px;background:#b42318;color:white">
                <h1 style="margin:0;font-size:22px">Custom domain needs a certificate</h1>
              </div>
              <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                <p style="margin-top:0"><strong>{host}</strong> is bound in Azure but has no SSL certificate.</p>
                <p>Agent: {WebUtility.HtmlEncode(agentName)}</p>
                <p style="background:#fef3f2;border-left:3px solid #b42318;padding:12px">
                  Their website is <strong>unreachable right now</strong> — visitors get a browser
                  security warning, because the site is HTTPS-only and is serving a certificate for
                  the wrong name.
                </p>
                <p>Issue one from the maintenance machine:</p>
                <pre style="background:#f6f8fb;padding:12px;border-radius:6px;font-size:13px">powershell -File C:\Users\admin\lego\New-AgentCert.ps1 -Domain {host}</pre>
                <p style="color:#475569;font-size:13px">
                  It prints a DNS TXT record to publish at <em>the agent's</em> registrar, then uploads
                  and binds the certificate. Afterwards add {host} to the $Domains list in
                  Check-CertExpiry.ps1 and to Certificates:Watch so renewal is monitored.
                </p>
                <p style="color:#475569;font-size:13px">
                  The agent has been told we were alerted automatically and that it will be secured
                  within one business day.
                </p>
              </div>
            </div>
            """;

        try
        {
            await _email.SendAsync(to, "IPRO Operations", $"Domain needs a certificate: {domain.DomainName}", html);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not send certificate alert for {Domain}", domain.DomainName);
        }
    }
}
