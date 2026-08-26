using System.Net;
using IPRO.Entities;
using Microsoft.Extensions.Logging;

namespace IPRO.Utility;

public class DomainCheckService : IDomainCheckService
{
    // Deliberately NOT from IHttpClientFactory: the factory's clients follow redirects, and the root
    // check needs to inspect the first hop rather than the destination. Static because a handler per
    // call would leak sockets; this one is called a few times per 5-minute job run.
    private static readonly HttpClient NoRedirectClient = CreateNoRedirectClient();

    private static HttpClient CreateNoRedirectClient()
    {
        // H4: the pinned handler validates the RESOLVED addresses at connect time, atomically --
        // the pre-checks above/below stay for fast, friendly error messages, but the security
        // boundary is here, where DNS rebinding between check and fetch can no longer win.
        var handler = PublicHostGuard.CreatePinnedHandler();
        handler.AllowAutoRedirect = false;
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        // A User-Agent is REQUIRED here, not cosmetic. HttpClient sends none by default, and
        // GoDaddy's domain-forwarding service answers a request with no User-Agent with 403
        // Forbidden instead of the 301 it gives a browser. Verified 2026-08-06 against ouritems.ca
        // and 411trades.com: UA present => 301 to the www host, UA absent => 403.
        //
        // The 403 is not a redirect, so the check concluded "not forwarding" and told two agents
        // their correctly-configured domains were broken. Registrar forwarding services sit behind
        // bot protection; anything probing them has to look like an ordinary client.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; IPRO-DomainCheck/1.0; +https://app.iproadvisers.com)");

        return client;
    }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAzureDomainAutomationService _azureDomains;
    private readonly ILogger<DomainCheckService> _logger;

    public DomainCheckService(
        IHttpClientFactory httpClientFactory,
        IAzureDomainAutomationService azureDomains,
        ILogger<DomainCheckService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _azureDomains = azureDomains;
        _logger = logger;
    }

    public async Task<bool> CheckAsync(AgentDomain domain, CancellationToken cancellationToken = default)
    {
        domain.LastCheckedAt = DateTime.UtcNow;
        domain.UpdatedAt = DateTime.UtcNow;
        domain.LastError = string.Empty;

        await CheckDomainAsync(domain, cancellationToken);
        await CheckRootDomainAsync(domain, cancellationToken);

        var fullyBound = domain.DnsStatus == AgentDomainStatus.Bound &&
                          domain.AzureBindingStatus == AgentDomainStatus.Bound &&
                          domain.SslStatus == AgentDomainStatus.Bound;

        if (fullyBound)
        {
            domain.RetryCount = 0;
            domain.AutoRetryExhausted = false;
            domain.NextRetryAt = null;
            domain.LastFailedAt = null;
        }
        else
        {
            domain.RetryCount++;
            domain.LastFailedAt = DateTime.UtcNow;
            domain.NextRetryAt = domain.RetryCount switch
            {
                <= 11 => null,
                <= 17 => DateTime.UtcNow.AddMinutes(30),
                <= 41 => DateTime.UtcNow.AddHours(4),
                _ => null
            };
            domain.AutoRetryExhausted = domain.RetryCount > 41;
        }

        return fullyBound;
    }

