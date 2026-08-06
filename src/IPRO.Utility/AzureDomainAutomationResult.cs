namespace IPRO.Utility;

public class AzureDomainAutomationResult
{
    public bool Success { get; init; }
    public bool BindingCreated { get; init; }
    public bool CertificateCreated { get; init; }
    public bool SslBound { get; init; }

    /// <summary>
    /// The hostname is bound but has no certificate, and automation cannot produce one.
    /// This is terminal, not transient: the site is HTTPS-only, so it stays unreachable
    /// (browser security warning) until a human runs ops/New-AgentCert.ps1.
    /// </summary>
    public bool CertificateNeedsManualIssue { get; init; }
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
