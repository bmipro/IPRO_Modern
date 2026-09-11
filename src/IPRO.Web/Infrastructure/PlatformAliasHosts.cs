using Microsoft.Extensions.Configuration;

namespace IPRO.Web.Infrastructure;

// 477 (2026-09-11): the old public names point at the new site at launch. A request on any of them
// gets a permanent redirect to the platform, so the old names keep working over HTTPS and search
// engines carry their rankings across. App:AliasHosts lists them, comma or semicolon separated,
// each optionally with its own landing path -- the owner's call (09-11): iproadvisers.com goes to
// the home, iproaccountants.com to /accountants, ipromortgages.com to /mortgage, so each domain
// keeps addressing its own audience without being a site of its own. Until the setting exists
// nothing is an alias, so this ships ahead of the DNS change and launch day is DNS only.
//
//   App:AliasHosts = "www.iproadvisers.com,iproadvisers.com,www.iproaccountants.com=/accountants,iproaccountants.com=/accountants"
public static class PlatformAliasHosts
{
    public const string ConfigKey = "App:AliasHosts";

    public static bool IsAlias(IConfiguration configuration, string? host) => Find(configuration, host) != null;

    // The absolute target for a request on this host: the platform base plus the host's own path
    // ("/" when none is configured, or the host is not an alias). Never the old path: an old path
    // sent to the new site would only land on a 404.
    public static string RedirectTarget(IConfiguration configuration, string? host = null)
    {
        var baseUrl = (configuration["App:BaseUrl"] ?? "https://app.iproadvisers.com").Trim().TrimEnd('/');
        var path = Find(configuration, host)?.Path ?? "/";
        return baseUrl + path;
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
