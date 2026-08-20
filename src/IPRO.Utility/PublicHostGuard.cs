using System.Net;
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
}