    private async Task CheckDomainAsync(AgentDomain domain, CancellationToken cancellationToken)
    {
        try
        {
            // A5-M-SSRF: an IP-literal "domain" is refused before we even resolve it.
            if (PublicHostGuard.IsBlockedHost(domain.DomainName))
            {
                domain.DnsStatus = AgentDomainStatus.Failed;
                domain.LastError = "This is not a public domain name, so it cannot be used as a website address.";
                return;
            }

            var addresses = await Dns.GetHostAddressesAsync(domain.DomainName, cancellationToken);
            if (addresses.Length == 0)
            {
                domain.DnsStatus = AgentDomainStatus.PendingDns;
                domain.LastError = "Waiting for DNS propagation. IPRO will check again automatically within 5 minutes.";
                return;
            }

            // A5-M-SSRF: a name resolving to loopback / private / link-local space is a probe of
            // things only this server can reach, not a customer domain. Refuse to fetch it, and do
            // not ask Azure to bind it either.
            if (PublicHostGuard.AnyBlocked(addresses))
            {
                domain.DnsStatus = AgentDomainStatus.Failed;
                domain.LastError = "This domain points at a private or internal address, so it cannot be used as a website address.";
                _logger.LogWarning("Custom domain {Domain} resolves to a non-public address; check refused.", domain.DomainName);
                return;
            }

            domain.DnsStatus = AgentDomainStatus.DnsReady;
            await EnsureAzureBindingAsync(domain, cancellationToken);
        }
        catch (Exception ex)
        {
            domain.DnsStatus = AgentDomainStatus.PendingDns;
            domain.LastError = "Waiting for DNS propagation. Confirm the CNAME points to " + domain.DnsTarget + "; IPRO will check again automatically within 5 minutes.";
            _logger.LogInformation(ex, "DNS check failed for custom domain {Domain}", domain.DomainName);
        }
    }

    private async Task EnsureAzureBindingAsync(AgentDomain domain, CancellationToken cancellationToken)
    {
        if (domain.AzureBindingStatus != AgentDomainStatus.Bound ||
            (_azureDomains.IsConfigured && domain.SslStatus != AgentDomainStatus.Bound))
        {
            var result = await _azureDomains.EnsureDomainAsync(domain.DomainName, cancellationToken);
            if (result.Success)
            {
                domain.DnsStatus = AgentDomainStatus.Bound;
                domain.AzureBindingStatus = AgentDomainStatus.Bound;
                domain.SslStatus = result.SslBound ? AgentDomainStatus.Bound : AgentDomainStatus.BindingPending;
                domain.LastError = result.SslBound ? string.Empty : result.Message;
                return;
            }

            if (_azureDomains.IsConfigured)
            {
                domain.AzureBindingStatus = AgentDomainStatus.Failed;
                domain.SslStatus = AgentDomainStatus.BindingPending;
                domain.LastError = result.Message;
                return;
            }
        }

        await CheckAzureBindingAsync(domain, cancellationToken);
    }

