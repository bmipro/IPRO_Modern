namespace IPRO.Utility;

public interface IAzureDomainAutomationService
{
    bool IsConfigured { get; }
    Task<AzureDomainAutomationResult> EnsureDomainAsync(string hostName, CancellationToken cancellationToken = default);
    Task<AzureDomainAutomationResult> RemoveDomainAsync(string hostName, CancellationToken cancellationToken = default);
}
