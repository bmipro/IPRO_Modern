using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Scheduler;

public class DomainAutomationJob
{
    private readonly IPRODbContext _db;
    private readonly IDomainCheckService _domainCheck;

    public DomainAutomationJob(IPRODbContext db, IDomainCheckService domainCheck)
    {
        _db = db;
        _domainCheck = domainCheck;
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
            await _domainCheck.CheckAsync(domain);
        }

        await _db.SaveChangesAsync();
    }
}
