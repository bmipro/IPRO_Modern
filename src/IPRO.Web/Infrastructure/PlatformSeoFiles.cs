using System.Security;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace IPRO.Web.Infrastructure;

// 509 (2026-09-21): robots.txt and sitemap.xml for the platform's OWN public pages. Adviser sites
// have had both since the SEO wave (PublicWebsiteController, one per site, by host); the platform
// host and its brand names answered 404, found by the independent review before the LinkedIn launch.
// One sitemap, the same on every one of these hosts: each public page once, at the address it is
// known by (PlatformAliasHosts.PageUrl), so a page a brand name serves is listed under that name and
// never twice. Search Console accepts another host's addresses once both are verified in one account.
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

    public static string Sitemap(IConfiguration configuration)
    {
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
        foreach (var location in PublicPages.Select(page => PlatformAliasHosts.PageUrl(configuration, page)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            xml.Append("  <url><loc>").Append(SecurityElement.Escape(location)).Append("</loc></url>\n");
        }
        xml.Append("</urlset>");
        return xml.ToString();
    }
}
