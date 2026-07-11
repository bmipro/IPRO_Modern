using System.Net;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

public class DomainAutomationJob
{
    private readonly IPRODbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAzureDomainAutomationService _azureDomains;
    private readonly ILogger<DomainAutomationJob> _logger;

    public DomainAutomationJob(
        IPRODbContext db,
        IHttpClientFactory httpClientFactory,
        IAzureDomainAutomationService azureDomains,
        ILogger<DomainAutomationJob> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _azureDomains = azureDomains;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        var domains = await _db.AgentDomains
            .Where(d => d.DnsStatus != AgentDomainStatus.Bound ||
                         d.AzureBindingStatus != AgentDomainStatus.Bound ||
                         d.SslStatus != AgentDomainStatus.Bound)
            .OrderBy(d => d.LastCheckedAt ?? d.CreatedAt)
            .Take(50)
            .ToListAsync();

        foreach (var domain in domains)
        {
            await CheckDomainAsync(domain);
        }

        await _db.SaveChangesAsync();
    }

    private async Task CheckDomainAsync(AgentDomain domain)
    {
        domain.LastCheckedAt = DateTime.UtcNow;
        domain.UpdatedAt = DateTime.UtcNow;
        domain.LastError = string.Empty;

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(domain.DomainName);
            if (addresses.Length == 0)
            {
                domain.DnsStatus = AgentDomainStatus.PendingDns;
                domain.LastError = "Waiting for DNS propagation. IPRO will check again automatically within 5 minutes.";
                return;
            }

            domain.DnsStatus = AgentDomainStatus.DnsReady;
            await EnsureAzureBindingAsync(domain);
        }
        catch (Exception ex)
        {
            domain.DnsStatus = AgentDomainStatus.PendingDns;
            domain.LastError = "Waiting for DNS propagation. Confirm the CNAME points to " + domain.DnsTarget + "; IPRO will check again automatically within 5 minutes.";
            _logger.LogInformation(ex, "DNS check failed for custom domain {Domain}", domain.DomainName);
        }
    }

    private async Task EnsureAzureBindingAsync(AgentDomain domain)
    {
        if (domain.AzureBindingStatus != AgentDomainStatus.Bound ||
            (_azureDomains.IsConfigured && domain.SslStatus != AgentDomainStatus.Bound))
        {
            var result = await _azureDomains.EnsureDomainAsync(domain.DomainName);
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

        await CheckAzureBindingAsync(domain);
    }

    private async Task CheckAzureBindingAsync(AgentDomain domain)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(12);
            using var response = await client.GetAsync("http://" + domain.DomainName);
            var body = await response.Content.ReadAsStringAsync();

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
}
