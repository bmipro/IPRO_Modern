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

    // 553 test seam (the PublicHostGuard.ResolveHook pattern): every name this service looks up.
    internal static Func<string, CancellationToken, Task<IPAddress[]>> ResolveHook =
        (host, cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAzureDomainAutomationService _azureDomains;
    private readonly ILogger<DomainCheckService> _logger;

    // The platform's own addresses, per CNAME target, for the life of this check run: one job run
    // checks up to fifty domains that all point at the same target.
    private readonly Dictionary<string, IPAddress[]> _platformAddresses = new(StringComparer.OrdinalIgnoreCase);

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
        await CheckCaaAsync(domain, cancellationToken);

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

    // 506: while a certificate is still wanted, read the domain's CAA records. A list that leaves
    // DigiCert out means the certificate will never arrive, and until now nothing said why. A lookup
    // that fails changes nothing: what was known stays.
    internal async Task CheckCaaAsync(AgentDomain domain, CancellationToken cancellationToken = default)
    {
        var hosts = new List<string>();
        if (domain.SslStatus != AgentDomainStatus.Bound && !string.IsNullOrWhiteSpace(domain.DomainName)) hosts.Add(domain.DomainName);
        // The short address needs a certificate of its own only when it points straight at the platform (553).
        if (domain.RootDnsStatus == AgentDomainStatus.Bound && domain.RootSslStatus != AgentDomainStatus.Bound &&
            !string.IsNullOrWhiteSpace(domain.RootDomain) && !hosts.Contains(domain.RootDomain, StringComparer.OrdinalIgnoreCase))
            hosts.Add(domain.RootDomain);
        if (hosts.Count == 0)
        {
            domain.CaaBlockingName = string.Empty;   // every certificate is in place
            return;
        }

        var blocking = string.Empty;
        foreach (var host in hosts)
        {
            if (PublicHostGuard.IsBlockedHost(host)) continue;
            var found = await CaaCheck.FindBlockingNameAsync(host, CaaCheck.LookupHook ?? LookUpCaaAsync, cancellationToken);
            if (found == null) return;                // a lookup failed: keep what was known
            if (found.Length > 0) { blocking = found; break; }
        }
        if (blocking.Length > 0 && blocking != domain.CaaBlockingName)
            _logger.LogWarning("Custom domain {Domain}: the CAA records at {Name} do not allow {Authority}, so no certificate can be issued", domain.DomainName, blocking, CaaCheck.Authority);
        domain.CaaBlockingName = blocking;
    }

    // One name's CAA records from a public DNS-over-HTTPS resolver, as its JSON. Null on any failure.
    private async Task<string?> LookUpCaaAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync($"https://dns.google/resolve?name={Uri.EscapeDataString(name)}&type=CAA", timeout.Token);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(timeout.Token) : null;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "CAA lookup for {Name} did not answer", name);
            return null;
        }
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

            var addresses = await ResolveHook(domain.DomainName, cancellationToken);
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
            var addresses = await ResolveHook(domain.RootDomain, cancellationToken);
            if (addresses.Length == 0)
            {
                domain.RootDnsStatus = AgentDomainStatus.NotConfigured;
                domain.RootRedirectsToWww = false;
                domain.RootLastError = "The root domain does not resolve yet. Point it at us or forward it to the www address (see the setup steps).";
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

            // 553: is the short address pointed straight at the platform (an A record) rather than
            // through a registrar's forwarding? Then it is ours to bind and secure, like the www
            // name, and the app itself sends it on to www with the page path kept -- which a
            // registrar's forwarding does not do (GoDaddy's answers 404 for anything but the home
            // address; measured 2026-10-06 on three domains).
            var platform = await PlatformAddressesAsync(domain.DnsTarget, cancellationToken);
            var here = addresses.Count(address => platform.Contains(address));
            if (here > 0)
            {
                await CheckDirectRootAsync(domain, addresses.Length - here, cancellationToken);
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

    // 553: the short address resolves to the platform. RootDnsStatus = Bound records that; the two
    // Azure statuses record what has been done about it. "Working" (RootRedirectsToWww, the flag
    // every screen reads) waits for the certificate: until then https on the short address shows a
    // browser warning, exactly as the www name does for its first few minutes.
    private async Task CheckDirectRootAsync(AgentDomain domain, int otherAddresses, CancellationToken cancellationToken)
    {
        // "Failed" on this row means one thing: the adviser has something to do, and RootLastError
        // says what (ShortAddressState shows it as Needs attention). A name that is already bound
        // stays recorded as bound whatever happens next -- removing the domain must still know
        // there is a binding to delete -- and anything that mends itself is left to the next check.
        if (otherAddresses > 0)
        {
            // A leftover record beside ours: the name answers in turn from both hosts, so some
            // visitors -- and the certificate authority's validation request -- still reach the old
            // one. RootDnsStatus = Failed is that state: ours, but not ours alone.
            domain.RootDnsStatus = AgentDomainStatus.Failed;
            domain.RootRedirectsToWww = false;
            domain.RootLastError = $"{domain.RootDomain} has more than one address. Remove the other A (and AAAA) records so that ours is the only one left, then click Check now.";
            return;
        }

        domain.RootDnsStatus = AgentDomainStatus.Bound;

        if (domain.RootAzureBindingStatus != AgentDomainStatus.Bound || domain.RootSslStatus != AgentDomainStatus.Bound)
        {
            var result = await _azureDomains.EnsureRootDomainAsync(domain.RootDomain, cancellationToken);
            if (result.Success || result.BindingCreated)
            {
                // Bound. Success without SslBound is the certificate still being issued; a failure
                // AFTER the binding is the certificate order refused for now (Azure's own DNS check
                // still sees the name's previous record). Both are "securing": the every-run path
                // of the job asks again in five minutes.
                if (domain.RootAzureBindingStatus != AgentDomainStatus.Bound) domain.RootBoundAt = DateTime.UtcNow;
                domain.RootAzureBindingStatus = AgentDomainStatus.Bound;
                domain.RootSslStatus = result.Success && result.SslBound ? AgentDomainStatus.Bound : AgentDomainStatus.BindingPending;
                if (!result.Success)
                {
                    _logger.LogWarning("Short address {Root} is bound; its certificate order was not accepted yet: {Message}", domain.RootDomain, result.Message);
                }
            }
            else
            {
                var (sentence, adviserCanFixIt) = DescribeRootFailure(domain.RootDomain, result.Message, _azureDomains.IsConfigured);
                if (domain.RootAzureBindingStatus != AgentDomainStatus.Bound)
                {
                    domain.RootAzureBindingStatus = adviserCanFixIt ? AgentDomainStatus.Failed : AgentDomainStatus.NotConfigured;
                }

                domain.RootRedirectsToWww = false;
                domain.RootLastError = sentence;
                // Warning: the raw Azure answer is the only place the real reason is written down
                // (the adviser sees the plain sentence above), and Information is not captured.
                _logger.LogWarning("Short address {Root} could not be bound: {Message}", domain.RootDomain, result.Message);
                return;
            }
        }

        var secured = domain.RootSslStatus == AgentDomainStatus.Bound;
        domain.RootRedirectsToWww = secured;
        domain.RootLastError = secured
            ? string.Empty
            : $"We are securing {domain.RootDomain}. This usually takes a few minutes and happens automatically.";
    }

    // What the adviser is told when Azure will not bind the short address, and whether it is theirs
    // to fix. Azure's own sentence for the usual case names the record ("A TXT record pointing from
    // asuid.example.com to ... was not found"), so that case gets the one instruction that fixes it.
    internal static (string Sentence, bool AdviserCanFixIt) DescribeRootFailure(string rootDomain, string? azureMessage, bool automationConfigured)
    {
        if (!automationConfigured)
        {
            return ("Domain automation is being finalized on our side. Please check back shortly.", false);
        }

        var message = azureMessage ?? string.Empty;
        if (message.Contains("TXT", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("asuid", StringComparison.OrdinalIgnoreCase))
        {
            return ($"{rootDomain} points at us, but its TXT record is missing or not right yet. Add the TXT record from the setup steps (name asuid), then click Check now.", true);
        }

        if (message.Contains("Conflict", StringComparison.OrdinalIgnoreCase))
        {
            return ($"{rootDomain} is already connected to another site in our system. Contact support if this seems wrong.", true);
        }

        return ($"We are connecting {rootDomain}. It did not go through on this try; we will keep trying, and there is nothing for you to do.", false);
    }

    private async Task<IPAddress[]> PlatformAddressesAsync(string? dnsTarget, CancellationToken cancellationToken)
    {
        var target = (dnsTarget ?? string.Empty).Trim().Trim('.');
        if (target.Length == 0) return Array.Empty<IPAddress>();
        if (_platformAddresses.TryGetValue(target, out var known)) return known;

        IPAddress[] found;
        try
        {
            found = await ResolveHook(target, cancellationToken);
        }
        catch (Exception ex)
        {
            // Not remembered: the next domain in this run asks again.
            _logger.LogWarning(ex, "The platform's own name {Target} did not resolve; short addresses pointed at it cannot be recognised this run", target);
            return Array.Empty<IPAddress>();
        }

        _platformAddresses[target] = found;
        return found;
    }
}
