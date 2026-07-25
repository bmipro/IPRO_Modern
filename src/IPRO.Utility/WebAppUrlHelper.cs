using Microsoft.Extensions.Configuration;

namespace IPRO.Utility;

// Single source of truth for "what is this app's own public base URL" - previously duplicated,
// independently, across PortalUrlHelper (IPRO.Web), NewsLetterDispatcher.GetBaseUrl,
// PayPalBillingService.BuildBillingPageUrl, and OverdueInvoiceReminderJob.BuildInvoiceUrl. Two of
// those read a different config key (App:PortalBaseUrl vs App:BaseUrl) than the others, so the same
// "app base URL" could be correctly configured under one key and left at its placeholder default
// under the other - that exact class of two-key config drift has caused two real incidents in this
// project's history. Checking both keys here, in one place, means whichever one is actually set in
// Azure keeps working, and there's only one spot left to fix if the fallback URL ever changes.
public static class WebAppUrlHelper
{
    private const string FallbackUrl = "https://ipro-prod-web.azurewebsites.net";

    public static string GetWebAppBaseUrl(IConfiguration configuration)
    {
        var baseUrl = configuration["App:BaseUrl"];
        if (IsConfigured(baseUrl))
        {
            return baseUrl!.TrimEnd('/');
        }

        var portalBaseUrl = configuration["App:PortalBaseUrl"];
        if (IsConfigured(portalBaseUrl))
        {
            return portalBaseUrl!.TrimEnd('/');
        }

        return FallbackUrl;
    }

    private static bool IsConfigured(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out _) &&
        !value.Contains("yourdomain.com", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("YOUR_", StringComparison.OrdinalIgnoreCase);
}
