using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.WebUtilities;

namespace IPRO.Admin.Infrastructure;

// Application Insights here is enabled via Azure's codeless auto-instrumentation agent (no SDK
// reference otherwise), which captures the full request URL including query string by default.
// Mirrors IPRO.Web's initializer defensively: Admin generates short-lived preview tokens (e.g.
// WebsiteTemplatesController's admin template preview) that are passed via query string, even
// though today they're redirected to the Web app rather than appearing on an Admin-side URL.
public class SensitiveDataTelemetryInitializer : ITelemetryInitializer
{
    private static readonly string[] SensitiveQueryParams = { "token" };

    public void Initialize(ITelemetry telemetry)
    {
        if (telemetry is not RequestTelemetry request || request.Url == null || string.IsNullOrEmpty(request.Url.Query))
        {
            return;
        }

        var query = QueryHelpers.ParseQuery(request.Url.Query);
        if (!SensitiveQueryParams.Any(p => query.ContainsKey(p)))
        {
            return;
        }

        var withoutQuery = new UriBuilder(request.Url) { Query = string.Empty }.Uri.ToString();
        var scrubbed = query.ToDictionary(
            kvp => kvp.Key,
            kvp => SensitiveQueryParams.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase) ? "REDACTED" : kvp.Value.ToString())
            as IDictionary<string, string?>;

        request.Url = new Uri(QueryHelpers.AddQueryString(withoutQuery, scrubbed));
    }
}
