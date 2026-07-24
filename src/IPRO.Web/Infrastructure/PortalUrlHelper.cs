namespace IPRO.Web.Infrastructure;

public static class PortalUrlHelper
{
    public static string GetAgentPortalBaseUrl(IConfiguration configuration)
    {
        var configured = configuration["App:PortalBaseUrl"]?.Trim().TrimEnd('/');
        return Uri.TryCreate(configured, UriKind.Absolute, out _) && !configured!.Contains("YOUR_", StringComparison.OrdinalIgnoreCase)
            ? configured
            : "https://ipro-prod-web.azurewebsites.net";
    }
}
