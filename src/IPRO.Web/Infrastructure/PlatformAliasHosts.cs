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
public static class PlatformAliasHosts
{
    public const string ConfigKey = "App:AliasHosts";

    public static bool IsAlias(IConfiguration configuration, string? host) => Find(configuration, host) != null;

    public static string PlatformBase(IConfiguration configuration) =>
        (configuration["App:BaseUrl"] ?? "https://app.iproadvisers.com").Trim().TrimEnd('/');

    // The absolute target for the root of this host: the platform base plus the host's own landing
    // path ("/" when none is configured, or the host is not an alias).
    public static string RedirectTarget(IConfiguration configuration, string? host = null) =>
        RedirectTarget(configuration, host, PathString.Empty, QueryString.Empty);

    // For any other path the platform gets the same path and query: the landing page's own links
    // (Register with its business type, Terms, the preview) must land where they point.
    public static string RedirectTarget(IConfiguration configuration, string? host, PathString path, QueryString query)
    {
        var baseUrl = PlatformBase(configuration);
        if (!path.HasValue || path == "/") return baseUrl + (Find(configuration, host)?.Path ?? "/");
        return baseUrl + path.Value + query.Value;
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

        if (alias.Path != "/" && ServesInPlace(request.Path))
        {
            request.Host = HostString.FromUriComponent(new Uri(PlatformBase(configuration)).Authority);
            if (!request.Path.HasValue || request.Path == "/") request.Path = alias.Path;
            return false;
        }

        context.Response.Redirect(RedirectTarget(configuration, alias.Host, request.Path, request.QueryString), permanent: true);
        return true;
    }

    private sealed record Alias(string Host, string Path);

    private static Alias? Find(IConfiguration configuration, string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var configured = configuration[ConfigKey];
        if (string.IsNullOrWhiteSpace(configured)) return null;
        var wanted = host.Trim().TrimEnd('.');
        foreach (var raw in configured.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = raw.Trim();
            if (entry.Length == 0) continue;
            var eq = entry.IndexOf('=');
            var name = (eq < 0 ? entry : entry[..eq]).Trim().TrimEnd('.');
            var path = eq < 0 ? "/" : "/" + entry[(eq + 1)..].Trim().Trim('/');
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) return new Alias(name, path.Length == 0 ? "/" : path);
        }
        return null;
    }
}
