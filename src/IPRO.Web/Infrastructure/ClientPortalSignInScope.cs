using System.Globalization;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// 586 (2026-10-17): a client's portal sign-in belongs to the adviser whose client it is, and a password
// is only ever checked against ONE adviser's clients. Before this the sign-in checked every adviser's
// client with the email typed, while an adviser can make a sign-in for any email (a client record of
// their own with that email, and the activation link the profile shows them) -- so one sign-in reached
// across advisers: the planted password opened the planted record on the other adviser's own address,
// and the planter's own sign-ins counted as wrong passwords at the other adviser's client (583's review,
// item d).
//
// Which adviser a sign-in page is for, in this order:
//   1. the adviser whose own address the page is on: their domain (AgentDomains, or the website's
//      CustomDomain; with or without www.) or their free <name>.247advisers.com site (their
//      AgentUsers.DomainName). The address decides; an adviser named in the link is ignored there.
//   2. on iPro's shared address, and on any host that is no adviser's, the adviser the link names:
//      ?adviser=<id>. Every sign-in address we hand a client on the shared address carries it
//      (ClientPortalUrls.LoginUrl). An id that is no adviser is an adviser with no clients.
//   3. neither (a link sent before 586, a bookmark, the page a lapsed session is sent to): the
//      controller takes the adviser of the OLDEST client record with that email and a portal password.
//      Nearly every client has one adviser, so their sign-in works as it did. The oldest, so that a
//      record another adviser adds later with the same email never takes the sign-in from the client.
public static class ClientPortalSignInScope
{
    public const string AdviserParameter = "adviser";

    // The adviser the page is for (1 and 2 above), or null for 3.
    public static async Task<int?> ForRequestAsync(IPRODbContext db, IConfiguration configuration, string? host, string? adviser, ILogger? logger = null) =>
        await AdviserForHostAsync(db, configuration, host, logger) ?? AdviserFromLink(adviser);

