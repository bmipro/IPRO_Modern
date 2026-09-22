using IPRO.DataAccess;
using Microsoft.Extensions.Configuration;

namespace IPRO.Web.Infrastructure;

// 512 (2026-09-22): the request side of PlatformVisits -- called by the four public pages (home,
// accountants, mortgage, Register) and once more when a registration succeeds. Never fails a page:
// everything here is inside a try, and a request with no HttpContext (a controller under test) is
// simply not a visit.
public static class PlatformVisitRecorder
{
    // A referrer is an origin only when it is NOT one of our own names: the platform host, any
    // alias (www.iproaccountants.com -> Register is our visitor walking our own pages), or another
    // name of the same site as the one the visitor is on.
    public static string ExternalReferrerHost(string? referer, IConfiguration configuration, string publicHost)
    {
        if (!Uri.TryCreate(referer, UriKind.Absolute, out var uri)) return string.Empty;
        var host = uri.Host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0) return string.Empty;
        if (PlatformSeoFiles.IsPlatformOrAlias(configuration, host)) return string.Empty;
        var platform = new Uri(PlatformAliasHosts.PlatformBase(configuration)).Host;
        if (Registered(host) == Registered(publicHost) || Registered(host) == Registered(platform)) return string.Empty;
        return host;
    }

    public static async Task RecordAsync(HttpContext? context, IPRODbContext db, string path)
    {
        try
        {
            if (context == null) return;
            var request = context.Request;
            var publicHost = PlatformAliasHosts.PublicHost(context);
            var configuration = context.RequestServices?.GetService(typeof(IConfiguration)) as IConfiguration;
            var referrer = configuration == null
                ? string.Empty
                : ExternalReferrerHost(request.Headers.Referer.ToString(), configuration, publicHost);
            var view = PlatformVisits.Build(
                publicHost, path,
                request.Query["utm_source"].ToString(), request.Query["utm_medium"].ToString(), request.Query["utm_campaign"].ToString(),
                referrer,
                request.Headers.UserAgent.ToString(),
                string.Equals(request.Headers["DNT"].ToString(), "1", StringComparison.Ordinal),
                context.Connection.RemoteIpAddress?.ToString(),
                DateTime.UtcNow);
            if (view == null) return;
            await PlatformVisits.RecordAsync(db, view);
        }
        catch (Exception ex)
        {
            Warn(context, ex, "Platform visit could not be recorded for {Path}.", path);
        }
    }

    public static async Task RecordSignupAsync(HttpContext? context, IPRODbContext db, int agentUserId)
    {
        try
        {
            if (context == null) return;
            var hash = PlatformVisits.VisitorHash(
                context.Connection.RemoteIpAddress?.ToString(),
                context.Request.Headers.UserAgent.ToString(),
                DateTime.UtcNow);
            await PlatformVisits.RecordSignupOriginAsync(db, agentUserId, hash, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            Warn(context, ex, "Sign-up origin could not be recorded for agent {AgentId}.", agentUserId);
        }
    }

    private static void Warn(HttpContext? context, Exception ex, string message, object argument)
    {
        var factory = context?.RequestServices?.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        factory?.CreateLogger(nameof(PlatformVisitRecorder)).LogWarning(ex, message, argument);
    }

    private static string Registered(string name)
    {
        var labels = name.Trim().TrimEnd('.').ToLowerInvariant().Split('.');
        return labels.Length <= 2 ? string.Join('.', labels) : string.Join('.', labels[^2..]);
    }
}
