using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// 591 (2026-10-18): whether a host is still named by a domain row -- its www name, its short address or the
// name it was added as, any adviser's. Asked before a removed domain's host is deleted from Azure (My Website's
// Remove, SuperAdmin's delete of an adviser), with the row being removed already gone: a binding and its
// certificate are the host's, and a host another row still names is that row's live site. The claim check
// (WebsiteController.DescribeDomainClaimAsync) keeps a second adviser from adding such a name; rows from
// before it (2026-08-05), or a race past it, could still name one. Leaving a binding behind costs nothing; the
// domain check of the row that keeps it uses it.
//
// Compared in small letters, by the database's own collation (which also ignores accents): a near-miss
// leaves a binding in place, never deletes one.
public static class AgentDomainHosts
{
    // 592: the names a domain row may hold in Azure, each once: the name it was added as, its www name, and its
    // short address when that was ever pointed straight at the platform (553: a forwarded short address never
    // had anything in Azure). What is removed from Azure when the row goes (DomainCleanupQueue).
    public static IReadOnlyList<string> HostsOf(IPRO.Entities.AgentDomain domain)
    {
        var shortAddressInAzure = domain.RootAzureBindingStatus != IPRO.Entities.AgentDomainStatus.NotConfigured
                                  || domain.RootDnsStatus == IPRO.Entities.AgentDomainStatus.Bound;
        return new[] { domain.DomainName, domain.WwwDomain, shortAddressInAzure ? domain.RootDomain : null }
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(host => host!)
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // 592: provenOnly counts only rows whose adviser has proven the domain (OwnershipVerifiedAt): an unproven row
    // is served nothing and connected nothing, so a binding it "names" is nobody's site, and once it is proven
    // the domain check binds the name again. An adviser's setup address (AgentUsers.DomainName) counts too: the
    // public site still serves it, and SuperAdmin may have set one outside the free sites' zone before 592.
    public static async Task<bool> StillNamedAsync(IPRODbContext db, string host, CancellationToken cancellationToken = default, bool provenOnly = false)
    {
        var name = (host ?? string.Empty).ToLowerInvariant();
        if (await db.AgentDomains.AnyAsync(d =>
                (!provenOnly || d.OwnershipVerifiedAt != null) &&
                (d.DomainName.ToLower() == name || d.RootDomain.ToLower() == name || d.WwwDomain.ToLower() == name),
                cancellationToken))
            return true;
        return provenOnly && await db.AgentUsers.AnyAsync(u => u.DomainName.ToLower() == name, cancellationToken);
    }
}
