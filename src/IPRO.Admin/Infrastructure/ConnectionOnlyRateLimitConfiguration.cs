using AspNetCoreRateLimit;
using Microsoft.Extensions.Options;

namespace IPRO.Admin.Infrastructure;

// 561 (2026-10-11): the same rule as IPRO.Web's class of this name -- the rate limiter knows a
// visitor by the connection's address only, never by a header the visitor can type. The library's
// configuration falls back to ClientRateLimitOptions.RealIpHeader ("X-Real-IP" by default) when
// IpRateLimitOptions.RealIpHeader is null, so nulling the latter alone left the header resolver in
// place: a malformed X-Real-IP answered 500 and a made-up one side-stepped the limits.
public sealed class ConnectionOnlyRateLimitConfiguration : RateLimitConfiguration
{
    public ConnectionOnlyRateLimitConfiguration(IOptions<IpRateLimitOptions> ipOptions, IOptions<ClientRateLimitOptions> clientOptions)
        : base(ipOptions, clientOptions)
    {
    }

    public override void RegisterResolvers()
    {
        ClientResolvers.Clear();
        IpResolvers.Clear();
        IpResolvers.Add(new IpConnectionResolveContributor());
    }
}
