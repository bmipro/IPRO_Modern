using System;

namespace IPRO.Utility;

// The one way to write a portal link as a string. (TODO 446, 2026-09-02.)
//
// THE URL-SPACE RULE (DOCS/INVARIANTS.md, enforced in IPRO.Web Program.cs): on an agent's own host --
// their 247advisers.com subdomain or a connected domain -- a bare path is ALWAYS the public website;
// the agent portal lives ONLY under /portal. Tag-helper links get this for free because the "portal"
// route is registered first. Links built as STRINGS do not, and every one of them 404'd on an agent
// host with the public site's "We couldn't find that page": the AI Daily Assistant's "View" (stored
// on the insight by the digest job), the client-timeline "Open related item", the marketing-calendar
// events, and the leads return-URL default. They all worked on app.iproadvisers.com, which is why
// nobody noticed until the owner's first morning of testing on his own domain.
//
// Never-shadowed prefixes (Account, Billing, health, media, ...) are exempt from the rule by design
// and are NOT routed through here.
public static class PortalPaths
{
    public const string Prefix = "/portal";

    // "/WebsiteLeads?status=new" -> "/portal/WebsiteLeads?status=new". Idempotent. Absolute and
    // protocol-relative URLs pass through untouched; null/blank stays null.
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim();

        if (p.StartsWith("//", StringComparison.Ordinal) || p.Contains("://", StringComparison.Ordinal))
            return p;

        if (p.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && (p.Length == Prefix.Length || p[Prefix.Length] is '/' or '?' or '#'))
            return p;

        return p.StartsWith('/') ? Prefix + p : Prefix + "/" + p;
    }

    // For literals and interpolations at the call site, where null is never intended.
    public static string To(string path) =>
        Normalize(path) ?? throw new ArgumentException("A portal path cannot be blank.", nameof(path));
}
