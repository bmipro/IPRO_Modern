namespace IPRO.Utility;

public class AzureDomainAutomationOptions
{
    public bool Enabled { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;
    public string WebAppName { get; set; } = string.Empty;
    public string AppServicePlanResourceId { get; set; } = string.Empty;
    public string Location { get; set; } = "Canada East";
    public bool CreateManagedCertificate { get; set; } = true;
    public string ApiVersion { get; set; } = "2023-12-01";

    public bool HasRequiredBindingSettings =>
        Enabled &&
        !string.IsNullOrWhiteSpace(TenantId) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(SubscriptionId) &&
        !string.IsNullOrWhiteSpace(ResourceGroup) &&
        !string.IsNullOrWhiteSpace(WebAppName);

    public bool HasRequiredCertificateSettings =>
        HasRequiredBindingSettings &&
        CreateManagedCertificate &&
        !string.IsNullOrWhiteSpace(AppServicePlanResourceId) &&
        !string.IsNullOrWhiteSpace(Location);
}
