using System.Text.Json;

namespace IPRO.Utility;

// 506. A domain can publish CAA records: a list of the only companies allowed to issue its security
// certificate. Many web hosts add them for their own free certificates (Sectigo, Let's Encrypt). The
// platform's certificates come from DigiCert, so when a customer's list leaves DigiCert out the
// certificate order sits "in progress" for ever and the customer sees a browser warning with no
// reason given (found on our own domains, 2026-09-20). This reads the list and says which name
// carries it, so the customer can be shown the one record to add.
public static class CaaCheck
{
    public const string Authority = "digicert.com";
    public const string RecordToAdd = "0 issue \"digicert.com\"";
    public const int CaaRecordType = 257;

    // Test seam: the JSON a DNS-over-HTTPS resolver returns for one name's CAA records (null = no answer).
    public static Func<string, CancellationToken, Task<string?>>? LookupHook { get; set; }

    // The CAA values in a resolver's answer for one name ("0 issue \"letsencrypt.org\"", ...).
    // A CNAME in the answer is not a CAA record and is skipped.
    public static List<string> ParseAnswer(string? json)
    {
        var records = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return records;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array) return records;
            foreach (var answer in answers.EnumerateArray())
            {
                if (!answer.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.Number || type.GetInt32() != CaaRecordType) continue;
                if (answer.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
                    records.Add(data.GetString() ?? string.Empty);
            }
        }
        catch (JsonException) { }
        return records;
    }

    // Whether a name's CAA records stop DigiCert. Only "issue" records restrict an ordinary
    // certificate ("issuewild" is for wildcards, "iodef" is a contact); with no "issue" record
    // any company may issue. `0 issue ";"` allows nobody.
    public static bool Blocks(IEnumerable<string> records)
    {
        var issuers = new List<string>();
        foreach (var record in records)
        {
            var parts = record.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !string.Equals(parts[1], "issue", StringComparison.OrdinalIgnoreCase)) continue;
            // The value is the issuer's domain, optionally followed by ";" and parameters.
            issuers.Add(parts[2].Trim().Trim('"').Split(';')[0].Trim().ToLowerInvariant());
        }
        return issuers.Count > 0 && !issuers.Contains(Authority);
    }

    // The names whose CAA records apply to a host, nearest first: the host itself, then each parent
    // down to the registered domain ("www.shop.example.com", "shop.example.com", "example.com").
    public static List<string> NamesToAsk(string host)
    {
        var names = new List<string>();
        var name = (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        while (name.Contains('.'))
        {
            names.Add(name);
            name = name[(name.IndexOf('.') + 1)..];
        }
        return names;
    }

    // The name whose CAA records stop the certificate for this host, or "" when nothing does. The
    // nearest name that has any CAA records decides. Null when a lookup failed, so the caller keeps
    // what it knew rather than clearing a real warning because DNS was slow for a moment.
    public static async Task<string?> FindBlockingNameAsync(string host, Func<string, CancellationToken, Task<string?>> lookup, CancellationToken cancellationToken = default)
    {
        foreach (var name in NamesToAsk(host))
        {
            string? json;
            try { json = await lookup(name, cancellationToken); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException) { return null; }
            if (json == null) return null;
            var records = ParseAnswer(json);
            if (records.Count == 0) continue;
            return Blocks(records) ? name : string.Empty;
        }
        return string.Empty;
    }
}
