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

    /// <summary>
    /// True if the current request host is the platform's own domain (where the agent portal
    /// and SuperAdmin live) rather than an agent's public website (temporary *.247advisers.com
    /// subdomain or a custom domain they've attached). Mirrors the host classification in
    /// Program.cs's ShouldRouteToPublicWebsite - kept separate (not shared) since that function
    /// answers a slightly different question (route this GET to the public site or not) and is
    /// too central to the app's routing to risk touching for this.
    /// </summary>
    public static bool IsPlatformHost(HttpContext context, IConfiguration configuration)
    {
        var host = context.Request.Host.Host.Trim().Trim('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(host)) return true;
        if (host is "localhost" or "127.0.0.1" or "::1") return true;
        if (host.EndsWith(".azurewebsites.net", StringComparison.OrdinalIgnoreCase)) return true;

        var adminDomain = configuration["App:AdminDomain"]?.Trim().Trim('.').ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(adminDomain) && host == adminDomain) return true;
        if (host.StartsWith("admin.", StringComparison.OrdinalIgnoreCase)) return true;

        var platformDomains = (configuration["App:PlatformDomains"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Trim().Trim('.').ToLowerInvariant())
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (platformDomains.Contains(host)) return true;

        var baseUrl = configuration["App:BaseUrl"];
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
            string.Equals(host, uri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
