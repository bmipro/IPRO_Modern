using System.Text.RegularExpressions;
using IPRO.Entities;

namespace IPRO.Web.Infrastructure;

// 553 (2026-10-06): the addresses a page had on the adviser's PREVIOUS website.
//
// A business that moves an existing site here brings years of links with it -- search results, a
// Google Business Profile's "Menu" button, a QR code on a printed card -- and every one names an
// address of the old site. The first such customer's pages were /bread-%26-pastries and /about-us
// on the old builder and /bread-pastries and /about here: without this, each was a 404 on the day
// the domain moved.
//
// Two ways an old address finds its page, in this order:
//   1. LISTED: the adviser types it on the page (Page Settings -> Old addresses), as many as the
//      page had. This is the only way that needs any work, and the only one that can say
//      "/about-us is now /about".
//   2. THE SAME WORDS, the old site's way: "bread-&-pastries", "About_Us", "services.html",
//      "about/index.php" become this site's own address form and are looked up as that.
// A page that really exists at the requested address always wins: neither rule is consulted for it.
public static class OldAddresses
{
    // Twelve addresses of up to 150 characters, one per line, always fit the column (2000).
    public const int MaxEntries = 12;
    public const int MaxLength = 150;

    // One address as it is stored and compared: the path only, decoded, lower case, no slash at
    // either end. Accepts what people actually paste -- "https://www.example.com/About-Us/?x=1",
    // "example.com/about-us", "/about-us" and "about-us" are all "about-us".
    public static string Normalize(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0) return string.Empty;

        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var slash = value.IndexOf('/', scheme + 3);
            value = slash < 0 ? string.Empty : value[slash..];
        }
        else if (!value.StartsWith('/'))
        {
            // "example.com/about-us": a first part with a dot, followed by more, is a host name.
            // ("about.html" alone, or "services/tax.html", is a path.)
            var slash = value.IndexOf('/');
            if (slash > 0 && value[..slash].Contains('.')) value = value[slash..];
        }

        foreach (var end in new[] { '?', '#' })
        {
            var at = value.IndexOf(end);
            if (at >= 0) value = value[..at];
        }

        try { value = Uri.UnescapeDataString(value); } catch (UriFormatException) { /* keep it as typed */ }

        value = Regex.Replace(value.Replace('\\', '/'), "/{2,}", "/").Trim().Trim('/').ToLowerInvariant();
        return value;
    }

    // The editor's field: one address per line (a comma works too). Blank, repeated and over-long
    // entries are dropped; at most MaxEntries are kept.
    public static List<string> Parse(string? field)
    {
        var result = new List<string>();
        foreach (var raw in (field ?? string.Empty).Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var address = Normalize(raw);
            if (address.Length == 0 || address.Length > MaxLength || result.Contains(address)) continue;
            result.Add(address);
            if (result.Count == MaxEntries) break;
        }

        return result;
    }

    public static string Store(IEnumerable<string> addresses) => string.Join("\n", addresses);

    // The page an address of the previous website belongs to now, or null. `pages` are the site's
    // published pages; the caller has already established that no page lives AT this address.
    public static WebsitePage? Find(IReadOnlyCollection<WebsitePage> pages, string? requestedPath)
    {
        var wanted = Normalize(requestedPath);
        if (wanted.Length == 0) return null;

        foreach (var page in pages)
        {
            if (!string.IsNullOrEmpty(page.OldAddresses) && Parse(page.OldAddresses).Contains(wanted)) return page;
        }

        var words = WithoutPageFile(wanted);
        if (words.Length == 0)
        {
            // "/index.html", "/default.aspx": the old site's front page.
            return pages.FirstOrDefault(p => p.IsHomePage);
        }

        var slug = Slug(words);
        if (slug.Length == 0 || slug == wanted) return null;
        return pages.FirstOrDefault(p => string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase));
    }

    // This site's own address rule (WebsitePagesController.NormalizeSlug), applied to another site's
    // spelling of the same words.
    public static string Slug(string value) =>
        Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    // "services.html" -> "services"; "about/index.php" -> "about"; "index.html" -> "".
    private static string WithoutPageFile(string path)
    {
        if (!PlatformAliasHosts.IsLegacyPage(new PathString("/" + path))) return path;

        var withoutExtension = path[..path.LastIndexOf('.')];
        var lastSlash = withoutExtension.LastIndexOf('/');
        var file = withoutExtension[(lastSlash + 1)..];
        if (file is "index" or "default" or "home")
        {
            return lastSlash < 0 ? string.Empty : withoutExtension[..lastSlash];
        }

        return withoutExtension;
    }
}
