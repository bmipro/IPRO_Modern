using System;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// 552 (2026-10-05): the web address an adviser's clients are shown on e-cards, e-letters and newsletters. The owner
// sent a card that read bahmanmotamed.247advisers.com while his site lives at www.4iPro.com: "I want it to show
// www.4iPro.com since that is the domain I m using". Every composer printed AgentUser.DomainName, the free address
// made at registration, whatever domain the adviser had connected since.
//
// The answer now: the website's custom domain while it is actually serving -- bound and SSL bound, the predicate
// ClientPortalUrls and the public site's canonical host use, so a link we hand a client never points at a domain
// that is still being set up -- written the way the adviser asked (CustomDomainDisplay: the same letters, their own
// capitals); otherwise the free <name>.247advisers.com address, which always serves the same site.
public static class AgentSiteAddress
{
    public static async Task<string> HostAsync(IPRODbContext db, int agentUserId, string? temporaryDomain)
    {
        var site = await db.AgentWebsites.AsNoTracking()
            .Where(w => w.AgentUserId == agentUserId && w.CustomDomain != null && w.CustomDomain != "")
            .Select(w => new { w.Id, w.CustomDomain, w.CustomDomainDisplay })
            .FirstOrDefaultAsync();

        if (site != null)
        {
            var host = site.CustomDomain.Trim().Trim('.').ToLowerInvariant();
            var serving = await db.AgentDomains.AsNoTracking().AnyAsync(d =>
                d.AgentWebsiteId == site.Id &&
                (d.DomainName.ToLower() == host || d.WwwDomain.ToLower() == host || d.RootDomain.ToLower() == host) &&
                d.AzureBindingStatus == AgentDomainStatus.Bound &&
                (d.SslStatus == AgentDomainStatus.Bound || d.SslStatus == "SslBound"));
            if (serving) return Written(host, site.CustomDomainDisplay);
        }

        return (temporaryDomain ?? string.Empty).Trim();
    }

    // The adviser's own capitals when they spell the same host; anything else is ignored rather than shown.
    public static string Written(string host, string? display)
    {
        var written = (display ?? string.Empty).Trim();
        return written.Length > 0 && string.Equals(written, host, StringComparison.OrdinalIgnoreCase) ? written : host;
    }
}
