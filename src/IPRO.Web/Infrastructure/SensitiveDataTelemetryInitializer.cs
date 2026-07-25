using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.WebUtilities;

namespace IPRO.Web.Infrastructure;

// Application Insights here is enabled via Azure's codeless auto-instrumentation agent (no SDK
// reference otherwise), which captures the full request URL including query string by default.
// Several links in this app carry a real, single-use, short-lived token in a query parameter
// (password reset, client-invoice/testimonial view links, PayPal return) - without this scrub, that
// token would sit in App Insights telemetry, visible to anyone with read access to the Azure
// resource, for as long as telemetry is retained - well beyond the token's own short validity window.
public class SensitiveDataTelemetryInitializer : ITelemetryInitializer
{
    private static readonly string[] SensitiveQueryParams = { "token", "subscription_id" };

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
