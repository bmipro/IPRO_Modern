using System.Security.Cryptography;
using System.Text;

namespace IPRO.Utility;

// 591 (2026-10-18), found by 589's independent review: the names of the App Service managed certificates the
// domain automation orders for advisers' domains. (iPro's own certificates were ordered by hand and are named
// after their host, ops/domain-switch/cert-order.sh.)
//
// Until 591 a certificate was named managed-<host with dots as dashes>, cut at 80 characters (Legacy). That
// is not one name per host: www.abc-def.com and www-abc.def.com share one, and so do two long names that agree
// on their first 72 dashed characters. An adviser could add a name sharing another adviser's certificate name
// and press Remove, and the certificate DELETE named the other adviser's certificate.
//
// What closes that is AzureDomainAutomationService reading a certificate before it orders or deletes one, and
// touching it only when it was ordered for this very host (canonicalName). The names below take away the rest:
// with the shared name, whichever host ordered first held it, and a name typed ahead of its owner could keep
// that owner from ever getting a certificate. A certificate ordered since 591 is named For(host); one ordered
// before keeps its Legacy name, found by the same read.
public static class ManagedCertificateNames
{
    public const int MaxLength = 80;

    // "managed--": no Legacy name starts so (a host's first label never starts with a hyphen), so a For name is
    // never another host's Legacy name -- the review's case of a name built to match one under the old scheme.
    private const string Prefix = "managed--";

    // Characters of the dashed host kept in front of the hash: "managed--" (9) + 54 + "-" (1) + 16 = 80.
    private const int ReadablePart = MaxLength - 9 - 1 - 16;

    public static string Legacy(string host)
    {
        var name = "managed-" + host.Replace(".", "-", StringComparison.OrdinalIgnoreCase);
        return name.Length > MaxLength ? name[..MaxLength] : name;
    }

    // "managed--", the readable start of the dashed host, then 16 hex digits of the SHA-256 of the host in small
    // letters (the form stored and sent, PlatformHostNames.TryCanonicalHost). 64 bits: finding a second host with
    // the same name is out of reach, and the prefix keeps it apart from every Legacy name. The read before an
    // order still checks whose certificate a name holds; that check is what protects, the name only keeps it
    // from ever having to refuse.
    public static string For(string host)
    {
        var canonical = host.ToLowerInvariant();
        var dashed = canonical.Replace('.', '-');
        if (dashed.Length > ReadablePart) dashed = dashed[..ReadablePart];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16].ToLowerInvariant();
        return Prefix + dashed.TrimEnd('-') + "-" + hash;
    }
}
