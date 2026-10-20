using IPRO.Entities;

namespace IPRO.Utility;

public interface IDomainCheckService
{
    Task<bool> CheckAsync(AgentDomain domain, CancellationToken cancellationToken = default);

    // 592: whether `token` is published at _ipro-verify.<provenName> (true), is not (false), or no resolver answered
    // (null). Asked by My Website when another adviser holds the name unproven. A service that cannot look says
    // it does not know.
    Task<bool?> IsOwnershipPublishedAsync(string provenName, string token, CancellationToken cancellationToken = default) =>
        Task.FromResult<bool?>(null);
}
