using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Hangfire;
using IPRO.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

// Watches TLS expiry on the platform's own names.
//
// WHY A WATCHDOG WHEN AZURE RENEWS THE CERTIFICATES ITSELF
// Since 2026-09-23 (TODO 505) every platform name carries an App Service managed certificate, which
// Azure renews on its own about 45 days before expiry. A renewal can still fail quietly: the July
// 2026 attempt never issued because the zone's CAA records did not allow DigiCert, and nothing said
// so. And what this guards is not portal-shaped: newsletter images resolve through
// https://app.iproadvisers.com/media/..., so an expired certificate breaks images in every
// newsletter, including mail already delivered, and nothing in the product reports it. Reading the
// live certificates every morning turns a silent non-renewal into a red row on the Job Scheduler
// dashboard and an email, a month before it matters. (A second copy of the check runs on the
// owner's machine, Check-CertExpiry.ps1, only while that machine is on.)
//
// WHY IT THROWS
// Hangfire has no "warning" state -- a job either succeeded or failed. Throwing after the email is
// sent is what turns an unrenewed certificate into a red row on the dashboard, which is the whole
// point of running it here. AutomaticRetry(Attempts = 0) keeps that from re-running and re-sending.
//
// 521 (2026-09-25): until then the red row and the email said "renew with the lego script on the
// other machine", the routine 505 retired. A watchdog that fires once in six months and then gives
// the wrong instruction is worse than silence: the text now says what to check, and the default
// list grew from the two hosts once renewed by hand to every platform name.
[AutomaticRetry(Attempts = 0)]
public class CertificateExpiryJob
{
    private static readonly string[] DefaultDomains =
    {
        "app.iproadvisers.com",     // newsletter image URLs resolve here on real sends
        "admin.iproadvisers.com",
        "www.iproadvisers.com",
        "iproadvisers.com",
        "www.iproaccountants.com",
        "iproaccountants.com",
        "www.ipromortgages.com",
        "ipromortgages.com"
    };

    // Deliberately NOT listed: advisers' custom domains. They carry the same kind of managed
    // certificate, but their list changes with every signup, and a failure there shows on one
    // adviser's site, not in every newsletter.

    private const int DefaultWarnDays = 30;

    internal static IReadOnlyList<string> DefaultWatchList => DefaultDomains;

    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CertificateExpiryJob> _logger;

    public CertificateExpiryJob(IEmailService email, IConfiguration configuration, ILogger<CertificateExpiryJob> logger)
    {
        _email = email;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        var domains = _configuration.GetSection("Certificates:Watch").Get<string[]>();
        if (domains == null || domains.Length == 0) domains = DefaultDomains;

        var warnDays = _configuration.GetValue<int?>("Certificates:WarnDays") ?? DefaultWarnDays;

        var problems = new List<string>();

        foreach (var domain in domains)
        {
            // Per-domain isolation, matching the other jobs: one unreachable host must not stop the
            // rest from being checked.
            try
            {
                var expiry = await GetExpiryAsync(domain);
                if (expiry == null)
                {
                    problems.Add($"{domain}: certificate could not be read");
                    _logger.LogWarning("Certificate check failed for {Domain}", domain);
                    continue;
                }

                var daysLeft = (int)Math.Floor((expiry.Value - DateTime.UtcNow).TotalDays);
                if (daysLeft <= warnDays)
                {
                    problems.Add($"{domain}: {daysLeft} days left (expires {expiry:yyyy-MM-dd})");
                    _logger.LogWarning("Certificate for {Domain} expires in {Days} days ({Expiry:u})", domain, daysLeft, expiry);
                }
                else
                {
                    _logger.LogInformation("Certificate for {Domain} has {Days} days remaining", domain, daysLeft);
                }
            }
            catch (Exception ex)
            {
                problems.Add($"{domain}: check threw -- {ex.Message}");
                _logger.LogWarning(ex, "Certificate check threw for {Domain}", domain);
            }
        }

        if (problems.Count == 0) return;

        await NotifyAsync(problems, warnDays);

        // Surfaces as a failed job on the Job Scheduler dashboard. See the class comment.
        throw new InvalidOperationException(FailureMessage(problems, warnDays));
    }

