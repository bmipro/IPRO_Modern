using IPRO.Entities;

namespace IPRO.Utility;

public interface IDomainCheckService
{
    Task<bool> CheckAsync(AgentDomain domain, CancellationToken cancellationToken = default);
}
