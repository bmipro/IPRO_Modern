using System.Text.RegularExpressions;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.WebUtilities;

namespace IPRO.Admin.Infrastructure;

// Application Insights here is enabled via Azure's codeless auto-instrumentation agent (no SDK
// reference otherwise), which captures the full request URL including query string by default.
// Mirrors IPRO.Web's initializer so the two cannot drift (the Admin app has no token-in-path
// routes today, but the scrub is cheap and Admin does mint short-lived preview tokens).
//
// Tokens travel two ways, and both are scrubbed (SO-M-NEW-6, completed 2026-08-20):
//   query string  - ?token=..., ?subscription_id=...   (password reset, email preferences, PayPal)
//   path segment  - /invoice/{token}, /testimonial/{token}   (client invoice + testimonial links)
// The first pass only handled the query string; the two path-carried links kept logging live
// tokens for another month. The path scrub also covers request.Name, which repeats the path.
public class SensitiveDataTelemetryInitializer : ITelemetryInitializer
{
    private static readonly string[] SensitiveQueryParams = { "token", "subscription_id" };

    private static readonly Regex TokenPathSegment = new(
        @"(?i)(/(?:invoice|testimonial)/)([^/?#\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public void Initialize(ITelemetry telemetry)
    {
        if (telemetry is not RequestTelemetry request) return;

        if (!string.IsNullOrEmpty(request.Name))
        {
            request.Name = TokenPathSegment.Replace(request.Name, "$1REDACTED");
        }
        if (!string.IsNullOrEmpty(telemetry.Context.Operation.Name))
        {
            telemetry.Context.Operation.Name = TokenPathSegment.Replace(telemetry.Context.Operation.Name, "$1REDACTED");
        }

        if (request.Url == null) return;

        var url = request.Url.ToString();
        var scrubbedPath = TokenPathSegment.Replace(url, "$1REDACTED");
        if (!ReferenceEquals(scrubbedPath, url) && scrubbedPath != url)
        {
            request.Url = new Uri(scrubbedPath);
        }

        if (string.IsNullOrEmpty(request.Url.Query)) return;

        var query = QueryHelpers.ParseQuery(request.Url.Query);
        if (!SensitiveQueryParams.Any(p => query.ContainsKey(p))) return;

        var withoutQuery = new UriBuilder(request.Url) { Query = string.Empty }.Uri.ToString();
        var scrubbed = query.ToDictionary(
            kvp => kvp.Key,
            kvp => SensitiveQueryParams.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase) ? "REDACTED" : kvp.Value.ToString())
            as IDictionary<string, string?>;

        request.Url = new Uri(QueryHelpers.AddQueryString(withoutQuery, scrubbed));
    }
}
