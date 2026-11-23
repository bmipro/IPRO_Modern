using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IPRO.Utility;

// 675 (2026-11-23): the few parts of ACME (RFC 8555) that iPro needs to get ONE certificate from Let's Encrypt by a DNS
// record: an account, an order, its authorization's dns-01 challenge, the finalize call and the download. Nothing more
// (no account recovery, no revocation, no key change): FreeAddressCertificateService is its only caller.
//
// Why our own and not a library: the whole protocol here is a handful of signed JSON posts, .NET signs them itself
// (ES256 over the account key, RFC 7515/7518), and a library would bring its own crypto stack into the process that
// holds the certificate's private key.
//
// Every request is a POST of a JWS: the first one (newAccount) carries the account's public key (jwk), the rest name
// the account by its URL (kid). Each needs a fresh nonce from the server; a badNonce answer is retried with the nonce
// that came with it (RFC 8555 section 6.5), twice at most. Any other error is an AcmeException with the server's own
// words (the problem document's detail), which ends up on the job's red row.
public sealed class AcmeClient
{
    public const string LetsEncrypt = "https://acme-v02.api.letsencrypt.org/directory";
    public const string LetsEncryptStaging = "https://acme-staging-v02.api.letsencrypt.org/directory";

    private readonly HttpClient _http;
    private readonly string _directoryUrl;
    private readonly ECDsa _accountKey;
    private AcmeDirectory? _directory;
    private string? _nonce;
    private string? _accountUrl;

    // Test seam: the pause between two looks at an order or an authorization.
    internal static Func<TimeSpan, CancellationToken, Task> Delay = Task.Delay;

    public AcmeClient(HttpClient http, string directoryUrl, ECDsa accountKey)
    {
        _http = http;
        _directoryUrl = directoryUrl;
        _accountKey = accountKey;
    }

    public sealed record AcmeDirectory(string NewNonce, string NewAccount, string NewOrder);

    public sealed record Order(string Url, string Status, IReadOnlyList<string> Authorizations, string Finalize, string? Certificate, string? Problem);

    public sealed record Challenge(string Type, string Url, string Token, string Status);

    public sealed record Authorization(string Url, string Status, string Identifier, bool Wildcard, IReadOnlyList<Challenge> Challenges, string? Problem)
    {
        public Challenge? Dns => Challenges.FirstOrDefault(c => c.Type == "dns-01");
    }

    // A new account each run (no contact address): nothing about it is kept, so there is no account key to store or
    // lose. termsOfServiceAgreed: turning the job on is the owner agreeing to Let's Encrypt's Subscriber Agreement
    // (DOCS/20_CERTIFICATES.md says so where it says how to turn it on).
    public async Task CreateAccountAsync(CancellationToken cancellationToken)
    {
        var directory = await DirectoryAsync(cancellationToken);
        var (response, _) = await PostAsync(directory.NewAccount, new { termsOfServiceAgreed = true }, useJwk: true, accept: null, cancellationToken);
        using (response)
        {
            _accountUrl = response.Headers.Location?.ToString();
        }
        if (string.IsNullOrEmpty(_accountUrl))
            throw new AcmeException("Let's Encrypt created the account but did not say where it is (no Location header).");
    }

    public async Task<Order> NewOrderAsync(IReadOnlyList<string> dnsNames, CancellationToken cancellationToken)
    {
        var directory = await DirectoryAsync(cancellationToken);
        var payload = new { identifiers = dnsNames.Select(n => new { type = "dns", value = n }).ToArray() };
        var (response, body) = await PostAsync(directory.NewOrder, payload, useJwk: false, accept: null, cancellationToken);
        using (response)
        {
            var url = response.Headers.Location?.ToString();
            if (string.IsNullOrEmpty(url)) throw new AcmeException("Let's Encrypt created the order but did not say where it is (no Location header).");
            return ParseOrder(url, body);
        }
    }

    public async Task<Order> GetOrderAsync(string url, CancellationToken cancellationToken)
    {
        var (response, body) = await PostAsync(url, null, useJwk: false, accept: null, cancellationToken);
        response.Dispose();
        return ParseOrder(url, body);
    }

