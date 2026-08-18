using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// The host-aware URL builder DOCS/INVARIANTS.md rule 3 promises. The auth cookie is host-only, so
// which base URL is correct depends on WHO the URL is for:
//
//   - IN-SESSION redirects and third-party return URLs (PayPal return_url/cancel_url): the host the
//     buyer's cookie actually lives on -> GetSessionBaseUrl / GetSessionBaseUrlAsync. Sending these
//     to the canonical host logs the buyer out mid-checkout (WEB-H-1: money moved at PayPal, the
//     subscription never activated locally, because /Billing/PayPalReturn arrived unauthenticated).
//
//   - OUT-OF-BAND links (email bodies, background jobs, webhooks): there is no request and no
//     session, and the link outlives both -> GetAgentPortalBaseUrl, the canonical origin, exactly
//     as before. Sweeping these to the host-aware method would bake whichever host happened to
//     serve the triggering request into mail that outlives it — do not.
//
//   - Third parties with a PRE-REGISTERED redirect-URI allowlist (Google OAuth, and only Google
//     today): the session host is unusable because Google has never seen it. Those callers bounce
//     to the canonical host first -> CanonicalRedirectUrlIfNeeded. PayPal is NOT such a party:
//     return_url is a per-order field.
public static class PortalUrlHelper
{
    // Canonical origin from App:BaseUrl -> App:PortalBaseUrl -> azurewebsites fallback.
    // For out-of-band links only; see the header for why in-session callers must not use it.
    public static string GetAgentPortalBaseUrl(IConfiguration configuration) =>
        WebAppUrlHelper.GetWebAppBaseUrl(configuration);

    // The single "is this a host we serve" predicate for session-URL purposes. Takes a bare string
    // so it is testable with zero ASP.NET types. Deliberately NOT merged with Program.cs's
    // ShouldRouteToPublicWebsite: that one answers "public site or portal?", this one answers
    // "may a session URL keep this host?" — same config keys, same normalisation shape
    // (NormalizeHostForLookup at Program.cs), different question. A comment there points here.
    //
    // AllowedHosts is "*" in appsettings.json, so ASP.NET HostFiltering screens nothing — this
    // allowlist is the only thing standing between a forged Host: header and PayPal's return_url.
    // Anything unrecognised falls back to canonical; custom domains are the async overload's job.
    public static bool IsAppHost(string? host, IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var normalized = host.Trim().Trim('.').ToLowerInvariant();
        if (normalized.Length == 0) return false;

        // Local dev environment (MEMORY: every change runs locally before any deploy).
        if (normalized is "localhost" or "127.0.0.1" or "::1") return true;

        if (normalized.EndsWith(".azurewebsites.net", StringComparison.Ordinal)) return true;

        var canonicalHost = new Uri(GetAgentPortalBaseUrl(configuration)).Host
            .Trim().Trim('.').ToLowerInvariant();
        if (normalized == canonicalHost) return true;

        var platformDomains = (configuration["App:PlatformDomains"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Trim().Trim('.').ToLowerInvariant())
            .Where(d => d.Length > 0);
        if (platformDomains.Contains(normalized)) return true;

        var temporaryRoot = (configuration["App:TemporarySiteRootDomain"] ?? "247advisers.com")
            .Trim().Trim('.').ToLowerInvariant();
        // "." prefix on purpose: bob.247advisers.com matches, evil-247advisers.com and
        // 247advisers.com.evil.com do not.
        if (normalized.EndsWith("." + temporaryRoot, StringComparison.Ordinal)) return true;

        return false;
    }

    // The origin the CURRENT session lives on: the host the host-only cookie was issued for.
    // Composes from Host.Value (port INCLUDED — http://localhost:5100 must round-trip) but matches
    // on Host.Host (portless). Unrecognised hosts fall back to canonical, never echoed.
    public static string GetSessionBaseUrl(HttpRequest request, IConfiguration configuration) =>
        IsAppHost(request.Host.Host, configuration)
            ? $"{request.Scheme}://{request.Host.Value}"
            : GetAgentPortalBaseUrl(configuration);

    // Async overload that additionally recognises a BOUND custom domain. The config allowlist is
    // consulted first so the canonical/subdomain case never touches the database. Matches the
    // health predicate shape in PublicWebsiteController (DomainName/WwwDomain/RootDomain,
    // AzureBindingStatus == Bound) but deliberately does NOT gate on SslStatus: the buyer's browser
    // is already on this host over TLS, so an SslStatus row lagging reality must not divert the
    // return URL to a host where the cookie does not exist — that would silently reintroduce the
    // exact WEB-H-1 defect this helper closes.
    public static async Task<string> GetSessionBaseUrlAsync(
        HttpRequest request, IConfiguration configuration, IPRODbContext db, ILogger? logger = null)
    {
        var host = request.Host.Host;
        if (IsAppHost(host, configuration))
        {
            return $"{request.Scheme}://{request.Host.Value}";
        }

        var normalized = (host ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
        if (normalized.Length > 0)
        {
            var isBoundCustomDomain = await db.AgentDomains.AsNoTracking().AnyAsync(d =>
                (d.DomainName.ToLower() == normalized ||
                 d.WwwDomain.ToLower() == normalized ||
                 d.RootDomain.ToLower() == normalized) &&
                d.AzureBindingStatus == AgentDomainStatus.Bound);
            if (isBoundCustomDomain)
            {
                return $"{request.Scheme}://{request.Host.Value}";
            }
        }

        // This line is how an operator finds a live custom domain missing from AgentDomains — the
        // checkout still works (canonical is always safe for a signed-out buyer to land on, they
        // can log in), but host preservation silently did not happen for this request.
        logger?.LogWarning(
            "Session URL fell back to the canonical host: request host {Host} is neither a platform host nor a bound custom domain.",
            host);
        return GetAgentPortalBaseUrl(configuration);
    }

    // The two PayPal action paths exist in exactly THIS file and nowhere else. A test walks the
    // source tree and fails on any other occurrence of these literals — that is what catches the
    // next hand-rolled copy (AccountController carried one for months; it was the higher-volume
    // signup path and the one nobody re-checked).
    public static async Task<string> BuildBillingActionUrlAsync(
        HttpRequest request, IConfiguration configuration, IPRODbContext db, string action, ILogger? logger = null) =>
        $"{await GetSessionBaseUrlAsync(request, configuration, db, logger)}/Billing/{action}";

    // The bounce, for callers whose third party requires a pre-registered redirect URI (Google
    // OAuth). Moved verbatim from GoogleCalendarController so "which hosts are ours" has one
    // answer; that controller's allowlist knew only canonical + azurewebsites and had already
    // drifted from this one. Returns null when no bounce is needed.
    public static string? CanonicalRedirectUrlIfNeeded(HttpRequest request, IConfiguration configuration)
    {
        var canonicalBaseUrl = GetAgentPortalBaseUrl(configuration);
        var canonicalHost = new Uri(canonicalBaseUrl).Host;
        var currentHost = request.Host.Host;
        if (string.Equals(currentHost, canonicalHost, StringComparison.OrdinalIgnoreCase)
            || currentHost.EndsWith(".azurewebsites.net", StringComparison.OrdinalIgnoreCase)
            || currentHost is "localhost" or "127.0.0.1" or "::1")
        {
            return null;
        }

        return $"{canonicalBaseUrl}{request.Path}{request.QueryString}";
    }
}
