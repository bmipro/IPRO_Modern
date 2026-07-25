using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

public class DomainAutomationJob
{
    private readonly IPRODbContext _db;
    private readonly IDomainCheckService _domainCheck;
    private readonly ILogger<DomainAutomationJob> _logger;

    public DomainAutomationJob(IPRODbContext db, IDomainCheckService domainCheck, ILogger<DomainAutomationJob> logger)
    {
        _db = db;
        _domainCheck = domainCheck;
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Domain automation check failed for domain {DomainId}", domain.Id);
            }
        }

        await _db.SaveChangesAsync();
    }
}
