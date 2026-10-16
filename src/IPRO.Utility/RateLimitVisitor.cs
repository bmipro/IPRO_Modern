using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;

namespace IPRO.Utility;

// 582 (2026-10-16). Who a visitor is, for the per-visitor limits on sign-in and the public forms (both
// apps' EndpointRateLimits). It is the connection's address and nothing the visitor can type: by the
// time the limiter runs, UseForwardedHeaders has replaced Azure's front end with the address the front
// end reports (the last X-Forwarded-For entry, believed only from Azure's own networks; anything a
// visitor writes into that header sits to the left of it and is ignored).
//
// An IPv6 visitor is their /64, the block one home or office is given: counted one address at a time,
// anyone with a block (2^64 addresses) would have a fresh allowance for every request. An IPv4 address
// written the IPv6 way (::ffff:a.b.c.d) is that IPv4 visitor. No address at all (never seen behind
// Azure) is one shared visitor, so it is limited like everyone else rather than let through.
public static class RateLimitVisitor
{
    public const string Unknown = "unknown";

    public static string Key(HttpContext context) => Key(context.Connection.RemoteIpAddress);

    public static string Key(IPAddress? address)
    {
        if (address == null) return Unknown;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();

        var block = address.GetAddressBytes();
        Array.Clear(block, 8, 8);
        return new IPAddress(block) + "/64";
    }
}