    // The red row's text. Static, so a test reads it without a network.
    internal static string FailureMessage(IReadOnlyCollection<string> problems, int warnDays) =>
        "The certificate check found a problem -- " + string.Join("; ", problems) +
        $". Azure renews these managed certificates itself about 45 days before expiry, so one inside {warnDays} days " +
        "means that renewal has not happened: check that the zone's CAA record still allows digicert.com and that " +
        "the certificate is still bound to the app. A host that could not be read may simply be down. " +
        "See DOCS/20_CERTIFICATES.md, \"When a managed certificate does not renew\".";

    private async Task NotifyAsync(IReadOnlyCollection<string> problems, int warnDays)
    {
        var to = ResolveAlertRecipient();
        if (string.IsNullOrWhiteSpace(to))
        {
            _logger.LogWarning("Certificate expiry alert had no deliverable recipient; skipping email");
            return;
        }

        var html = AlertHtml(problems, warnDays);

        try
        {
            // 454 (2026-09-11): a returned false is a refused send, not a thrown one; say so.
            var sent = await _email.SendAsync(to, "IPRO Operations", "A certificate needs attention", html);
            if (!sent) _logger.LogWarning("Certificate expiry alert email to {To} was not sent: the provider refused it", to);
        }
        catch (Exception ex)
        {
            // The thrown exception below is still the durable signal, so a mail failure must not
            // swallow the alert entirely.
            _logger.LogWarning(ex, "Could not send certificate expiry alert email");
        }
    }

    // The email's body. Static, so a test reads it without a network.
    internal static string AlertHtml(IReadOnlyCollection<string> problems, int warnDays)
    {
        var rows = string.Concat(problems.Select(p =>
            $"<li style=\"margin-bottom:6px\">{System.Net.WebUtility.HtmlEncode(p)}</li>"));

        return $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              <div style="padding:22px;background:#b42318;color:white">
                <h1 style="margin:0;font-size:22px">A certificate needs attention</h1>
              </div>
              <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                <ul style="padding-left:18px">{rows}</ul>
                <p style="margin-top:18px">Azure renews these certificates itself, about 45 days before they expire.
                  One inside {warnDays} days means that renewal has not happened. Check, in this order:</p>
                <ol style="padding-left:18px">
                  <li style="margin-bottom:6px">The domain's CAA record still allows DigiCert: <code>0 issue "digicert.com"</code>. Without it Azure cannot issue, and reports nothing.</li>
                  <li style="margin-bottom:6px">In the Azure portal, the managed certificate for that host: its status, and that it is still bound to the site.</li>
                  <li style="margin-bottom:6px">DOCS/20_CERTIFICATES.md, "When a managed certificate does not renew".</li>
                </ol>
                <p style="color:#475569;font-size:13px;margin-top:18px">
                  A host that could not be read at all may simply be down: check the site first.
                  Newsletter images are served from app.iproadvisers.com/media. If that certificate lapses,
                  images break in newsletters that have already been delivered, and nothing in the
                  portal will report it.
                </p>
              </div>
            </div>
            """;
    }

    // Email:NotificationEmail ships as a CHANGE_THIS_ placeholder and is not set in Azure, so
    // trusting it blindly would send alerts into the void. Fall through to the From address, which
    // is real and monitored.
    private string? ResolveAlertRecipient()
    {
        var candidates = new[]
        {
            _configuration["Certificates:AlertEmail"],
            _configuration["Email:NotificationEmail"],
            _configuration["Email:FromEmail"]
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (candidate.Contains("CHANGE_THIS", StringComparison.OrdinalIgnoreCase)) continue;
            if (!candidate.Contains('@')) continue;
            return candidate;
        }
        return null;
    }

    private static async Task<DateTime?> GetExpiryAsync(string host)
    {
        // Raw handshake rather than an HTTP call: this has to report the certificate even when the
        // site is down, mid-deploy, or returning 5xx. The validation callback accepts everything
        // because the certificate is being inspected, not trusted -- an already-expired one still
        // has to be readable here.
        using var client = new TcpClient();
        var connect = client.ConnectAsync(host, 443);
        if (await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(15))) != connect) return null;
        await connect;

        using var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(host);

        if (ssl.RemoteCertificate == null) return null;
        using var cert = new X509Certificate2(ssl.RemoteCertificate);
        return cert.NotAfter.ToUniversalTime();
    }
}
