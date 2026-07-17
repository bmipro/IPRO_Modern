using System.Net;
using IPRO.Entities;
using Microsoft.Extensions.Logging;

namespace IPRO.Utility;

public class DomainCheckService : IDomainCheckService
{
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
            var addresses = await Dns.GetHostAddressesAsync(domain.DomainName, cancellationToken);
            if (addresses.Length == 0)
            {
                domain.DnsStatus = AgentDomainStatus.PendingDns;
                domain.LastError = "Waiting for DNS propagation. IPRO will check again automatically within 5 minutes.";
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
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(12);
            using var response = await client.GetAsync("http://" + domain.DomainName, cancellationToken);
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

            domain.RootDnsStatus = AgentDomainStatus.DnsReady;

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            using var response = await client.GetAsync("http://" + domain.RootDomain, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var finalHost = response.RequestMessage?.RequestUri?.Host;
            domain.RootRedirectsToWww = string.Equals(finalHost, domain.WwwDomain, StringComparison.OrdinalIgnoreCase);
            domain.RootLastError = domain.RootRedirectsToWww
                ? string.Empty
                : $"The root domain resolves but does not redirect to {domain.WwwDomain}. Visitors typing the bare domain may not reach the site.";
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