    public async Task<Authorization> GetAuthorizationAsync(string url, CancellationToken cancellationToken)
    {
        var (response, body) = await PostAsync(url, null, useJwk: false, accept: null, cancellationToken);
        response.Dispose();
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var challenges = new List<Challenge>();
        if (root.TryGetProperty("challenges", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in list.EnumerateArray())
            {
                challenges.Add(new Challenge(Text(c, "type") ?? "", Text(c, "url") ?? "", Text(c, "token") ?? "", Text(c, "status") ?? ""));
            }
        }
        var identifier = root.TryGetProperty("identifier", out var id) ? Text(id, "value") ?? "" : "";
        var wildcard = root.TryGetProperty("wildcard", out var w) && w.ValueKind == JsonValueKind.True;
        return new Authorization(url, Text(root, "status") ?? "", identifier, wildcard, challenges, ProblemOf(root));
    }

    // The TXT value that answers a dns-01 challenge: base64url(SHA-256(token "." thumbprint of the account key)),
    // RFC 8555 section 8.1 and 8.4.
    public string DnsTxtValue(string token) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(token + "." + JwkThumbprint())));

    // "Ready to be checked": an empty JSON object, RFC 8555 section 7.5.1.
    public async Task RespondAsync(Challenge challenge, CancellationToken cancellationToken)
    {
        var (response, _) = await PostAsync(challenge.Url, new { }, useJwk: false, accept: null, cancellationToken);
        response.Dispose();
    }

    // Polls the authorization until Let's Encrypt has decided (valid or invalid), or the time is up.
    public async Task<Authorization> WaitForAuthorizationAsync(string url, TimeSpan limit, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + limit;
        while (true)
        {
            var authorization = await GetAuthorizationAsync(url, cancellationToken);
            if (authorization.Status is not ("pending" or "processing") || DateTime.UtcNow >= until) return authorization;
            await Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
    }

    public async Task<Order> FinalizeAsync(Order order, byte[] csrDer, CancellationToken cancellationToken)
    {
        var (response, body) = await PostAsync(order.Finalize, new { csr = Base64Url(csrDer) }, useJwk: false, accept: null, cancellationToken);
        response.Dispose();
        return ParseOrder(order.Url, body);
    }

    // After FinalizeAsync only: polls until the certificate is issued (valid) or refused (invalid), or the time is up.
    public async Task<Order> WaitForOrderAsync(Order order, TimeSpan limit, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + limit;
        while (order.Status is ("processing" or "ready") && DateTime.UtcNow < until)
        {
            await Delay(TimeSpan.FromSeconds(2), cancellationToken);
            order = await GetOrderAsync(order.Url, cancellationToken);
        }
        return order;
    }

    // The certificate and its chain, end-entity first (RFC 8555 section 7.4.2), as PEM text.
    public async Task<string> DownloadCertificateAsync(string url, CancellationToken cancellationToken)
    {
        var (response, body) = await PostAsync(url, null, useJwk: false, accept: "application/pem-certificate-chain", cancellationToken);
        response.Dispose();
        return body;
    }

    internal async Task<AcmeDirectory> DirectoryAsync(CancellationToken cancellationToken)
    {
        if (_directory != null) return _directory;
        using var response = await _http.GetAsync(_directoryUrl, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new AcmeException($"Let's Encrypt's directory answered {(int)response.StatusCode}: {Detail(body)}");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        _directory = new AcmeDirectory(
            Text(root, "newNonce") ?? throw new AcmeException("Let's Encrypt's directory names no newNonce."),
            Text(root, "newAccount") ?? throw new AcmeException("Let's Encrypt's directory names no newAccount."),
            Text(root, "newOrder") ?? throw new AcmeException("Let's Encrypt's directory names no newOrder."));
        return _directory;
    }

    private async Task<string> NewNonceAsync(CancellationToken cancellationToken)
    {
        var directory = await DirectoryAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Head, directory.NewNonce);
        using var response = await _http.SendAsync(request, cancellationToken);
        return NonceFrom(response) ?? throw new AcmeException("Let's Encrypt gave no nonce (no Replay-Nonce header).");
    }

    // One signed POST; payload null is a POST-as-GET (an empty payload, RFC 8555 section 6.3).
    private async Task<(HttpResponseMessage Response, string Body)> PostAsync(string url, object? payload, bool useJwk, string? accept, CancellationToken cancellationToken)
    {
        if (!useJwk && _accountUrl == null) throw new InvalidOperationException("CreateAccountAsync comes first.");
        for (var attempt = 0; ; attempt++)
        {
            var nonce = _nonce ?? await NewNonceAsync(cancellationToken);
            _nonce = null;

            var header = new Dictionary<string, object> { ["alg"] = "ES256", ["nonce"] = nonce, ["url"] = url };
            if (useJwk) header["jwk"] = Jwk();
            else header["kid"] = _accountUrl!;

            var protectedPart = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header));
            var payloadPart = payload == null ? string.Empty : Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
            // ECDsa.SignData gives r||s (IEEE P1363), which is what JWS's ES256 is (RFC 7518 section 3.4).
            var signature = _accountKey.SignData(Encoding.ASCII.GetBytes(protectedPart + "." + payloadPart), HashAlgorithmName.SHA256);
            var jws = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["protected"] = protectedPart,
                ["payload"] = payloadPart,
                ["signature"] = Base64Url(signature)
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(jws, Encoding.UTF8) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/jose+json");
            if (accept != null) request.Headers.Accept.ParseAdd(accept);

            var response = await _http.SendAsync(request, cancellationToken);
            _nonce = NonceFrom(response) ?? _nonce;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode) return (response, body);

            var status = (int)response.StatusCode;
            response.Dispose();
            if (ProblemType(body) == "urn:ietf:params:acme:error:badNonce" && attempt < 2) continue;
            throw new AcmeException($"Let's Encrypt answered {status}: {Detail(body)}");
        }
    }

    private static string? NonceFrom(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Replay-Nonce", out var values) ? values.FirstOrDefault() : null;

    private Order ParseOrder(string url, string body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var authorizations = new List<string>();
        if (root.TryGetProperty("authorizations", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
                if (a.ValueKind == JsonValueKind.String) authorizations.Add(a.GetString()!);
        }
        return new Order(url, Text(root, "status") ?? "", authorizations,
            Text(root, "finalize") ?? throw new AcmeException("Let's Encrypt's order names no finalize address."),
            Text(root, "certificate"), ProblemOf(root));
    }

    // The account's public key as a JWK (RFC 7517/7518): P-256, x and y base64url.
    private Dictionary<string, string> Jwk()
    {
        var p = _accountKey.ExportParameters(false);
        return new Dictionary<string, string>
        {
            ["crv"] = "P-256",
            ["kty"] = "EC",
            ["x"] = Base64Url(p.Q.X!),
            ["y"] = Base64Url(p.Q.Y!)
        };
    }

    // RFC 7638: the required members in lexicographic order, no white space, hashed with SHA-256.
    internal string JwkThumbprint()
    {
        var jwk = Jwk();
        var canonical = $"{{\"crv\":\"{jwk["crv"]}\",\"kty\":\"{jwk["kty"]}\",\"x\":\"{jwk["x"]}\",\"y\":\"{jwk["y"]}\"}}";
        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(canonical)));
    }

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // An order's or authorization's error, and a challenge's, in the server's words.
    private static string? ProblemOf(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            return Text(error, "detail") ?? Text(error, "type");
        if (root.TryGetProperty("challenges", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in list.EnumerateArray())
                if (c.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
                    return Text(e, "detail") ?? Text(e, "type");
        }
        return null;
    }

    private static string? ProblemType(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return Text(json.RootElement, "type");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Detail(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var detail = Text(json.RootElement, "detail");
            if (!string.IsNullOrEmpty(detail)) return detail;
        }
        catch (JsonException)
        {
            // Not a problem document: the text itself, cut short.
        }
        return string.IsNullOrWhiteSpace(body) ? "no detail given" : body.Length > 300 ? body[..300] : body;
    }
}

public sealed class AcmeException : Exception
{
    public AcmeException(string message) : base(message) { }
}
