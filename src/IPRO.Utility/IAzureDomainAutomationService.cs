namespace IPRO.Utility;

public interface IAzureDomainAutomationService
{
    bool IsConfigured { get; }
    Task<AzureDomainAutomationResult> EnsureDomainAsync(string hostName, CancellationToken cancellationToken = default);

    // 553: the same for a short address (example.com) that points at the platform with an A record.
    // Azure proves a name mapped that way by its asuid TXT record, not by a CNAME.
    Task<AzureDomainAutomationResult> EnsureRootDomainAsync(string hostName, CancellationToken cancellationToken = default);

    Task<AzureDomainAutomationResult> RemoveDomainAsync(string hostName, CancellationToken cancellationToken = default);
}
