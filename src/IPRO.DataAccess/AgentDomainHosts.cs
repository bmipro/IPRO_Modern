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
    public static Task<bool> StillNamedAsync(IPRODbContext db, string host, CancellationToken cancellationToken = default)
    {
        var name = (host ?? string.Empty).ToLowerInvariant();
        return db.AgentDomains.AnyAsync(d =>
            d.DomainName.ToLower() == name || d.RootDomain.ToLower() == name || d.WwwDomain.ToLower() == name,
            cancellationToken);
    }
}
