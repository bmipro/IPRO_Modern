using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace IPRO.Web.Infrastructure;

// 477 (2026-09-11): the old public names point at the new site at launch. App:AliasHosts lists
// them, comma or semicolon separated, each optionally with its own landing path -- the owner's call
// (09-11): iproadvisers.com goes to the home, iproaccountants.com to /accountants,
// ipromortgages.com to /mortgage, so each domain keeps addressing its own audience without being
// a site of its own. Until the setting exists nothing is an alias, so this shipped ahead of the DNS
// change and the switch day is DNS only.
//
//   App:AliasHosts = "www.iproadvisers.com,iproadvisers.com,www.iproaccountants.com=/accountants,iproaccountants.com=/accountants"
//
// 484 (2026-09-12): a name WITH a landing path is a brand domain and serves that page under its own
// name -- the owner, seeing ipromortgages.com redirect the day it went live: the brand stays in the
// address bar. The request is re-addressed to the platform host and the landing path and carries on
// down the pipeline; the files the page loads and the live preview frame come along the same way.
// Everything else on the brand name (Register, Sign in, the preview pages, Terms, Privacy, an old
// deep link) goes to the platform, permanently, with its path and query kept, because accounts and
// the rest of the site live there. A name WITHOUT a landing path (the iproadvisers.com pair) is the
// platform's own brand and keeps redirecting to the home. The page's canonical tag still names the
// platform address, so search keeps one authority; the brand name is the door.
//
// 507 (2026-09-21): the evening the four names went live the owner typed www.iproadvisers.com,
// watched it become app.iproadvisers.com, and asked for the same as 484: the name stays. So an entry
// written "name=/" is a brand domain whose own page is the HOME page (served in place, with its files;
// everything else still goes to the platform), while a bare "name" keeps only forwarding. The first
// such name is also the address the home page is known by (HomeBase): its canonical tag, og:url and
// the landing pages' links to its sections -- the owner's choice, www.iproadvisers.com.
//   App:AliasHosts = "www.iproadvisers.com=/,iproadvisers.com=/,www.iproaccountants.com=/accountants,..."
// And a forward now says how long it may be remembered: a 301 with no cache lifetime can sit in a
// browser indefinitely, which is why visitors from the first evening kept landing on app. after this.
//
// 509 (2026-09-21, from an independent review of the public names the owner commissioned): every
// public page is known by the brand name that serves it (PageUrl: the accountants page told search
// engines app.iproadvisers.com/accountants while living at www.iproaccountants.com); an old-style
// page address left over from the legacy sites (/websites_for_advisers.html, /index.php) goes to the
// name's own front page instead of a 404; a forward from the root keeps the visitor's parameters;
// and a request served under a brand name remembers the name it came in on (PublicHost), because
// robots.txt must name that host's own sitemap.
public static class PlatformAliasHosts
{
    public const string ConfigKey = "App:AliasHosts";

    public static bool IsAlias(IConfiguration configuration, string? host) => Find(configuration, host) != null;

    public static string PlatformBase(IConfiguration configuration) =>
        (configuration["App:BaseUrl"] ?? "https://app.iproadvisers.com").Trim().TrimEnd('/');

    // The address the home page is known by: the first alias whose own page is the home ("name=/"),
    // over https; the platform address when there is none (every local and test configuration).
    public static string HomeBase(IConfiguration configuration) => PageUrl(configuration, "/").TrimEnd('/');

    // 509: the address a public page is KNOWN by -- its canonical tag, og:url, share image and sitemap
    // entry. The first name whose own page it is, over https ("https://www.iproaccountants.com/" for
    // "/accountants"); the platform address and the path when no name serves it.
    public static string PageUrl(IConfiguration configuration, string path)
    {
        var wanted = "/" + (path ?? "").Trim().Trim('/');
        foreach (var alias in All(configuration))
            if (alias.OwnPage && string.Equals(alias.Path, wanted, StringComparison.OrdinalIgnoreCase))
                return "https://" + alias.Host.ToLowerInvariant() + "/";
        return PlatformBase(configuration) + wanted;
    }

    // 509: TryHandle re-addresses a brand name's own page to the platform host, so further down the
    // pipeline Request.Host is the platform's. The name the visitor actually used is kept here.
    public const string PublicHostItem = "IPRO.PublicHost";

    public static string PublicHost(HttpContext context) =>
        context.Items.TryGetValue(PublicHostItem, out var host) && host is string name && name.Length > 0
            ? name
            : context.Request.Host.Host;

    // 509: addresses of the legacy sites' pages. Search engines and old bookmarks still hold them; none
    // of them is a file this app serves (wwwroot has no page of these kinds).
    private static readonly string[] LegacyPageExtensions = { ".html", ".htm", ".php", ".asp", ".aspx", ".cfm", ".jsp", ".shtml" };

