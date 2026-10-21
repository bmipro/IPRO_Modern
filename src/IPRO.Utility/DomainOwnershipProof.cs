using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IPRO.Entities;

namespace IPRO.Utility;

// 592 (2026-10-19, found by 591's review and confirmed by the owner on www.411trades.com). A domain name says
// nothing about who owns it. Until now an adviser could add any name nobody else had in iPro, and when that
// name's DNS still pointed at iPro -- a customer who left, a binding left behind in Azure -- the domain check
// connected it and the public site served the adviser's pages on it. Someone else's domain, someone else's
// visitors and leads.
//
// Now whoever controls the domain's DNS says which adviser it is for: a TXT record named _ipro-verify on the
// domain (its short address: _ipro-verify.yourfirm.ca for www.yourfirm.ca) holding a value made for that one
// row -- one adviser, one domain. Nothing is bound, secured or served for a row until the record has been seen
// (AgentDomain.OwnershipVerifiedAt). Another adviser's row for the same name has another value, and a record
// left in place after a domain is removed proves nothing for anyone else. Rows from before 592 count as proven
// at the release (StartupSchemaRepair).
public static class DomainOwnershipProof
{
    public const string RecordLabel = "_ipro-verify";
    public const int TxtRecordType = 16;
    private const string TokenPrefix = "ipro-";

    // Test seam (CaaCheck.LookupHook's shape): a DNS-over-HTTPS resolver's JSON answer for one name's TXT
    // records, or null when it did not answer.
    public static Func<string, CancellationToken, Task<string?>>? LookupHook { get; set; }

    public static bool IsNeeded(AgentDomain domain) => domain.OwnershipVerifiedAt == null;

    // A fresh value: 128 random bits, nothing anyone could work out from the adviser or the domain.
    public static string NewToken() => TokenPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    // The name the proof is published under covers every name of the row: the short address when the row is a
    // www name (www.x.ca and x.ca are both in the x.ca zone), else the name itself (clients.firm.ca). Worked out
    // from the name the row was added as, never read from the row's other fields.
    public static string ProvenName(AgentDomain domain) => ProvenNameOf(domain.DomainName);

    public static string ProvenNameOf(string? domainName)
    {
        var name = (domainName ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        return name.StartsWith("www.", StringComparison.Ordinal) ? name[4..] : name;
    }

    // The Name box's text for a proven name (NameToType's rule, for a name with no row yet).
    public static string NameToTypeFor(string provenName) =>
        provenName.Count(c => c == '.') == 1 ? RecordLabel : RecordLabel + "." + provenName;

    // Whether one of the row's names is covered by its proof: the proven name or its www name. iPro writes a row's
    // short address and www name from the name it was added as (WebsiteController.BuildDomainParts), so this only
    // fails for a row written some other way; such a row is never connected on this proof.
    public static bool Covers(AgentDomain domain, string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        var name = host.Trim().ToLowerInvariant();
        var proven = ProvenName(domain);
        return name == proven || name == "www." + proven;
    }

    public static string RecordName(AgentDomain domain) => RecordLabel + "." + ProvenName(domain);

    // What most registrars want in their Name box: the label alone when the proven name is a two-part domain
    // (yourfirm.ca), the full name otherwise (there is no telling where a longer name's zone starts).
    public static string NameToType(AgentDomain domain)
    {
        var proven = ProvenName(domain);
        return proven.Count(c => c == '.') == 1 ? RecordLabel : RecordName(domain);
    }

    // The sentence the adviser is shown while the record has not been seen. Kept as the row's LastError and
    // passed through unchanged by DomainErrorTranslator (it carries "_ipro-verify").
    public static string Instruction(AgentDomain domain) =>
        $"Before {ProvenName(domain)} can be connected, prove it is yours: add a TXT record named {NameToType(domain)} " +
        $"with the value {domain.VerificationToken} where the domain's DNS is managed, then click Check now. " +
        "Nothing is connected until we see it.";

    public static string InstructionWithoutValue(AgentDomain domain) =>
        $"Before {ProvenName(domain)} can be connected, prove it is yours: open My Website to see the TXT record ({RecordLabel}) to add " +
        "where the domain's DNS is managed. Nothing is connected until we see it.";

    // The TXT values in a resolver's answer for one name. A record split into several quoted strings is one
    // value (DNS keeps strings of up to 255 characters; resolvers show them as "a" "b").
    public static List<string> ParseAnswer(string? json)
    {
        var values = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return values;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array) return values;
            foreach (var answer in answers.EnumerateArray())
            {
                if (!answer.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.Number || type.GetInt32() != TxtRecordType) continue;
                if (!answer.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String) continue;
                values.Add(JoinStrings(data.GetString() ?? string.Empty));
            }
        }
        catch (JsonException) { }
        return values;
    }

    private static string JoinStrings(string data)
    {
        var text = data.Trim();
        if (!text.StartsWith('"')) return text;
        var joined = new StringBuilder();
        var inside = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && inside && i + 1 < text.Length) { joined.Append(text[++i]); continue; }
            if (c == '"') { inside = !inside; continue; }
            if (inside) joined.Append(c);
        }
        return joined.ToString();
    }

    // Exactly the row's value, nothing else: a value of another row, an empty token or a near miss is no proof.
    public static bool Matches(IEnumerable<string> values, string? token) =>
        !string.IsNullOrWhiteSpace(token) &&
        values.Any(value => string.Equals(value.Trim(), token.Trim(), StringComparison.OrdinalIgnoreCase));

    // Whether the record is published: true, false, or null when no resolver answered (the caller treats null as
    // not seen yet). Two public resolvers, so that one resolver's remembered "no such record" from before the
    // adviser added it does not hold them up.
    public static Task<bool?> IsPublishedAsync(AgentDomain domain, Func<string, CancellationToken, Task<string?>> lookup, CancellationToken cancellationToken = default) =>
        IsPublishedAsync(ProvenName(domain), domain.VerificationToken, lookup, cancellationToken);

    public static async Task<bool?> IsPublishedAsync(string provenName, string? token, Func<string, CancellationToken, Task<string?>> lookup, CancellationToken cancellationToken = default)
    {
        // The proven name as it is: making a row of it would take a leading "www." off a second time.
        var proven = (provenName ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(token) || proven.Length == 0) return false;
        var name = RecordLabel + "." + proven;
        bool? seen = null;
        foreach (var resolver in Resolvers)
        {
            string? json;
            try { json = await lookup(resolver(name), cancellationToken); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException) { continue; }
            if (json == null) continue;
            if (Matches(ParseAnswer(json), token)) return true;
            seen = false;
        }
        return seen;
    }

    // Each resolver's question for one name. The test seam receives the full address, so a test can tell the
    // two apart; DomainCheckService fetches it.
    private static readonly Func<string, string>[] Resolvers =
    {
        name => $"https://dns.google/resolve?name={Uri.EscapeDataString(name)}&type=TXT",
        name => $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(name)}&type=TXT",
    };
}
