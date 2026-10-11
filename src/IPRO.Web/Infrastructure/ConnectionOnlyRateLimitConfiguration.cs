using AspNetCoreRateLimit;
using Microsoft.Extensions.Options;

namespace IPRO.Web.Infrastructure;

// 561 (2026-10-11). The rate limiter must know a visitor by the connection's address and by nothing
// the visitor can type. The library's own configuration adds a resolver that reads the address from
// a request header named by RealIpHeader, and it takes that name from IpRateLimitOptions OR, when
// that is null, from ClientRateLimitOptions -- whose default is "X-Real-IP". So forcing the first
// to null (the fix of the 2026-07-24 audit, H-1) never removed the header resolver. Seen live on
// 2026-10-11: `X-Real-IP: not-an-ip` answered 500 on every host (the library parses the header and
// throws; 69 such errors in two days of logs, from scanners), and a well-formed made-up address was
// taken as the visitor, so the limits on sign-in, registration, password reset and the public forms
// could be side-stepped by changing the header on each request.
//
// This configuration registers the connection resolver alone. The connection's address is the real
// client: UseForwardedHeaders has already replaced Azure's front end with the address it reports,
// from the trusted networks only.
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
