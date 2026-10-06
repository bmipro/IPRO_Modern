using System.Net;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace IPRO.Web.Infrastructure;

// 553 (2026-10-06): a custom domain's short address (example.com beside www.example.com) that
// points straight at this app goes to its www name -- permanently, with the page path and the
// query kept.
//
// Why the app does this itself. The setup steps used to offer one way to make the short address
// work, the registrar's forwarding, and a registrar forwards the HOME address only: on the day the
// first customer with an existing website moved in (a bakery whose site had lived on the short
// address for years), GoDaddy's forwarder answered 404 for every one of its menu pages, and the
// same was measured on three domains already on the platform. Pointed here instead (an A record;
// DomainCheckService binds and secures the name), every old link, bookmark and search result
// arrives -- and this sends it on to the page.
//
// Only once the www name is connected and secured: before that the redirect would send visitors
// into a certificate warning, so the site is simply served on the short address meanwhile.
public static class ShortAddressRedirect
{
    // How long an answer is kept per host, found or not. A short address that has just gone live
    // starts redirecting within this time; nothing here is worth a database read per page view.
    public static readonly TimeSpan Remembered = TimeSpan.FromMinutes(5);

    // The pipeline's call, right after the platform's own alias names. True: the response is a
    // permanent redirect and the request is finished.
    public static async Task<bool> TryHandleAsync(HttpContext context, IConfiguration configuration)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)) return false;

        // The certificate authority proves a bare name by fetching a token under /.well-known over
        // the A record. It must never be sent elsewhere (the same exemption PlatformAliasHosts makes).
        if (request.Path.StartsWithSegments("/.well-known", StringComparison.OrdinalIgnoreCase)) return false;

        var host = (request.Host.Host ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
        if (!CouldBeShortAddress(host, configuration)) return false;

        var target = await TargetHostAsync(context.RequestServices, host);
        if (target == null) return false;

        // Permanent, but not for ever (507): a browser may keep a 301 with no lifetime indefinitely.
        context.Response.Headers.CacheControl = PlatformAliasHosts.RedirectLifetime;
        // 493: Path is the DECODED path; ToUriComponent re-escapes it for the Location header.
        context.Response.Redirect(
            "https://" + target + request.PathBase.ToUriComponent() + request.Path.ToUriComponent() + request.QueryString.Value,
            permanent: true);
        return true;
    }

    // The cheap half: names that cannot be a custom domain's short address never reach the lookup.
    // A www name is the address being redirected TO; the platform's own hosts, the free-site zone
    // and local addresses are not customer domains at all.
    internal static bool CouldBeShortAddress(string host, IConfiguration configuration)
    {
        if (host.Length == 0 || !host.Contains('.')) return false;
        if (host.StartsWith("www.", StringComparison.Ordinal)) return false;
        if (IPAddress.TryParse(host, out _)) return false;
        if (host.EndsWith(".azurewebsites.net", StringComparison.Ordinal)) return false;
        if (host.StartsWith("admin.", StringComparison.Ordinal)) return false;

        var temporaryRoot = (configuration["App:TemporarySiteRootDomain"] ?? "247advisers.com").Trim().Trim('.').ToLowerInvariant();
        if (host == temporaryRoot || host.EndsWith("." + temporaryRoot, StringComparison.Ordinal)) return false;

        if (Uri.TryCreate(configuration["App:BaseUrl"], UriKind.Absolute, out var baseUri) &&
            string.Equals(host, baseUri.Host, StringComparison.OrdinalIgnoreCase)) return false;

        foreach (var platform in (configuration["App:PlatformDomains"] ?? string.Empty)
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(host, platform.Trim('.'), StringComparison.OrdinalIgnoreCase)) return false;
        }

        return !PlatformAliasHosts.IsAlias(configuration, host);
    }

    // The www name this short address belongs to, when that name is live; null otherwise. A
    // database that cannot answer never fails the request: the page is served where it was asked for.
    internal static async Task<string?> TargetHostAsync(IServiceProvider services, string host)
    {
        var cache = services.GetService<IMemoryCache>();
        var key = "short-address:" + host;
        if (cache != null && cache.TryGetValue(key, out string? remembered)) return string.IsNullOrEmpty(remembered) ? null : remembered;

        string target;
        try
        {
            var db = services.GetRequiredService<IPRODbContext>();
            var row = await db.AgentDomains.AsNoTracking()
                .Where(d => d.RootDomain == host && d.DomainName != host)
                .Select(d => new { d.DomainName, d.AzureBindingStatus, d.SslStatus })
                .FirstOrDefaultAsync();

            // "SslBound": rows written before the status scheme settled carry the legacy value (the
            // same allowance the canonical-host rule and ClientPortalUrls make).
            var live = row != null &&
                       row.AzureBindingStatus == AgentDomainStatus.Bound &&
                       (row.SslStatus == AgentDomainStatus.Bound || row.SslStatus == "SslBound");
            target = live ? row!.DomainName.Trim().Trim('.').ToLowerInvariant() : string.Empty;
        }
        catch (Exception ex)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger(typeof(ShortAddressRedirect).FullName!)
                .LogWarning(ex, "Short-address lookup failed for {Host}; serving the request where it arrived", host);
            return null;
        }

        cache?.Set(key, target, Remembered);
        return target.Length == 0 ? null : target;
    }
}
