using System;

namespace IPRO.Entities;

// 646 (the owner, 2026-11-01: "make it available for those that already have website and would like to use the
// rest of features i.e. the other 90%"; price decided 2026-11-17: the same monthly prices, no setup fee; built
// 2026-11-18). A professional who keeps the website they already have uses iPro for everything else: clients,
// follow-ups, invoicing, newsletters and the client portal.
//
// What changes for them: iPro's site stays written and published underneath (its leads need the website row, and
// they can switch to it whenever they like), but its address shows only a short card that points to their own
// website; the portal's Website menu drops Domain and Analytics, and My Website becomes the page where they keep
// their address and copy the lead form for their own site; the e-cards, e-letters and newsletters show their own
// website's address; sign-up charges no setup fee.
//
// Its own small table, NOT columns on AgentUsers or AgentWebsites, for AgentFollowUpReminder's reason. No row means
// "uses iPro's website", which is every account before this.
public class AgentOwnWebsite
{
    public const int MaxUrlLength = 300;

    public int AgentUserId { get; set; }
    // Their website, an absolute http(s) address; null until they give it (the lead form then only opens as
    // a link, never inside another page).
    public string? WebsiteUrl { get; set; }
    // The lead form's public key: /PublicWebsite/LeadForm/{FormKey}. Random, so the address names nobody.
    public string FormKey { get; set; } = NewFormKey();
    public DateTime ChosenAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public AgentUser AgentUser { get; set; } = null!;

    public static string NewFormKey() => Guid.NewGuid().ToString("N");

    // An absolute http(s) address with a real host, or null.
    public static string? NormalizeUrl(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Length > MaxUrlLength) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        if (string.IsNullOrWhiteSpace(uri.Host) || !uri.Host.Contains('.') || uri.Host.Contains(' ')) return null;
        var absolute = uri.GetLeftPart(UriPartial.Path);
        return absolute.Length > MaxUrlLength ? null : absolute;
    }

    // The address as their clients would read it: the host, without the scheme or a trailing slash.
    public static string DisplayHost(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    // The origins a page of their site can be served from -- the host and its www twin -- which may show the
    // lead form inside one of their pages (Content-Security-Policy frame-ancestors). Empty with no address.
    public static string[] FrameOrigins(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host)) return Array.Empty<string>();
        var host = uri.Host.ToLowerInvariant();
        var twin = host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : "www." + host;
        return new[] { $"https://{host}", $"https://{twin}", $"http://{host}", $"http://{twin}" };
    }
}