    private async Task CheckAzureBindingAsync(AgentDomain domain, CancellationToken cancellationToken)
    {
        try
        {
            // A5-M-SSRF: the factory client follows redirects, so a hostile domain could answer our
            // probe with a 302 to an internal address and have us fetch it. The no-redirect client
            // asks the only question this check has: what does the FIRST response look like? A 3xx
            // has no Azure "not configured" marker in its body, so a legitimately-bound site that
            // redirects http->https still lands in the Bound branch exactly as it did before.
            using var response = await NoRedirectClient.GetAsync("http://" + domain.DomainName, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (body.Contains("Custom domain has not been configured inside Azure", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("404 Web Site not found", StringComparison.OrdinalIgnoreCase))
            {
                domain.AzureBindingStatus = AgentDomainStatus.BindingPending;
                domain.SslStatus = AgentDomainStatus.BindingPending;
                domain.LastError = "DNS is ready. Azure custom-domain binding is still needed.";
                return;
            }

            domain.AzureBindingStatus = AgentDomainStatus.Bound;
            domain.SslStatus = AgentDomainStatus.Bound;
            domain.DnsStatus = AgentDomainStatus.Bound;
            domain.LastError = string.Empty;
        }
        catch (Exception ex)
        {
            domain.AzureBindingStatus = AgentDomainStatus.BindingPending;
            domain.LastError = "DNS is ready, but the site could not be checked yet.";
            _logger.LogInformation(ex, "Azure binding check failed for custom domain {Domain}", domain.DomainName);
        }
    }

    private async Task CheckRootDomainAsync(AgentDomain domain, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(domain.RootDomain) ||
            string.Equals(domain.RootDomain, domain.DomainName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        domain.RootLastCheckedAt = DateTime.UtcNow;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(domain.RootDomain, cancellationToken);
            if (addresses.Length == 0)
            {
                domain.RootDnsStatus = AgentDomainStatus.NotConfigured;
                domain.RootRedirectsToWww = false;
                domain.RootLastError = "The root domain does not resolve yet. Ask your registrar to forward it to the www address.";
                return;
            }

            // A5-M-SSRF: same refusal as the www check -- the root fetch below must never target
            // loopback / private / link-local space.
            if (PublicHostGuard.IsBlockedHost(domain.RootDomain) || PublicHostGuard.AnyBlocked(addresses))
            {
                domain.RootDnsStatus = AgentDomainStatus.NotConfigured;
                domain.RootRedirectsToWww = false;
                domain.RootLastError = "The root domain points at a private or internal address, so it cannot be checked.";
                _logger.LogWarning("Root domain {Domain} resolves to a non-public address; check refused.", domain.RootDomain);
                return;
            }

            domain.RootDnsStatus = AgentDomainStatus.DnsReady;

            // Ask only the question we actually care about: does the registrar redirect the bare
            // domain to the www host? Read the FIRST response's Location header instead of following
            // the chain to completion.
            //
            // Following it through meant the app fetched its own public hostname over HTTPS from
            // inside App Service -- a TLS handshake and a round trip that can fail or time out for
            // reasons that have nothing to do with the agent's forwarding, and any such failure was
            // reported to the agent as "not forwarding". One redirect hop is the whole question.
            using var response = await NoRedirectClient.GetAsync(
                "http://" + domain.RootDomain, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            var isRedirect = (int)response.StatusCode is >= 300 and < 400;
            var location = response.Headers.Location;
            string? target = null;

            if (isRedirect && location != null)
            {
                // Location may be relative; resolve against the request URI before reading the host.
                target = location.IsAbsoluteUri
                    ? location.Host
                    : new Uri(new Uri("http://" + domain.RootDomain), location).Host;
            }

            domain.RootRedirectsToWww =
                string.Equals(target, domain.WwwDomain, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target, domain.DomainName, StringComparison.OrdinalIgnoreCase);

            domain.RootLastError = domain.RootRedirectsToWww
                ? string.Empty
                : isRedirect
                    ? $"The root domain redirects to {target ?? "somewhere else"} rather than {domain.WwwDomain}."
                    : $"The root domain answered with {(int)response.StatusCode} instead of redirecting to {domain.WwwDomain}. Visitors typing the bare domain may not reach the site.";

            // Log the inputs to the decision, not just the verdict. Diagnosing a wrong "Not
            // forwarding" from outside meant guessing at which of several steps had failed, with no
            // way to tell a stale value from a failing check.
            //
            // Warning, not Information, ONLY when the answer is negative: Application Insights
            // captures Warning and above by default, so an Information line here is invisible in
            // production -- which is exactly the hole that made this undiagnosable. A successful
            // check stays at Information so we do not manufacture noise.
            if (domain.RootRedirectsToWww)
            {
                _logger.LogInformation("Root check {Root}: forwards to {Target} as expected", domain.RootDomain, target);
            }
            else
            {
                _logger.LogWarning(
                    "Root check {Root} says NOT forwarding: status={Status} location={Location} target={Target} expected={Www}",
                    domain.RootDomain, (int)response.StatusCode, location?.ToString() ?? "(none)",
                    target ?? "(none)", domain.WwwDomain);
            }
        }
        catch (Exception ex)
        {
            domain.RootDnsStatus = AgentDomainStatus.NotConfigured;
            domain.RootRedirectsToWww = false;
            domain.RootLastError = "Could not check the root domain yet.";
            _logger.LogInformation(ex, "Root domain check failed for {Domain}", domain.RootDomain);
        }
    }
}
