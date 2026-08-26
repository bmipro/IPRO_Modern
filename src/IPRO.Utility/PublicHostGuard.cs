using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace IPRO.Utility;

// A5-M-SSRF (2026-08-20). DomainCheckService resolves and fetches whatever hostname an agent typed
// into the custom-domain box, then reports back whether it answered. Without screening, that turns
// the server into a free probe of everything it can reach and the public internet cannot:
// localhost, the App Service's own metadata endpoint (169.254.169.254), and anything on the vnet.
//
// The rule: a custom domain is only ever a PUBLIC website. Any name that is an IP literal, or that
// resolves to a loopback / private / link-local / ULA / unspecified address, is not a customer
// domain misconfiguration -- it is a probe, and the check refuses to touch it.
//
// This screens the RESOLVED ADDRESSES, not just the name's shape, so "metadata.mycorp.internal"
// pointing at 169.254.169.254 is caught the same as the raw IP. DNS rebinding between our check
// and our fetch is out of scope here: the fetch target is what the agent's public visitors would
// hit anyway, and nothing internal listens on the App Service's outbound path once the resolved
// set is clean.
public static class PublicHostGuard
{
    /// True when the address must never be fetched by a server-side check.
    public static bool IsBlockedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return true;
            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None)) return true;
            var b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;              // fc00::/7 unique-local
            if (address.IsIPv4MappedToIPv6) return IsBlockedAddress(address.MapToIPv4());

            // M17 (audit): ::ffff: is not the only way to smuggle an IPv4 address inside IPv6.
            // The transition mechanisms each embed one, and every embedded private/loopback
            // address is the same probe wearing different clothes:
            //   ::a.b.c.d            IPv4-compatible (deprecated ::/96)
            //   64:ff9b::a.b.c.d     NAT64 well-known prefix (and 64:ff9b:1::/48 local-use)
            //   2002:AABB:CCDD::/16  6to4 (v4 in bytes 2-5)
            //   2001:0::/32          Teredo (server v4 in bytes 4-7; CLIENT v4 in bytes 12-15,
            //                        each byte XOR 0xFF)
            if (b[0] == 0x20 && b[1] == 0x02)                    // 6to4
            {
                return IsBlockedAddress(new IPAddress(new[] { b[2], b[3], b[4], b[5] }));
            }
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)   // Teredo
            {
                var server = new IPAddress(new[] { b[4], b[5], b[6], b[7] });
                var client = new IPAddress(new[] { (byte)(b[12] ^ 0xFF), (byte)(b[13] ^ 0xFF), (byte)(b[14] ^ 0xFF), (byte)(b[15] ^ 0xFF) });
                return IsBlockedAddress(server) || IsBlockedAddress(client);
            }
            var nat64 = b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B &&
                        (b[4] | b[5] | b[6] | b[7] | b[8] | b[9] | b[10] | b[11]) == 0;
            var nat64Local = b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4] == 0x00 && b[5] == 0x01;
            var v4Compatible = (b[0] | b[1] | b[2] | b[3] | b[4] | b[5] | b[6] | b[7] | b[8] | b[9] | b[10] | b[11]) == 0;
            if (nat64 || nat64Local || v4Compatible)
            {
                return IsBlockedAddress(new IPAddress(new[] { b[12], b[13], b[14], b[15] }));
            }
            return false;
        }

        var v4 = address.GetAddressBytes();
        return v4[0] switch
        {
            0 => true,                                            // 0.0.0.0/8
            10 => true,                                           // 10/8
            100 when v4[1] >= 64 && v4[1] <= 127 => true,         // 100.64/10 CGNAT
            127 => true,                                          // loopback (also caught above)
            169 when v4[1] == 254 => true,                        // link-local incl. 169.254.169.254
            172 when v4[1] >= 16 && v4[1] <= 31 => true,          // 172.16/12
            192 when v4[1] == 168 => true,                        // 192.168/16
            192 when v4[1] == 0 && v4[2] == 0 => true,            // 192.0.0/24 protocol assignments
            198 when v4[1] == 18 || v4[1] == 19 => true,          // 198.18/15 benchmarking
            >= 224 => true,                                       // multicast + reserved + broadcast
            _ => false
        };
    }

    /// True when the hostname itself is an IP literal that must not be fetched.
    public static bool IsBlockedHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        var trimmed = host.Trim().TrimEnd('.').Trim('[', ']');
        return IPAddress.TryParse(trimmed, out var literal) && IsBlockedAddress(literal);
    }

    /// True when any address the name resolves to is one a server-side check must not touch.
    public static bool AnyBlocked(IEnumerable<IPAddress> addresses) => addresses.Any(IsBlockedAddress);

    // H4 (audit): DNS REBINDING defeated the resolve-then-fetch pattern -- the guard resolved and
    // validated, then HttpClient resolved AGAIN internally, and an attacker's nameserver
    // alternating public/private answers put the fetch on the private one. The fix is structural:
    // validation happens AT CONNECT TIME, atomically with the resolve the connection actually
    // uses. A handler built here dials only addresses this guard has just approved; there is no
    // second resolve for an attacker to win.

    /// Test seam: how the pinned handler resolves names. Production = real DNS.
    internal static Func<string, CancellationToken, Task<IPAddress[]>> ResolveHook =
        (host, ct) => Dns.GetHostAddressesAsync(host, ct);

    /// The connect-time gate: every resolved address must be clean, or the connection is refused.
    /// Mixed public+private answers are refused whole -- that shape IS the rebinding/probe smell.
    internal static IPAddress[] FilterForConnect(string host, IPAddress[] resolved)
    {
        if (resolved.Length == 0)
        {
            throw new HttpRequestException($"'{host}' does not resolve.");
        }
        if (resolved.Any(IsBlockedAddress))
        {
            throw new HttpRequestException($"Refusing to connect to '{host}': it resolves to a private or internal address.");
        }
        return resolved;
    }

    /// A SocketsHttpHandler whose connections can only ever reach guard-approved addresses.
    public static SocketsHttpHandler CreatePinnedHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var literalHost = host.Trim().Trim('[', ']');
            var resolved = IPAddress.TryParse(literalHost, out var literal)
                ? new[] { literal }
                : await ResolveHook(host, ct);
            var allowed = FilterForConnect(host, resolved);

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };
}
