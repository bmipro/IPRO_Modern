using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Hangfire;
using IPRO.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

// Watches TLS expiry on the domains we renew by hand.
//
// WHY THIS IS A JOB AND NOT JUST A SCHEDULED TASK ON SOMEONE'S PC
// There is a Windows scheduled task doing the same check (C:\Users\admin\lego\Check-CertExpiry.ps1),
// but it only runs while that machine is on and logged in. The thing it guards is not laptop-shaped:
// newsletter images resolve through https://app.iproadvisers.com/media/..., so an expired certificate
// breaks images in every newsletter, including mail already delivered, and nothing in the product
// reports it. Running the check in Azure means the warning arrives whether or not anyone is at a desk,
// and it shows up in the Job Scheduler dashboard alongside every other recurring job.
//
// Renewal deliberately stays local: it needs lego, the ACME account key, and a DNS TXT record
// published by hand. See DOCS/20_CERTIFICATES.md.
//
// WHY IT THROWS
// Hangfire has no "warning" state -- a job either succeeded or failed. Throwing after the email is
// sent is what turns a due certificate into a red row on the dashboard, which is the whole point of
// running it here. AutomaticRetry(Attempts = 0) keeps that from re-running and re-sending.
[AutomaticRetry(Attempts = 0)]
public class CertificateExpiryJob
{
    private static readonly string[] DefaultDomains =
    {
        "app.iproadvisers.com",     // newsletter image URLs resolve here on real sends
        "admin.iproadvisers.com"
    };

    private const int DefaultWarnDays = 30;

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

        await NotifyAsync(problems);

        // Surfaces as a failed job on the Job Scheduler dashboard. See the class comment.
        throw new InvalidOperationException(
            "Certificate renewal is due -- " + string.Join("; ", problems) +
            ". Renew with Renew-Certs.ps1 on the maintenance machine (see DOCS/20_CERTIFICATES.md).");
    }

    private async Task NotifyAsync(IReadOnlyCollection<string> problems)
    {
        var to = ResolveAlertRecipient();
        if (string.IsNullOrWhiteSpace(to))
        {
            _logger.LogWarning("Certificate expiry alert had no deliverable recipient; skipping email");
            return;
        }

        var rows = string.Concat(problems.Select(p =>
            $"<li style=\"margin-bottom:6px\">{System.Net.WebUtility.HtmlEncode(p)}</li>"));

        var html = $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              <div style="padding:22px;background:#b42318;color:white">
                <h1 style="margin:0;font-size:22px">Certificate renewal due</h1>
              </div>
              <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                <ul style="padding-left:18px">{rows}</ul>
                <p style="margin-top:18px">Renew from the maintenance machine:</p>
                <pre style="background:#f6f8fb;padding:12px;border-radius:6px;font-size:13px">powershell -File C:\Users\admin\lego\Renew-Certs.ps1</pre>
                <p style="color:#475569;font-size:13px;margin-top:18px">
                  Newsletter images are served from app.iproadvisers.com. If that certificate lapses,
                  images break in newsletters that have already been delivered, and nothing in the
                  portal will report it.
                </p>
              </div>
            </div>
            """;

        try
        {
            await _email.SendAsync(to, "IPRO Operations", "Certificate renewal due", html);
        }
        catch (Exception ex)
        {
            // The thrown exception below is still the durable signal, so a mail failure must not
            // swallow the alert entirely.
            _logger.LogWarning(ex, "Could not send certificate expiry alert email");
        }
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
