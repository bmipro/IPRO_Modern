using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace IPRO.Web.Infrastructure;

// 457 (2026-09-02): the ONE answer to "where is this agent's client portal?". The client-facing
// paths (ClientPortal, ClientPortalAccount, ...) are never-shadowed so a client uses the portal on
// the AGENT'S domain; every link we hand a client must therefore be built on that domain, not on
// the platform host. The owner's rule (2026-09-02): the website's custom domain when it is actually
// serving (bound, SSL bound -- the same predicate PublicWebsiteController uses for its canonical
// host, legacy "SslBound" included), otherwise the platform host. The free <name>.247advisers.com
// site also serves the portal, but is deliberately not used for links we hand a client.
public static class ClientPortalUrls
{
    public static async Task<string> GetBaseUrlAsync(IPRODbContext db, int agentId, IConfiguration configuration)
    {
        var custom = await db.AgentWebsites.AsNoTracking()
            .Where(w => w.AgentUserId == agentId && w.CustomDomain != null && w.CustomDomain != "")
            .Select(w => new { w.Id, w.CustomDomain })
            .FirstOrDefaultAsync();

        if (custom != null)
        {
            var host = custom.CustomDomain!.Trim().Trim('.').ToLowerInvariant();
            var healthy = await db.AgentDomains.AsNoTracking().AnyAsync(d =>
                d.AgentWebsiteId == custom.Id &&
                (d.DomainName.ToLower() == host || d.WwwDomain.ToLower() == host || d.RootDomain.ToLower() == host) &&
                d.AzureBindingStatus == AgentDomainStatus.Bound &&
                (d.SslStatus == AgentDomainStatus.Bound || d.SslStatus == "SslBound"));
            if (healthy) return $"https://{host}";
        }

        return WebAppUrlHelper.GetWebAppBaseUrl(configuration);
    }

    public static string LoginUrl(string baseUrl) => $"{baseUrl.TrimEnd('/')}/ClientPortalAccount/Login";

    public static string ActivateUrl(string baseUrl, string token) =>
        $"{baseUrl.TrimEnd('/')}/ClientPortalAccount/Activate?token={System.Net.WebUtility.UrlEncode(token)}";
}
