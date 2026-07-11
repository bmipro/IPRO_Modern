using System.Net;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

public class DomainAutomationJob
{
    private readonly IPRODbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DomainAutomationJob> _logger;

    public DomainAutomationJob(IPRODbContext db, IHttpClientFactory httpClientFactory, ILogger<DomainAutomationJob> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        var domains = await _db.AgentDomains
            .Where(d => d.IsPrimary &&
                        d.DnsStatus != AgentDomainStatus.Bound &&
                        d.AzureBindingStatus != AgentDomainStatus.Bound)
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
                domain.LastError = "DNS has not resolved yet.";
                return;
            }

            domain.DnsStatus = AgentDomainStatus.DnsReady;
            await CheckAzureBindingAsync(domain);
        }
        catch (Exception ex)
        {
            domain.DnsStatus = AgentDomainStatus.PendingDns;
            domain.LastError = "DNS has not resolved yet. Confirm the CNAME points to " + domain.DnsTarget + ".";
            _logger.LogInformation(ex, "DNS check failed for custom domain {Domain}", domain.DomainName);
        }
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
