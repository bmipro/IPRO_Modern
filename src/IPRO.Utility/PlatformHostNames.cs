using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace IPRO.Utility;

// 589 (2026-10-18): iPro's own host names. No adviser's custom domain may be one, and the domain
// automation never binds, re-binds or deletes one, whatever a row says. Found by 586's review: My Website
// refused only the free-site zone, so an adviser with extra domains could add app.iproadvisers.com, and
// removing it again sent Azure a DELETE for that name's binding and certificate on the production web app.
//
// A name is iPro's when it is, or sits under:
//   - one of iPro's registered domains (OwnDomains). This list holds whatever a setting says: the admin
//     app's settings name none of iPro's addresses, and it deletes bindings too (an adviser's erasure).
//     A domain iPro registers and serves goes on this list when it goes live (crm.to forwards to the
//     home page since 555, 2026-10-07).
//   - a name the settings give as iPro's, with or without www.: App:BaseUrl's and App:PortalBaseUrl's
//     hosts, App:PlatformDomains, App:AliasHosts, App:AdminDomain, the free sites' zone
//     (App:TemporarySiteRootDomain) and the DNS target advisers point at (App:WebsiteDnsTarget).
//   - the App Service's own names (*.azurewebsites.net), or a local machine (localhost).
// A name is compared as typed and as a resolver reads it (IDNA: full-width letters, capitals). Before a
// name is stored or sent to Azure it must also be a host name at all (TryCanonicalHost).
public static class PlatformHostNames
{
    public static readonly IReadOnlyList<string> OwnDomains = new[]
    {
        "iproadvisers.com", "iproaccountants.com", "ipromortgages.com", "247advisers.com", "crm.to",
    };

    private static readonly string[] MachineDomains = { "azurewebsites.net", "localhost" };

    public static bool IsPlatformHost(string? host, IConfiguration? configuration = null)
    {
        var domains = Domains(configuration).ToList();
        return Forms(host).Any(name => domains.Any(domain => name == domain || name.EndsWith("." + domain, StringComparison.Ordinal)));
    }

    // The certificate the domain automation named for a host before 591 was managed-<host with dots as dashes>,
    // so two names that differ only in where their dots and dashes are shared one: app-iproadvisers.com would
    // have ordered or deleted the certificate of app.iproadvisers.com. Such a name is still refused like iPro's
    // own. (Since 591 every certificate is read before it is ordered or deleted, and touched only when it was
    // ordered for that very host; ManagedCertificateNames.)
    public static bool SharesCertificateNameWithPlatformHost(string? host, IConfiguration? configuration = null)
    {
        var domains = Domains(configuration).Select(Undash).ToList();
        return Forms(host).Select(Undash).Any(name => domains.Any(domain => name == domain || name.EndsWith("." + domain, StringComparison.Ordinal)));
    }

    private static string Undash(string name) => name.Replace('-', '.');

    // 589's review: the one shape a name may have before it is stored or sent to Azure. What a resolver reads
    // (IDNA with the STD3 rules: letters, digits and hyphens only), two labels or more, each 1-63 characters
    // and neither starting nor ending with a hyphen, 253 characters in all. A name with anything else in it
    // (a backslash, a percent sign, a control or zero-width character) is no host name, and nothing here can
    // know how Azure would read it.
    //
    // And the name must already BE that form (the review's recheck): a name IDNA would change -- full-width
    // letters, an ideographic full stop, characters it drops, another script -- is refused, not converted.
    // Converted, it would be stored and compared with other advisers' names in one form and sent to Azure in
    // another, so a full-width twin of another adviser's domain would pass the claim check and its removal
    // would delete that adviser's binding. Which characters IDNA drops also differs between Windows and the
    // Linux servers; refusing every change makes that difference harmless. A domain in another script is
    // entered as its xn-- form. Nothing is trimmed here either (the second recheck): a stored "example.com."
    // or " example.com" is not the name the claim check compared, so it is refused, not cleaned into another
    // adviser's domain. Only capitals are let through (every comparison ignores them). `ascii` is the name
    // in small letters: the form stored, compared and sent.
    public static bool TryCanonicalHost(string? host, out string ascii)
    {
        ascii = string.Empty;
        var name = (host ?? string.Empty).ToLowerInvariant();
        if (name.Length == 0) return false;

        string mapped;
        try { mapped = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(name).ToLowerInvariant(); }
        catch (ArgumentException) { return false; }
        if (!string.Equals(mapped, name, StringComparison.Ordinal)) return false;

        var labels = mapped.Split('.');
        if (mapped.Length > 253 || labels.Length < 2) return false;
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-') return false;
            if (!label.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) return false;
        }

        ascii = mapped;
        return true;
    }

    // A name as it may go into a log line: anything outside printable ASCII written as <U+XXXX>, so a typed
    // line break cannot start a line of its own.
    public static string Printable(string? host) =>
        string.Concat((host ?? string.Empty).Select(c => c is >= ' ' and <= '~' ? c.ToString() : "<U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture) + ">"));

    private static IEnumerable<string> Forms(string? host)
    {
        var name = Clean(host);
        if (name.Length == 0) yield break;
        yield return name;
        string? ascii = null;
        try { ascii = Clean(new IdnMapping().GetAscii(name)); }
        catch (ArgumentException) { }   // no resolver reads it either
        if (!string.IsNullOrEmpty(ascii) && ascii != name) yield return ascii;
    }

    private static IEnumerable<string> Domains(IConfiguration? configuration)
    {
        foreach (var domain in OwnDomains.Concat(MachineDomains)) yield return domain;
        if (configuration == null) yield break;

        foreach (var url in new[] { configuration["App:BaseUrl"], configuration["App:PortalBaseUrl"] })
            if (Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Host.Contains('.')) yield return WithoutWww(uri.Host);

        var names = (configuration["App:PlatformDomains"] ?? string.Empty).Split(',')
            .Concat((configuration["App:AliasHosts"] ?? string.Empty).Split(',', ';').Select(entry => entry.Split('=')[0]))
            .Append(configuration["App:AdminDomain"])
            .Append(configuration["App:TemporarySiteRootDomain"])
            .Append(configuration["App:WebsiteDnsTarget"]);
        // A setting's name has a dot: a stray single word ("com") must not make every name iPro's.
        foreach (var name in names.Select(Clean).Where(n => n.Contains('.'))) yield return WithoutWww(name);
    }

    private static string Clean(string? host) => (host ?? string.Empty).Trim().Trim('.').ToLowerInvariant();

    // www.example.com and example.com are one name here; a name under either is under the short one.
    private static string WithoutWww(string host)
    {
        var name = Clean(host);
        return name.StartsWith("www.", StringComparison.Ordinal) && name.IndexOf('.', 4) > 4 ? name[4..] : name;
    }
}
