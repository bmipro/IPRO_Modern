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
    private readonly IAzureDomainAutomationService? _azureDomains;

    public DomainAutomationJob(
        IPRODbContext db,
        IDomainCheckService domainCheck,
        IEmailService email,
        IConfiguration configuration,
        ILogger<DomainAutomationJob> logger,
        IAzureDomainAutomationService? azureDomains = null)
    {
        _db = db;
        _domainCheck = domainCheck;
        _email = email;
        _configuration = configuration;
        _logger = logger;
        _azureDomains = azureDomains;
    }

    // 592: one run at a time. A run asks DNS resolvers and Azure, each with a timeout, for up to 75 rows; a slow
    // run must not overlap the next one and ask Azure for the same removals twice.
    // No automatic retries either: the next run is five minutes away, and a run that waited for the lock and gave
    // up would otherwise be queued again, up to ten times.
    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 10 * 60)]
    [Hangfire.AutomaticRetry(Attempts = 0)]
    public async Task RunAsync()
    {
        // 592: first, the addresses that stopped being an adviser's and are still to be removed from Azure.
        if (_azureDomains != null)
        {
            try
            {
                await DomainCleanupQueue.RunDueAsync(_db, _azureDomains, _configuration, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Removing released domain addresses from Azure failed this run; the next run asks again");
            }
        }

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

        // 553: a short address pointed straight at the platform is bound in one pass and secured in
        // the next, like the www name -- so while its certificate is being issued the domain is back
        // on the every-run path even though the www half is finished. Only for the grace period: a
        // certificate that has not arrived by then is an alert, not something a 5-minute retry fixes.
        var rootCertificateCutoff = now - CertificateGracePeriod;

        var domains = await _db.AgentDomains
            .Where(d =>
                (
                    d.RootDnsStatus == AgentDomainStatus.Bound &&
                    d.RootAzureBindingStatus == AgentDomainStatus.Bound &&
                    d.RootSslStatus != AgentDomainStatus.Bound &&
                    d.RootBoundAt != null && d.RootBoundAt > rootCertificateCutoff
                )
                ||
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
                // 592: saved one by one. A row removed while it was being checked (its adviser's Remove, or another
                // adviser proving the name) must not cost the other rows of the run their results.
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(ex, "Domain {DomainId} was removed while it was being checked; its result is dropped", domain.Id);
                _db.Entry(domain).State = EntityState.Detached;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Domain automation check failed for domain {DomainId}", domain.Id);
                // 592: left tracked, its unsaved changes would fail every later save of this run.
                _db.Entry(domain).State = EntityState.Detached;
            }
        }

        await _db.SaveChangesAsync();

        // 595: and the advisers hear how their domains are doing.
        try
        {
            await SendSetupEmailsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Domain setup emails failed this run; the next run tries again");
        }
    }

    // 595: the adviser is emailed when a domain is live, and once if it is still not connected a day after it was added,
    // with what is missing (the guide's own words). Each at most once per domain, stamped before sending as the
    // certificate alert is: a slow or failing mail provider must not turn into a repeating email. A few per run.
    internal static readonly TimeSpan StuckAfter = TimeSpan.FromHours(24);

    private async Task SendSetupEmailsAsync()
    {
        var now = DateTime.UtcNow;
        var settings = DomainSetupGuide.Settings.From(_configuration);
        var myWebsite = WebAppUrlHelper.GetWebAppBaseUrl(_configuration).TrimEnd('/') + PortalPaths.To("/Website/Domains");

        var live = await _db.AgentDomains
            .Where(d => d.LiveEmailSentAt == null && d.OwnershipVerifiedAt != null && d.AzureBindingStatus == AgentDomainStatus.Bound &&
                        (d.SslStatus == AgentDomainStatus.Bound || d.SslStatus == "SslBound"))
            .OrderBy(d => d.Id).Take(20).ToListAsync();
        foreach (var domain in live)
        {
            domain.LiveEmailSentAt = now;
            await _db.SaveChangesAsync();
            var www = WebUtility.HtmlEncode(domain.DomainName);
            var shortAddress = ShortAddressState.HasShortAddress(domain)
                ? ShortAddressState.Describe(domain).Working
                    ? $"<p>{WebUtility.HtmlEncode(domain.RootDomain)} (without www) opens it too.</p>"
                    : $"<p>Optional: {WebUtility.HtmlEncode(domain.RootDomain)} (without www) does not open your site yet. Step 3 of the guide on your <a href=\"{WebUtility.HtmlEncode(myWebsite)}\">Domain page</a> shows how.</p>"
                : string.Empty;
            await SendToAdviserAsync(domain, $"{domain.DomainName} is live", $"""
                <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
                  <h1 style="font-size:22px">{www} is live</h1>
                  <p>Good news: your website now opens at <a href="https://{www}">https://{www}</a>, with its padlock.</p>
                  {shortAddress}
                  <p>There is nothing more to do. The padlock renews itself.</p>
                </div>
                """);
        }

        var cutoff = now - StuckAfter;
        var stuck = await _db.AgentDomains
            .Where(d => d.StuckEmailSentAt == null && d.LiveEmailSentAt == null && d.CreatedAt < cutoff &&
                        !(d.OwnershipVerifiedAt != null && d.AzureBindingStatus == AgentDomainStatus.Bound &&
                          (d.SslStatus == AgentDomainStatus.Bound || d.SslStatus == "SslBound")))
            .OrderBy(d => d.CreatedAt).Take(5).ToListAsync();
        foreach (var domain in stuck)
        {
            domain.StuckEmailSentAt = now;
            await _db.SaveChangesAsync();
            // At most ten seconds for the look (595's review, L-i); without it the email says what is recorded.
            DomainSetupLook? look;
            using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                try { look = await _domainCheck.LookAsync(domain, budget.Token).WaitAsync(budget.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { look = null; }
            }
            var guide = DomainSetupGuide.Describe(domain, look, settings, now);
            var missing = string.Concat(DomainSetupGuide.StillToDo(guide).Select(item => $"<li style=\"margin-bottom:8px\">{WebUtility.HtmlEncode(item)}</li>"));
            var www = WebUtility.HtmlEncode(domain.DomainName);
            await SendToAdviserAsync(domain, $"{domain.DomainName} is not connected yet", $"""
                <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
                  <h1 style="font-size:22px">{www} is not connected yet</h1>
                  <p>You added {www} on {domain.CreatedAt.ToString("MMMM d", System.Globalization.CultureInfo.InvariantCulture)}. Your website keeps working at its temporary address meanwhile. This is what is still missing:</p>
                  <ul>{missing}</ul>
                  <p>The guide on your <a href="{WebUtility.HtmlEncode(myWebsite)}">Domain page</a> (My Website, then Domain) shows each record with Copy buttons, and ticks each step as soon as it sees it.</p>
                  <p>Would you rather we help? Click <strong>I need help</strong> in the guide: it sends us what we see for each record.</p>
                </div>
                """);
        }
    }

    private async Task SendToAdviserAsync(AgentDomain domain, string subject, string html)
    {
        // An account that is switched off hears nothing (595's review, L11).
        var adviser = await _db.AgentUsers.AsNoTracking().Where(a => a.Id == domain.AgentUserId && a.IsActive)
            .Select(a => new { a.Email, Name = a.FirstName + " " + a.LastName }).FirstOrDefaultAsync();
        if (adviser == null || string.IsNullOrWhiteSpace(adviser.Email)) return;
        try
        {
            var sent = await _email.SendAsync(adviser.Email, adviser.Name.Trim(), subject, html);
            if (!sent) _logger.LogWarning("Domain email '{Subject}' to agent {AgentUserId} was not sent: the provider refused it", subject, domain.AgentUserId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not send domain email '{Subject}' to agent {AgentUserId}", subject, domain.AgentUserId);
        }
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

    // 553: which of the domain's two names is waiting for a certificate, and whether the wait is
    // past the grace period. The short address counts from the moment it was bound (RootBoundAt): it
    // is usually connected long after the row was created, so CreatedAt would alert at once.
    internal static (bool Waiting, string? OverdueHost) CertificateWait(AgentDomain domain, DateTime utcNow)
    {
        var wwwWaiting =
            domain.AzureBindingStatus == AgentDomainStatus.Bound &&
            domain.SslStatus != AgentDomainStatus.Bound;
        var rootWaiting =
            domain.RootDnsStatus == AgentDomainStatus.Bound &&
            domain.RootAzureBindingStatus == AgentDomainStatus.Bound &&
            domain.RootSslStatus != AgentDomainStatus.Bound;

        string? overdue = null;
        // 595: from when the www name was connected (CertificateClock), not from when the row was added.
        if (wwwWaiting && utcNow - CertificateClock.StartOf(domain) >= CertificateGracePeriod)
        {
            overdue = domain.DomainName;
        }
        else if (rootWaiting && domain.RootBoundAt.HasValue && utcNow - domain.RootBoundAt.Value >= CertificateGracePeriod)
        {
            overdue = domain.RootDomain;
        }

        return (wwwWaiting || rootWaiting, overdue);
    }

    private async Task AlertIfCertificateIsOverdueAsync(AgentDomain domain)
    {
        var (waiting, overdueHost) = CertificateWait(domain, DateTime.UtcNow);

        if (!waiting)
        {
            // Re-arm, so a certificate that lapses later alerts again rather than staying silent.
            domain.CertificateAlertSentAt = null;
            return;
        }

        // Still inside the window where Azure is simply working on it.
        if (overdueHost == null)
        {
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

        // 591: a certificate ordered since 591 has a name of its own; one ordered before keeps the old name.
        var certificateName = IPRO.Utility.ManagedCertificateNames.For(overdueHost);
        var olderCertificateName = IPRO.Utility.ManagedCertificateNames.Legacy(overdueHost);
        _logger.LogWarning(
            "Domain {Domain} (agent {AgentUserId}) has been bound with no certificate for over {Hours}h. " +
            "Managed certificate issuance appears stuck; check the {CertificateName} certificate resource in Azure (ordered before 591: {OlderCertificateName})",
            overdueHost, domain.AgentUserId, CertificateGracePeriod.TotalHours,
            certificateName, olderCertificateName);

        var to = _configuration["Email:NotificationEmail"];
        if (string.IsNullOrWhiteSpace(to) || to.StartsWith("CHANGE_THIS_", StringComparison.OrdinalIgnoreCase))
        {
            // Matches CertificateExpiryJob: the configured notification address ships as a
            // placeholder and is not set in Azure, so fall through to the verified From address.
            to = _configuration["Email:FromEmail"];
        }

        if (string.IsNullOrWhiteSpace(to))
        {
            _logger.LogWarning("Certificate alert for {Domain} had no deliverable recipient", overdueHost);
            return;
        }

        var agentName = await _db.AgentUsers
            .Where(a => a.Id == domain.AgentUserId)
            .Select(a => a.FirstName + " " + a.LastName + " (" + a.Email + ")")
            .FirstOrDefaultAsync() ?? ("agent #" + domain.AgentUserId);

        var host = WebUtility.HtmlEncode(overdueHost);
        // 506: when the domain's CAA records keep DigiCert out, that is the whole story.
        var caaNote = string.IsNullOrEmpty(domain.CaaBlockingName)
            ? string.Empty
            : $"<p style=\"background:#fff7ed;border-left:3px solid #c2410c;padding:12px\"><strong>Likely cause: CAA.</strong> The CAA records at <strong>{WebUtility.HtmlEncode(domain.CaaBlockingName)}</strong> do not list {IPRO.Utility.CaaCheck.Authority}, so the managed certificate cannot be issued. The adviser is shown the record to add on My Website (<code>{WebUtility.HtmlEncode(IPRO.Utility.CaaCheck.RecordToAdd)}</code>); nothing below will help until it is there.</p>";
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
                {caaNote}
                <p style="background:#fef3f2;border-left:3px solid #b42318;padding:12px">
                  Their website is <strong>unreachable</strong> — the site is HTTPS-only and is serving a
                  certificate for the wrong name, so browsers block it.
                </p>
                <p><strong>Check this first.</strong> The managed certificate may exist and simply not be bound:</p>
                <pre style="background:#f6f8fb;padding:12px;border-radius:6px;font-size:13px">az resource show --ids /subscriptions/&lt;sub&gt;/resourceGroups/&lt;rg&gt;/providers/Microsoft.Web/certificates/{WebUtility.HtmlEncode(certificateName)} --query "[properties.canonicalName, properties.thumbprint]"</pre>
                <p style="color:#475569;font-size:13px">
                  A certificate ordered by an older version of IPRO has the name <code>{WebUtility.HtmlEncode(olderCertificateName)}</code> instead.
                  Either one is this domain's only when its canonicalName is <strong>{host}</strong>.
                </p>
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
            // 454 (2026-09-11): a returned false is a refused send, not a thrown one; say so.
            var sent = await _email.SendAsync(to, "IPRO Operations", $"Domain needs a certificate: {overdueHost}", html);
            if (!sent) _logger.LogWarning("Certificate alert for {Domain} to {To} was not sent: the provider refused it", overdueHost, to);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not send certificate alert for {Domain}", overdueHost);
        }
    }
}
