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

        // Fully-bound domains used to be excluded outright, which froze the root-domain forwarding
        // status at whatever it was the moment the certificate landed. An agent who set up
        // forwarding afterwards -- the normal order, since the portal only tells them about it once
        // the domain is added -- was shown "Not forwarding" indefinitely, with a Last checked time
        // hours or days old. Reported on ouritems.ca and 411trades.com, both of which were
        // forwarding correctly while the portal said otherwise.
        //
        // So bound domains are still re-checked, just less often: the work is one DNS lookup and one
        // HTTP request, and it also catches a domain that silently stops working later.
        //
        // 30 minutes, not hours. The common case is an agent who has just set up forwarding and is
        // looking at the screen right now -- a 6-hour window meant they saw a stale "Not forwarding"
        // for the rest of the working day, which is barely better than never re-checking. Even with
        // hundreds of domains this is a handful of lookups per run.
        var boundRecheckCutoff = now.AddMinutes(-30);

        var domains = await _db.AgentDomains
            .Where(d =>
                (
                    // Still being set up -- the fast path, every run.
                    (d.DnsStatus != AgentDomainStatus.Bound ||
                     d.AzureBindingStatus != AgentDomainStatus.Bound ||
                     d.SslStatus != AgentDomainStatus.Bound) &&
                    !d.AutoRetryExhausted &&
                    (d.NextRetryAt == null || d.NextRetryAt <= now)
                )
                ||
                (
                    // Fully bound -- revisit every 6 hours so forwarding status stays true.
                    d.DnsStatus == AgentDomainStatus.Bound &&
                    d.AzureBindingStatus == AgentDomainStatus.Bound &&
                    d.SslStatus == AgentDomainStatus.Bound &&
                    (d.LastCheckedAt == null || d.LastCheckedAt < boundRecheckCutoff)
                ))
            .OrderBy(d => d.LastCheckedAt ?? d.CreatedAt)
            .Take(50)
            .ToListAsync();

        foreach (var domain in domains)
        {
            try
            {
                await _domainCheck.CheckAsync(domain);
                await AlertIfCertificateIsOverdueAsync(domain);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Domain automation check failed for domain {DomainId}", domain.Id);
            }
        }

        await _db.SaveChangesAsync();
    }

    // Bound + no certificate is NORMAL for the first few minutes: Azure issues managed certificates
    // asynchronously and the next check binds them. So this alerts only when that has clearly not
    // happened -- the domain has been bound without a certificate for hours, which means issuance is
    // genuinely stuck rather than merely in flight.
    //
    // The threshold matters. An earlier version of this alerted immediately, which would have fired
    // on every single new domain during perfectly normal issuance and trained whoever reads it to
    // ignore the mail.
    private static readonly TimeSpan CertificateGracePeriod = TimeSpan.FromHours(3);

    private async Task AlertIfCertificateIsOverdueAsync(AgentDomain domain)
    {
        var boundWithoutCertificate =
            domain.AzureBindingStatus == AgentDomainStatus.Bound &&
            domain.SslStatus != AgentDomainStatus.Bound;

        // Still inside the window where Azure is simply working on it.
        if (boundWithoutCertificate && DateTime.UtcNow - domain.CreatedAt < CertificateGracePeriod)
        {
            return;
        }

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
            "Domain {Domain} (agent {AgentUserId}) has been bound with no certificate for over {Hours}h. " +
            "Managed certificate issuance appears stuck; check the managed-{Slug} certificate resource in Azure",
            domain.DomainName, domain.AgentUserId, CertificateGracePeriod.TotalHours,
            domain.DomainName.Replace('.', '-'));

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
                <h1 style="margin:0;font-size:22px">Certificate issuance looks stuck</h1>
              </div>
              <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                <p style="margin-top:0">
                  <strong>{host}</strong> has been bound in Azure for over {CertificateGracePeriod.TotalHours} hours
                  with no SSL certificate. Normally the managed certificate issues within minutes and
                  binds itself, so this is not the usual delay.
                </p>
                <p>Agent: {WebUtility.HtmlEncode(agentName)}</p>
                <p style="background:#fef3f2;border-left:3px solid #b42318;padding:12px">
                  Their website is <strong>unreachable</strong> — the site is HTTPS-only and is serving a
                  certificate for the wrong name, so browsers block it.
                </p>
                <p><strong>Check this first.</strong> The managed certificate may exist and simply not be bound:</p>
                <pre style="background:#f6f8fb;padding:12px;border-radius:6px;font-size:13px">az resource show --ids /subscriptions/&lt;sub&gt;/resourceGroups/&lt;rg&gt;/providers/Microsoft.Web/certificates/managed-{host.Replace('.', '-')} --query properties.thumbprint</pre>
                <p style="color:#475569;font-size:13px">
                  If it has a thumbprint, bind it — do not issue a new one. On 2026-08-06 a perfectly
                  good auto-renewing managed certificate was displaced by a hand-issued Let's Encrypt
                  one because this check was skipped.
                </p>
                <p style="color:#475569;font-size:13px">
                  Only if no managed certificate exists, fall back to
                  <code>ops/New-AgentCert.ps1 -Domain {host}</code>, and add the domain to the expiry
                  watch lists afterwards since that one does not auto-renew.
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
