using System.Text.RegularExpressions;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.AspNetCore.WebUtilities;

namespace IPRO.Utility;

// Application Insights here is enabled via Azure's codeless auto-instrumentation agent, which captures the full
// request URL including the query string by default. Several links in this app carry a real, single-use,
// short-lived token; without this scrub, that token would sit in telemetry, visible to anyone with read access to
// the Azure resource, for as long as telemetry is kept, well beyond the token's own validity window.
//
// Tokens travel two ways, and both are scrubbed (SO-M-NEW-6, completed 2026-08-20):
//   query string  - ?token=..., ?subscription_id=...   (password reset, email preferences, PayPal)
//   path segment  - /invoice/{token}, /testimonial/{token}   (client invoice + testimonial links)
//   493 (2026-09-17) - ?secret=... (the Event Grid webhook secret rides in the query string of every delivery
//                    report), and the platform's own tracking links, which carry a never-expiring
//                    per-recipient token in the path (/t/o/{kind}/{token}.gif, /t/c/{kind}/{token}) and the
//                    redirect's signature in ?s=. The privacy policy says telemetry is scrubbed of tokens.
//   496 - the prospect's own name and company, which the 30-second preview carries in its URL (its form is a GET)
//         under a banner that says "Nothing is saved".
//
// 663 (audit 08-20 L2/L3, found open again 2026-11-20): the scrub looked at page requests only and returned at
// once for anything else, so the same address written into a log line, an outgoing call or an error went out as
// it was; and SuperAdmin had its own hand copy that had drifted (two names instead of eight, no tracking links)
// and was never tested. One scrub for both apps now, over every kind of record.
public static class SensitiveTelemetry
{
    // 678 (audit 2026-11-24 F-8): also the OAuth callbacks' code and state (Google Calendar, Stripe), a free-trial
    // invite's trialCode, the names typed into a search, and t, the website sign-up confirmation's token.
    public static readonly string[] SensitiveQueryParams = { "token", "subscription_id", "secret", "s", "firstName", "lastName", "companyName", "key", "code", "state", "trialCode", "search", "t" };

    private static readonly Regex TokenPathSegment = new(
        @"(?i)(/(?:invoice|testimonial)/|/t/[oc]/[^/?#\s]+/)([^/?#\s""']+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // The same names inside free text (a log line, an error message): name=value up to the next & # space or quote.
    private static readonly Regex SensitiveParamInText = new(
        @"(?i)([?&](?:" + string.Join("|", SensitiveQueryParams.Select(Regex.Escape)) + @")=)[^&#\s""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void Scrub(ITelemetry telemetry)
    {
        if (!string.IsNullOrEmpty(telemetry.Context.Operation.Name))
            telemetry.Context.Operation.Name = ScrubText(telemetry.Context.Operation.Name);

        switch (telemetry)
        {
            case RequestTelemetry request:
                ScrubRequest(request);
                break;
            case DependencyTelemetry dependency:
                if (!string.IsNullOrEmpty(dependency.Name)) dependency.Name = ScrubText(dependency.Name);
                if (!string.IsNullOrEmpty(dependency.Data)) dependency.Data = ScrubText(dependency.Data);
                break;
            case TraceTelemetry trace:
                if (!string.IsNullOrEmpty(trace.Message)) trace.Message = ScrubText(trace.Message);
                break;
            case ExceptionTelemetry exception:
                if (!string.IsNullOrEmpty(exception.Message)) exception.Message = ScrubText(exception.Message);
                // 678 (audit F-8): the inner exceptions' messages too, not only the outer one.
                foreach (var details in exception.ExceptionDetailsInfoList)
                {
                    if (!string.IsNullOrEmpty(details.Message)) details.Message = ScrubText(details.Message);
                }
                break;
        }

        if (telemetry is ISupportProperties withProperties)
        {
            foreach (var key in withProperties.Properties.Keys.ToList())
            {
                var value = withProperties.Properties[key];
                if (!string.IsNullOrEmpty(value)) withProperties.Properties[key] = ScrubText(value);
            }
        }
    }

    // Any text: the token in a path, and the sensitive names' values in a query.
    public static string ScrubText(string text) =>
        SensitiveParamInText.Replace(TokenPathSegment.Replace(text, "$1REDACTED"), "$1REDACTED");

    private static void ScrubRequest(RequestTelemetry request)
    {
        if (!string.IsNullOrEmpty(request.Name))
            request.Name = TokenPathSegment.Replace(request.Name, "$1REDACTED");

        if (request.Url == null) return;

        var url = request.Url.ToString();
        var scrubbedPath = TokenPathSegment.Replace(url, "$1REDACTED");
        if (scrubbedPath != url)
            request.Url = new Uri(scrubbedPath);

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