    // The ?adviser=<id> of a link: digits only, else nothing (a mangled link is a link without one).
    public static int? AdviserFromLink(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    // The adviser whose own address `host` is, or null: iPro's own names, an empty host (it must match
    // nothing: many AgentDomains rows hold an empty RootDomain), a host that is no adviser's. Matched the
    // way the public site matches a host (PublicWebsiteController.FindWebsiteForHostRawAsync), whether the
    // site is published or not, and the free site by the adviser's own record, website or none.
    //
    // Rules the public site does not need (586's review): iPro's own names are never an adviser's, whatever a
    // row says -- My Website lets an adviser type any name, and a row naming the shared address would
    // otherwise make every sign-in there theirs; a domain row counts only once Azure has bound it (the rule
    // PortalUrlHelper.GetSessionBaseUrlAsync uses), so a name merely typed in decides nothing; and a free
    // site is found by its adviser's own record alone (only iPro makes those names). A host that falls
    // through is the shared address's case, which is safe for anyone.
    public static async Task<int?> AdviserForHostAsync(IPRODbContext db, IConfiguration configuration, string? host, ILogger? logger = null)
    {
        var normalized = (host ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
        if (normalized.Length == 0 || IsIproHost(configuration, normalized, logger)) return null;
        var matches = new[] { normalized, Twin(normalized) };

        if (normalized.EndsWith("." + Zone(configuration), StringComparison.Ordinal))
            return await db.AgentUsers.AsNoTracking()
                .Where(a => matches.Contains(a.DomainName.ToLower()))
                .OrderBy(a => a.Id)
                .Select(a => (int?)a.Id)
                .FirstOrDefaultAsync();

        var domains = await db.AgentDomains.AsNoTracking()
            .Where(d => matches.Contains(d.DomainName.ToLower()) || matches.Contains(d.RootDomain.ToLower()) || matches.Contains(d.WwwDomain.ToLower()))
            .OrderBy(d => d.Id)
            .Select(d => new { d.AgentUserId, d.AgentWebsiteId, d.AzureBindingStatus })
            .ToListAsync();
        if (domains.Count > 0)
        {
            var bound = domains.FirstOrDefault(d => d.AzureBindingStatus == AgentDomainStatus.Bound);
            if (bound == null) return null;   // named, never bound: not this adviser's address (yet)
            if (bound.AgentUserId > 0) return bound.AgentUserId;
            return await db.AgentWebsites.AsNoTracking()
                .Where(w => w.Id == bound.AgentWebsiteId)
                .Select(w => (int?)w.AgentUserId)
                .FirstOrDefaultAsync();
        }

        // An older site known only by its website's own field, as the public site still finds it.
        return await db.AgentWebsites.AsNoTracking()
            .Where(w => matches.Contains(w.CustomDomain.ToLower()))
            .OrderBy(w => w.Id)
            .Select(w => (int?)w.AgentUserId)
            .FirstOrDefaultAsync();
    }

    // iPro's own names, with or without www.: the shared address (App:BaseUrl's host and
    // App:PlatformDomains), the brand names (App:AliasHosts), the free sites' own zone, the admin host, the
    // App Service's own name and a local machine. An adviser's free site (<name>.<zone>) is theirs. A name
    // iPro starts to serve goes into those settings when it goes live, or it is not known here. A setting
    // that cannot be read makes every host iPro's: the shared address's rules, safe for anyone.
    public static bool IsIproHost(IConfiguration configuration, string normalizedHost, ILogger? logger = null)
    {
        try
        {
            return IsIproName(configuration, normalizedHost) || IsIproName(configuration, Twin(normalizedHost));
        }
        catch (UriFormatException ex)
        {
            // Safe, but "the address decides" is off for every adviser until the setting is fixed: say so once.
            if (Interlocked.Exchange(ref _unreadableSettingsWarned, 1) == 0)
                logger?.LogWarning(ex, "Client portal sign-in: the settings naming iPro's own addresses (App:BaseUrl, App:AliasHosts) cannot be read, so every host answers as the shared address until they are fixed");
            return true;
        }
    }

    private static int _unreadableSettingsWarned;

    private static bool IsIproName(IConfiguration configuration, string host)
    {
        // The names configured as iPro's come first, wherever they sit.
        if (PlatformSeoFiles.IsPlatformOrAlias(configuration, host)) return true;   // App:BaseUrl's host, App:AliasHosts
        var configured = (configuration["App:PlatformDomains"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Trim('.').ToLowerInvariant());
        var admin = (configuration["App:AdminDomain"] ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
        if (configured.Contains(host) || (admin.Length > 0 && host == admin) || host.StartsWith("admin.", StringComparison.Ordinal))
            return true;

        var zone = Zone(configuration);
        if (host == zone) return true;
        if (host.EndsWith("." + zone, StringComparison.Ordinal)) return false;   // an adviser's free site
        return PortalUrlHelper.IsAppHost(host, configuration);                   // a local machine, *.azurewebsites.net
    }

    private static string Zone(IConfiguration configuration) =>
        (configuration["App:TemporarySiteRootDomain"] ?? "247advisers.com").Trim().Trim('.').ToLowerInvariant();

    private static string Twin(string host) =>
        host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : "www." + host;

    // The name a sign-in counts under when the email is no client of the page's adviser (SignInLockout's
    // SignInNameLocks keeps only a hash of it): one count per adviser, and one for the shared address with
    // no adviser named. The page comes first and the email last, after a line break the page's part never
    // contains, so no typed email -- line breaks and all -- can land on another page's count. (586's review:
    // a count shared that way would let a stranger read from the lock, without locking anyone, whether an
    // email is an adviser's client.)
    public static string LockName(int? adviser, string normalizedEmail) =>
        (adviser is { } id ? $"adviser {id}" : "shared") + (char)0x0A + normalizedEmail;
}
