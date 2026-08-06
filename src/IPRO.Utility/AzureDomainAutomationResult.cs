namespace IPRO.Utility;

public class AzureDomainAutomationResult
{
    public bool Success { get; init; }
    public bool BindingCreated { get; init; }
    public bool CertificateCreated { get; init; }
    public bool SslBound { get; init; }
    public string Message { get; init; } = string.Empty;

    public static AzureDomainAutomationResult Skipped(string message) => new()
    {
        Success = false,
        Message = message
    };

    public static AzureDomainAutomationResult Failed(string message) => new()
    {
        Success = false,
        Message = message
    };
}
