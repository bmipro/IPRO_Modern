using System;
using System.Collections.Generic;

namespace IPRO.DataAccess;

// 632 L-7 (2026-11-24): the page addresses iPro keeps for itself on every adviser's domain. A page's
// address is "/" + its slug, and Program.cs (IsNeverShadowedPrefix, and /portal before it) hands these
// first segments to iPro, never to the adviser's site, so a page given one of them could never be
// reached by a visitor. The builder importer refused them from the start (BuilderContract.ReservedSlugs
// is this list); iPro's own page editor, SuperAdmin's starter pages and the starter articles did not
// check at all -- the audit of 2026-09-17 found it for "t" alone. One list for every door that makes a
// page; BuilderImport571Tests keeps it in step with the router. "sitemap" and "robots" are kept too:
// /sitemap.xml and /robots.txt are iPro's on every site, and a page beside them would only confuse.
public static class ReservedPageAddresses
{
    public static readonly string[] All =
    {
        "account", "billing", "publicwebsite", "media", "hangfire", "health", "error",
        "clientportal", "clientportalaccount", "clientportalappointments", "clientportaldocuments",
        "clientportalmessages", "clientportalpreferences", "clientportalprofile",
        "invoice", "poll", "testimonial", "email-preferences", "t",
        "portal", "sitemap", "robots"
    };

    private static readonly HashSet<string> Set = new(All, StringComparer.OrdinalIgnoreCase);

    public static bool IsReserved(string? slug) =>
        !string.IsNullOrWhiteSpace(slug) && Set.Contains(slug.Trim().Trim('/'));

    // What the adviser (or SuperAdmin) is told; the address as it would be typed after the domain.
    public static string Refusal(string slug) =>
        $"\"/{slug}\" is an address iPro keeps for itself on every website, so visitors could never reach a page there. Choose another address for this page.";

    // For a page saved with an address it already had before this rule: the save goes through (nothing
    // else on the form is lost), and the adviser is told why the page cannot be seen.
    public static string StillThere(string slug) =>
        $"This page's address \"/{slug}\" is one iPro keeps for itself, so visitors cannot reach the page. Give it another address to make it visible.";
}
