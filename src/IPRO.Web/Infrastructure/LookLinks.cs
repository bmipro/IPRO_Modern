using System.Net;
using System.Text.RegularExpressions;

namespace IPRO.Web.Infrastructure;

// 585 (the owner's bench walk-through, 2026-10-19): a look at a Template Builder package (the adviser's
// "See it on my site" and review, SuperAdmin's 571 look) draws the site through its real design, with the
// site's own addresses: the menu, the buttons and the footer say "/about". On iPro's host that is iPro's
// address, so a click left the look for a "Page not found". While a look is drawn (PublicWebsite/Index.cshtml),
// every link a visitor can click to one of the site's own pages goes to the same look instead: Look is its
// address, with the key and the choices it was opened with, and the page is page=<that page>; the home page
// is the look itself. Links anywhere else (another site, mail, a phone number, a place on the page), and
// what a visitor does not click (styles, scripts, forms), stay as they are.
public sealed partial record LookLinks(string Look, string CurrentSlug)
{
    public const string LookPath = "/PublicWebsite/PreviewBuilderImport";

    public static LookLinks For(int id, string key, int? design, string? without, string? keep, string? sections, string? moved, string currentSlug)
    {
        static string Choice(string name, string? value) =>
            string.IsNullOrEmpty(value) ? string.Empty : "&" + name + "=" + Uri.EscapeDataString(value);
        var look = $"{LookPath}?id={id}&key={Uri.EscapeDataString(key)}"
            + (design.HasValue ? "&design=" + design.Value : string.Empty)
            + Choice("without", without) + Choice("keep", keep) + Choice("sections", sections) + Choice("moved", moved);
        return new LookLinks(look, currentSlug);
    }

    public string Rewrite(string html) => Anchor().Replace(html, match =>
    {
        var target = Target(WebUtility.HtmlDecode(match.Groups["href"].Value));
        return target == null ? match.Value : match.Groups["before"].Value + WebUtility.HtmlEncode(target) + "\"";
    });

    // Where a link of the site's own leads in the look, or null for a link that is not one.
    private string? Target(string href)
    {
        if (!href.StartsWith('/') || href.StartsWith("//", StringComparison.Ordinal)) return null;
        var hash = href.IndexOf('#');
        var fragment = hash >= 0 ? href[hash..] : string.Empty;
        var rest = hash >= 0 ? href[..hash] : href;
        var question = rest.IndexOf('?');
        var path = question >= 0 ? rest[..question] : rest;
        var query = question >= 0 ? rest[(question + 1)..] : string.Empty;

        // The look's own address: a link already in the look, or one written against the request's path
        // (a blog's "read more"), which stays on the page shown.
        if (string.Equals(path, LookPath, StringComparison.OrdinalIgnoreCase))
            return query.Contains("key=", StringComparison.OrdinalIgnoreCase) ? null : Look + "&page=" + Uri.EscapeDataString(CurrentSlug) + fragment;

        var slug = path.Trim('/');
        if (slug.Length == 0) return Look + fragment;
        return Slug().IsMatch(slug) ? Look + "&page=" + Uri.EscapeDataString(slug.ToLowerInvariant()) + fragment : null;
    }

    // An <a> element's href, as Razor writes it (double quotes).
    [GeneratedRegex("(?<before><a\\b[^>]*?\\shref=\")(?<href>[^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex Anchor();

    // A page's address on a site: one part, letters, digits and dashes.
    [GeneratedRegex("\\A[A-Za-z0-9][A-Za-z0-9-]{0,99}\\z")]
    private static partial Regex Slug();
}
