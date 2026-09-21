using System.Security;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace IPRO.Web.Infrastructure;

// 509 (2026-09-21): robots.txt and sitemap.xml for the platform's OWN public pages. Adviser sites
// have had both since the SEO wave (PublicWebsiteController, one per site, by host); the platform
// host and its brand names answered 404, found by the independent review before the LinkedIn launch.
// Each public page is listed once, at the address it is known by (PlatformAliasHosts.PageUrl), and
// only in the sitemap of its OWN site. 509 listed all three brands' front pages in one file, the same
// on every host; Search Console read it and refused the other two domains' addresses ("URL not
// allowed for a Sitemap at this location") the day it shipped -- 510.
public static class PlatformSeoFiles
{
    // Pages a brand name may serve, then pages that live on the platform host only.
    private static readonly string[] PublicPages = { "/", "/accountants", "/mortgage", "/terms", "/privacy" };

    public static bool IsPlatformOrAlias(IConfiguration configuration, string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var wanted = host.Trim().TrimEnd('.');
        var platform = new Uri(PlatformAliasHosts.PlatformBase(configuration)).Host;
        return string.Equals(wanted, platform, StringComparison.OrdinalIgnoreCase)
            || PlatformAliasHosts.IsAlias(configuration, wanted);
    }

    // The portal sits behind a sign-in: nothing there is for a crawler, and every address in it
    // answers with a redirect to the login page.
    public static string Robots(string publicHost) =>
        $"User-agent: *\nAllow: /\nDisallow: /portal/\nSitemap: https://{publicHost.ToLowerInvariant()}/sitemap.xml\n";

    public static string Sitemap(IConfiguration configuration, string publicHost)
    {
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
        var locations = PublicPages
            .Select(page => PlatformAliasHosts.PageUrl(configuration, page))
            .Where(location => SameSite(new Uri(location).Host, publicHost))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var location in locations)
        {
            xml.Append("  <url><loc>").Append(SecurityElement.Escape(location)).Append("</loc></url>\n");
        }
        xml.Append("</urlset>");
        return xml.ToString();
    }

    // The same registered domain: www.iproadvisers.com, iproadvisers.com and app.iproadvisers.com are
    // one site; www.iproaccountants.com is another. Compared on the last two labels, which is right
    // for every name this platform owns (.com); a name under a two-part suffix such as .co.uk would
    // need the public suffix list.
    private static bool SameSite(string host, string other)
    {
        static string Registered(string name)
        {
            var labels = name.Trim().TrimEnd('.').ToLowerInvariant().Split('.');
            return labels.Length <= 2 ? string.Join('.', labels) : string.Join('.', labels[^2..]);
        }
        return Registered(host) == Registered(other);
    }
}