    public static bool IsLegacyPage(PathString path)
    {
        if (!path.HasValue) return false;
        var value = path.Value!;
        foreach (var extension in LegacyPageExtensions)
            if (value.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // The absolute target for the root of this host: the platform base plus the host's own landing
    // path ("/" when none is configured, or the host is not an alias).
    public static string RedirectTarget(IConfiguration configuration, string? host = null) =>
        RedirectTarget(configuration, host, PathString.Empty, QueryString.Empty);

    // For any other path the platform gets the same path and query: the landing page's own links
    // (Register with its business type, Terms, the preview) must land where they point.
    public static string RedirectTarget(IConfiguration configuration, string? host, PathString path, QueryString query)
    {
        var baseUrl = PlatformBase(configuration);
        // 509: the root keeps its query too -- a campaign link to a name that only forwards arrived at
        // the platform without its utm_ parameters.
        if (!path.HasValue || path == "/") return baseUrl + (Find(configuration, host)?.Path ?? "/") + query.Value;
        // 493: PathString.Value is the DECODED path; a %0A or a space in it made a Location header
        // Kestrel refuses (a 500 on a public host). ToUriComponent re-escapes it; the query string is
        // carried as received, already escaped.
        return baseUrl + path.ToUriComponent() + query.Value;
    }

    // What a brand domain serves under its own name: its landing page at "/", the files that page and
    // the preview frame load (anything with a file extension), and the live preview frame itself.
    public static bool ServesInPlace(PathString path)
    {
        if (!path.HasValue || path == "/") return true;
        if (path.StartsWithSegments("/Preview/Site", StringComparison.OrdinalIgnoreCase)) return true;
        var value = path.Value!;
        var lastSegment = value[(value.LastIndexOf('/') + 1)..];
        var dot = lastSegment.LastIndexOf('.');
        return dot > 0 && dot < lastSegment.Length - 1;
    }

    // The one rule the pipeline calls first. True: the response is a permanent redirect and the
    // request is finished. False: carry on -- untouched for the platform host, agent sites and
    // /.well-known (certificate validation on the old names keeps working), or re-addressed to the
    // platform host and the landing path for a brand domain's own page.
    public static bool TryHandle(IConfiguration configuration, HttpContext context)
    {
        var request = context.Request;
        if (request.Path.StartsWithSegments("/.well-known", StringComparison.OrdinalIgnoreCase)) return false;
        var alias = Find(configuration, request.Host.Host);
        if (alias == null) return false;

        if (IsLegacyPage(request.Path))
        {
            // 509: to the front page of the name the visitor used when that name has a page of its own
            // (the closest thing to what the old page was about); to the platform home when it only
            // forwards. The old query string meant something to the old site only.
            var front = alias.OwnPage ? "https://" + request.Host.Value.ToLowerInvariant() + "/" : PlatformBase(configuration) + "/";
            context.Response.Headers.CacheControl = RedirectLifetime;
            context.Response.Redirect(front, permanent: true);
            return true;
        }

        if (alias.OwnPage && ServesInPlace(request.Path))
        {
            context.Items[PublicHostItem] = request.Host.Host.ToLowerInvariant();
            request.Host = HostString.FromUriComponent(new Uri(PlatformBase(configuration)).Authority);
            if (!request.Path.HasValue || request.Path == "/") request.Path = alias.Path;
            return false;
        }

        // Permanent, but not for ever: without a lifetime a browser may keep a 301 indefinitely, and a
        // name that forwards today may serve its own page tomorrow (507 is that story).
        context.Response.Headers.CacheControl = RedirectLifetime;
        context.Response.Redirect(RedirectTarget(configuration, alias.Host, request.Path, request.QueryString), permanent: true);
        return true;
    }

    public const string RedirectLifetime = "public, max-age=3600";

    // OwnPage: the entry was written with "=", so the name serves a page under its own name ("/" is
    // the home page). Without it the name only forwards.
    private sealed record Alias(string Host, string Path, bool OwnPage);

    private static Alias? Find(IConfiguration configuration, string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var wanted = host.Trim().TrimEnd('.');
        foreach (var alias in All(configuration))
            if (string.Equals(alias.Host, wanted, StringComparison.OrdinalIgnoreCase)) return alias;
        return null;
    }

    private static IEnumerable<Alias> All(IConfiguration configuration)
    {
        var configured = configuration[ConfigKey];
        if (string.IsNullOrWhiteSpace(configured)) yield break;
        foreach (var raw in configured.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = raw.Trim();
            if (entry.Length == 0) continue;
            var eq = entry.IndexOf('=');
            var name = (eq < 0 ? entry : entry[..eq]).Trim().TrimEnd('.');
            if (name.Length == 0) continue;
            var path = eq < 0 ? "/" : "/" + entry[(eq + 1)..].Trim().Trim('/');
            yield return new Alias(name, path.Length == 0 ? "/" : path, OwnPage: eq >= 0);
        }
    }
}
