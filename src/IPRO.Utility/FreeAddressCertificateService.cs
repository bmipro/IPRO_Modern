using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IPRO.Utility;

public enum FreeAddressCertificateMode
{
    Off,
    Test,
    On
}

// 675 (2026-11-23): the *.247advisers.com certificate renews itself.
//
// Every adviser's free address (name.247advisers.com) is served by ipro-prod-web under one wildcard certificate. Until
// now it was bought (SSLs.com, Sectigo) and uploaded by hand (638/672), and the industry's limit on a certificate's life
// is shrinking: 200 days since 2026-03-15, 100 days from 2027-03-15, 47 days from 2029-03-15. Azure's own free
// certificates cannot cover a wildcard. So this asks Let's Encrypt for one, proving the domain by a DNS record:
//
//   - The record Let's Encrypt reads is _acme-challenge.247advisers.com. The domain's DNS is at GoDaddy, where it is a
//     CNAME (set once, by hand) to _acme-challenge.acme-dns.247advisers.com, and acme-dns.247advisers.com is a small
//     Azure DNS zone delegated from GoDaddy (four NS records, set once). This service writes the TXT record there with
//     the same Azure identity that binds advisers' domains (AzureDomainAutomation, Contributor on the resource group),
//     and removes it when the check is over. Nothing here can touch the domain's other records, or its mail.
//   - The new certificate is uploaded to the resource group under a name of its own (ipro-free-address-<UTC time>) and
//     bound to the *.247advisers.com host name, replacing whatever was bound before; Azure starts serving it within a
//     few minutes. A certificate this service uploaded is removed once it has expired; one it did not upload (the one
//     bought by hand) is never touched.
//   - It renews 30 days before expiry. The first run with the switch On replaces the bought certificate at once.
//
// FreeAddressCertificate__Mode, an app setting: Off (the default: nothing happens), Test (the whole path against Let's
// Encrypt's test service -- the record, the check, an upload and its removal -- switching nothing), On.
//
// Like AzureDomainAutomationService, it acts only when this process IS the configured web app (WEBSITE_SITE_NAME), so a
// copy of production's settings anywhere else cannot rebind production. Any failure throws: the job's row on the Job
// Scheduler goes red, and CertificateExpiryJob still warns 21 days before expiry (with the switch On) as the backstop.
public sealed class FreeAddressCertificateService
{
    public const string ModeSetting = "FreeAddressCertificate:Mode";
    public const string CertificatePrefix = "ipro-free-address-";
    public const string TestCertificateName = CertificatePrefix + "test";
    public const string ZoneLabel = "acme-dns";
    public const string ChallengeLabel = "_acme-challenge";
    public const int RenewWithinDays = 30;

    private const string DnsApiVersion = "2018-05-01";
    private static readonly TimeSpan RecordWait = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CheckWait = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ServeWait = TimeSpan.FromMinutes(5);

    // Test seams.
    internal static Func<TimeSpan, CancellationToken, Task> Delay = Task.Delay;
    internal static Func<string, CancellationToken, Task<IPAddress[]>> ResolveHost = (host, ct) => Dns.GetHostAddressesAsync(host, ct);
    internal static Func<string, CancellationToken, Task<string?>> ServedThumbprint = ReadServedThumbprintAsync;
    internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AzureDomainAutomationOptions _azure;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FreeAddressCertificateService> _logger;
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    public FreeAddressCertificateService(
        IHttpClientFactory httpClientFactory,
        IOptions<AzureDomainAutomationOptions> azure,
        IConfiguration configuration,
        ILogger<FreeAddressCertificateService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _azure = azure.Value;
        _configuration = configuration;
        _logger = logger;
    }

    // Routine: nothing to say beyond "still fine" (or Off); the job logs everything else as a Warning.
    public sealed record Outcome(bool Switched, string Summary, bool Routine = false);

    internal sealed record Issued(byte[] Pfx, string Password, string Thumbprint, DateTime NotAfter, string Issuer);

    internal sealed record HeldCertificate(string Name, string? Thumbprint, DateTime? Expires);

    public static FreeAddressCertificateMode ModeFrom(IConfiguration? configuration) =>
        (configuration?[ModeSetting] ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "on" => FreeAddressCertificateMode.On,
            "test" => FreeAddressCertificateMode.Test,
            _ => FreeAddressCertificateMode.Off
        };

    public static string RootDomain(IConfiguration? configuration)
    {
        var root = (configuration?["App:TemporarySiteRootDomain"] ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
        return root.Length == 0 ? "247advisers.com" : root;
    }

    public static string ZoneName(string root) => ZoneLabel + "." + root;

    // The name Let's Encrypt reads (a CNAME at the domain's own DNS host) and where it points (the TXT in Azure DNS).
    public static string ChallengeName(string root) => ChallengeLabel + "." + root;
    public static string ChallengeTarget(string root) => ChallengeLabel + "." + ZoneName(root);

    public static bool IsOurs(string certificateName) => certificateName.StartsWith(CertificatePrefix, StringComparison.Ordinal);

    public async Task<Outcome> RunAsync(CancellationToken cancellationToken)
    {
        var mode = ModeFrom(_configuration);
        if (mode == FreeAddressCertificateMode.Off)
            return new Outcome(false, "Off: the free-address certificate is renewed by hand (the app setting FreeAddressCertificate__Mode is not On).", Routine: true);

        var missing = MissingSettings();
        if (missing.Length > 0)
            throw new InvalidOperationException($"The free-address certificate was not renewed: the AzureDomainAutomation settings are incomplete ({missing}).");
        if (!RunningAsTheWebApp(out var reason))
            throw new InvalidOperationException("The free-address certificate was not renewed: " + reason + ".");

        var root = RootDomain(_configuration);
        if (!PlatformHostNames.TryCanonicalHost(root, out root) || !root.Contains('.'))
            throw new InvalidOperationException("The free-address certificate was not renewed: App:TemporarySiteRootDomain is not a domain name.");
        var wildcard = "*." + root;

        var held = await ListCertificatesAsync(cancellationToken);
        var boundThumbprint = await BoundThumbprintAsync(wildcard, cancellationToken);
        var bound = held.FirstOrDefault(c => string.Equals(c.Thumbprint, boundThumbprint, StringComparison.OrdinalIgnoreCase));
        var now = UtcNow();

        if (mode == FreeAddressCertificateMode.On && bound is { Expires: { } expires } && IsOurs(bound.Name) &&
            expires > now.AddDays(RenewWithinDays))
        {
            return new Outcome(false,
                $"{wildcard}: {bound.Name} is bound, good to {expires:yyyy-MM-dd} ({(int)(expires - now).TotalDays} days); it is renewed {RenewWithinDays} days before that.",
                Routine: true);
        }

        var test = mode == FreeAddressCertificateMode.Test;
        var issued = await IssueAsync(root, test ? AcmeClient.LetsEncryptStaging : AcmeClient.LetsEncrypt, cancellationToken);

        if (test)
        {
            await UploadAsync(TestCertificateName, issued, cancellationToken);
            await DeleteCertificateAsync(TestCertificateName, cancellationToken);
            return new Outcome(false,
                $"Test passed: Let's Encrypt's test service issued a certificate for {wildcard} (good to {issued.NotAfter:yyyy-MM-dd}); " +
                "the DNS record, the check, the upload and its removal all worked. Nothing was switched. Set FreeAddressCertificate__Mode to On.");
        }

        var name = CertificatePrefix + now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var thumbprint = await UploadAsync(name, issued, cancellationToken);
        await BindAsync(wildcard, thumbprint, cancellationToken);
        _logger.LogWarning("Free-address certificate: {Name} (Let's Encrypt, good to {Expires:yyyy-MM-dd}) is now bound to {Wildcard}; it replaced {Previous}",
            name, issued.NotAfter, wildcard, bound?.Name ?? boundThumbprint ?? "nothing");

        var served = await WaitUntilServedAsync("certificate-check." + root, thumbprint, cancellationToken);
        await RemoveExpiredAsync(held, thumbprint, now, cancellationToken);

        return new Outcome(true,
            $"{wildcard}: {name} from Let's Encrypt, good to {issued.NotAfter:yyyy-MM-dd}, is bound in place of {bound?.Name ?? "the previous certificate"}" +
            (served ? " and is being served." : "; Azure was not serving it yet after five minutes (it usually follows within the hour; the certificate check will say if not)."));
    }

    // ------------------------------------------------------------------------------------------------ the certificate --

    private async Task<Issued> IssueAsync(string root, string directory, CancellationToken cancellationToken)
    {
        var zone = ZoneName(root);
        var nameServers = await ZoneNameServersAsync(zone, cancellationToken);

        using var accountKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var acme = new AcmeClient(_httpClientFactory.CreateClient(), directory, accountKey);
        await acme.CreateAccountAsync(cancellationToken);

        var wildcard = "*." + root;
        var order = await acme.NewOrderAsync(new[] { wildcard }, cancellationToken);
        var pending = new List<(AcmeClient.Authorization Authorization, AcmeClient.Challenge Challenge, string Value)>();
        foreach (var url in order.Authorizations)
        {
            var authorization = await acme.GetAuthorizationAsync(url, cancellationToken);
            if (authorization.Status == "valid") continue;
            var challenge = authorization.Dns ?? throw new AcmeException($"Let's Encrypt offered no DNS check for {authorization.Identifier}.");
            pending.Add((authorization, challenge, acme.DnsTxtValue(challenge.Token)));
        }

        if (pending.Count > 0)
        {
            var values = pending.Select(p => p.Value).Distinct(StringComparer.Ordinal).ToList();
            try
            {
                await PutTxtAsync(zone, values, cancellationToken);
                await WaitUntilVisibleAsync(nameServers, ChallengeTarget(root), values, cancellationToken);
                foreach (var p in pending) await acme.RespondAsync(p.Challenge, cancellationToken);
                foreach (var p in pending)
                {
                    var result = await acme.WaitForAuthorizationAsync(p.Authorization.Url, CheckWait, cancellationToken);
                    if (result.Status != "valid")
                    {
                        throw new AcmeException(
                            $"Let's Encrypt could not confirm {result.Identifier} ({result.Status}): {result.Problem ?? "no reason given"}. " +
                            $"Check that {ChallengeName(root)} at the domain's DNS host is a CNAME to {ChallengeTarget(root)}, and that " +
                            $"{zone} is delegated to Azure DNS (DOCS/20_CERTIFICATES.md, \"The free-address certificate\").");
                    }
                }
            }
            finally
            {
                try
                {
                    await DeleteTxtAsync(zone, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Free-address certificate: the check record in {Zone} could not be removed; it is harmless and is replaced on the next run", zone);
                }
            }
        }

        order = await acme.GetOrderAsync(order.Url, cancellationToken);
        if (order.Status != "ready")
            throw new AcmeException($"Let's Encrypt's order for {wildcard} is '{order.Status}', not ready to finish: {order.Problem ?? "no reason given"}.");

        using var key = RSA.Create(2048);
        order = await acme.FinalizeAsync(order, BuildCsr(wildcard, key), cancellationToken);
        order = await acme.WaitForOrderAsync(order, CheckWait, cancellationToken);
        if (order.Status != "valid" || string.IsNullOrEmpty(order.Certificate))
            throw new AcmeException($"Let's Encrypt did not issue the certificate for {wildcard} ('{order.Status}'): {order.Problem ?? "no reason given"}.");

        var pem = await acme.DownloadCertificateAsync(order.Certificate, cancellationToken);
        return Package(pem, key, root);
    }

    // RSA 2048: App Service takes an uploaded certificate's key as RSA of at least 2048 bits.
    internal static byte[] BuildCsr(string wildcard, RSA key)
    {
        var request = new CertificateRequest(new X500DistinguishedName("CN=" + wildcard), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(wildcard);
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSigningRequest();
    }

    // The PFX App Service takes: the certificate with its key, then the chain, password-protected with triple DES. The
    // password is made here, sent to Azure with the upload, and kept nowhere.
    internal static Issued Package(string pem, RSA key, string root)
    {
        var chain = new X509Certificate2Collection();
        chain.ImportFromPem(pem);
        try
        {
            if (chain.Count == 0) throw new AcmeException("Let's Encrypt sent no certificate.");
            var leaf = chain[0];
            if (!leaf.MatchesHostname("certificate-check." + root))
                throw new AcmeException($"The certificate Let's Encrypt sent is not for *.{root} ({leaf.Subject}).");

            using var withKey = leaf.CopyWithPrivateKey(key);   // throws when the key is not this certificate's
            var export = new X509Certificate2Collection { withKey };
            for (var i = 1; i < chain.Count; i++) export.Add(chain[i]);

            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            var pfx = export.ExportPkcs12(Pkcs12ExportPbeParameters.Pkcs12TripleDesSha1, password);
            return new Issued(pfx, password, leaf.Thumbprint, leaf.NotAfter.ToUniversalTime(), leaf.Issuer);
        }
        finally
        {
            foreach (var certificate in chain) certificate.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------- Azure DNS --

    private async Task<IReadOnlyList<string>> ZoneNameServersAsync(string zone, CancellationToken cancellationToken)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, DnsUri(zone, null), null, cancellationToken);
        if (status == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"The Azure DNS zone {zone} does not exist in {_azure.ResourceGroup} (DOCS/20_CERTIFICATES.md, \"The free-address certificate\").");
        EnsureSuccess(status, body, "read the DNS zone " + zone);

        using var json = JsonDocument.Parse(body);
        var servers = new List<string>();
        if (json.RootElement.TryGetProperty("properties", out var properties) &&
            properties.TryGetProperty("nameServers", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var server in list.EnumerateArray())
                if (server.ValueKind == JsonValueKind.String) servers.Add(server.GetString()!.TrimEnd('.'));
        }
        if (servers.Count == 0) throw new InvalidOperationException($"Azure names no name servers for {zone}.");
        return servers;
    }

    private async Task PutTxtAsync(string zone, IReadOnlyList<string> values, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            properties = new
            {
                TTL = 60,
                TXTRecords = values.Select(v => new { value = new[] { v } }).ToArray()
            }
        });
        var (status, body) = await SendAsync(HttpMethod.Put, DnsUri(zone, ChallengeLabel), payload, cancellationToken);
        EnsureSuccess(status, body, $"write the check record in {zone}");
    }

    private async Task DeleteTxtAsync(string zone, CancellationToken cancellationToken)
    {
        var (status, body) = await SendAsync(HttpMethod.Delete, DnsUri(zone, ChallengeLabel), null, cancellationToken);
        if (status != HttpStatusCode.NotFound) EnsureSuccess(status, body, $"remove the check record in {zone}");
    }

    // Let's Encrypt asks one of the zone's name servers once; it must not ask before Azure has the record everywhere.
    // Asked of each name server directly (ZoneDns: no resolver remembering an older "no such record").
    private async Task WaitUntilVisibleAsync(IReadOnlyList<string> nameServers, string name, IReadOnlyList<string> values, CancellationToken cancellationToken)
    {
        var until = UtcNow() + RecordWait;
        var waiting = nameServers.ToList();
        while (true)
        {
            foreach (var server in waiting.ToList())
            {
                var addresses = await ResolveHost(server, cancellationToken);
                var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
                if (address == null) continue;
                var answer = await ZoneDns.QueryAsync(address, name, ZoneDns.TXT, cancellationToken);
                var seen = answer?.Values(ZoneDns.TXT, name).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
                if (values.All(seen.Contains)) waiting.Remove(server);
            }
            if (waiting.Count == 0) return;
            if (UtcNow() >= until)
                throw new InvalidOperationException($"The check record {name} did not show on Azure's name servers ({string.Join(", ", waiting)}) within two minutes.");
            await Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private string DnsUri(string zone, string? txtName) => ManagementUri(
        $"subscriptions/{_azure.SubscriptionId}/resourceGroups/{_azure.ResourceGroup}/providers/Microsoft.Network/dnsZones/{zone}" +
        (txtName == null ? string.Empty : "/TXT/" + txtName), DnsApiVersion);

    // ------------------------------------------------------------------------------------------- App Service (web) --

    internal async Task<IReadOnlyList<HeldCertificate>> ListCertificatesAsync(CancellationToken cancellationToken)
    {
        var certificates = new List<HeldCertificate>();
        string? next = ManagementUri($"subscriptions/{_azure.SubscriptionId}/resourceGroups/{_azure.ResourceGroup}/providers/Microsoft.Web/certificates", _azure.ApiVersion);
        for (var page = 0; next != null && page < 50; page++)
        {
            if (!next.StartsWith("https://management.azure.com/", StringComparison.Ordinal))
                throw new InvalidOperationException("Azure's certificate list pointed outside management.azure.com.");
            var (status, body) = await SendAsync(HttpMethod.Get, next, null, cancellationToken);
            EnsureSuccess(status, body, "list the certificates");
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("value", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    if (string.IsNullOrEmpty(name)) continue;
                    string? thumbprint = null;
                    DateTime? expires = null;
                    if (item.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object)
                    {
                        if (p.TryGetProperty("thumbprint", out var t) && t.ValueKind == JsonValueKind.String) thumbprint = t.GetString();
                        if (p.TryGetProperty("expirationDate", out var e) && e.ValueKind == JsonValueKind.String &&
                            DateTimeOffset.TryParse(e.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                            expires = at.UtcDateTime;
                    }
                    certificates.Add(new HeldCertificate(name, thumbprint, expires));
                }
            }
            next = json.RootElement.TryGetProperty("nextLink", out var link) && link.ValueKind == JsonValueKind.String ? link.GetString() : null;
        }
        return certificates;
    }

    private string BindingUri(string wildcard) => ManagementUri(
        $"subscriptions/{_azure.SubscriptionId}/resourceGroups/{_azure.ResourceGroup}/providers/Microsoft.Web/sites/{_azure.WebAppName}/hostNameBindings/{wildcard}",
        _azure.ApiVersion);

    private async Task<JsonElement> ReadBindingAsync(string wildcard, CancellationToken cancellationToken)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, BindingUri(wildcard), null, cancellationToken);
        if (status == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"{_azure.WebAppName} has no {wildcard} host name, so there is nothing to put the certificate on.");
        EnsureSuccess(status, body, "read the " + wildcard + " host name");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("properties", out var properties) ? properties.Clone() : default;
    }

    private async Task<string?> BoundThumbprintAsync(string wildcard, CancellationToken cancellationToken)
    {
        var properties = await ReadBindingAsync(wildcard, cancellationToken);
        return properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("thumbprint", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
    }

    // The binding as Azure holds it, with only the certificate changed (a PUT replaces the whole binding).
    private async Task BindAsync(string wildcard, string thumbprint, CancellationToken cancellationToken)
    {
        var current = await ReadBindingAsync(wildcard, cancellationToken);
        var properties = new Dictionary<string, object?>
        {
            ["siteName"] = _azure.WebAppName,
            ["hostNameType"] = "Verified"
        };
        if (current.ValueKind == JsonValueKind.Object)
        {
            foreach (var keep in new[] { "siteName", "hostNameType", "customHostNameDnsRecordType" })
                if (current.TryGetProperty(keep, out var value) && value.ValueKind == JsonValueKind.String)
                    properties[keep] = value.GetString();
        }
        properties["sslState"] = "SniEnabled";
        properties["thumbprint"] = thumbprint;

        var (status, body) = await SendAsync(HttpMethod.Put, BindingUri(wildcard), JsonSerializer.Serialize(new { properties }), cancellationToken);
        EnsureSuccess(status, body, "bind the new certificate to " + wildcard);
    }

    private string CertificateUri(string name) => ManagementUri(
        $"subscriptions/{_azure.SubscriptionId}/resourceGroups/{_azure.ResourceGroup}/providers/Microsoft.Web/certificates/{Uri.EscapeDataString(name)}",
        _azure.ApiVersion);

    private async Task<string> UploadAsync(string name, Issued issued, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            location = _azure.Location,
            properties = new
            {
                pfxBlob = Convert.ToBase64String(issued.Pfx),
                password = issued.Password,
                serverFarmId = _azure.AppServicePlanResourceId
            }
        });
        var (status, body) = await SendAsync(HttpMethod.Put, CertificateUri(name), payload, cancellationToken);
        EnsureSuccess(status, body, "upload the certificate " + name);

        using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var thumbprint = json.RootElement.TryGetProperty("properties", out var p) && p.TryGetProperty("thumbprint", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
        if (!string.Equals(thumbprint, issued.Thumbprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Azure holds {name} with the thumbprint {thumbprint ?? "(none)"}, not the issued certificate's {issued.Thumbprint}; nothing was bound.");
        return issued.Thumbprint;
    }

    private async Task DeleteCertificateAsync(string name, CancellationToken cancellationToken)
    {
        if (!IsOurs(name)) throw new InvalidOperationException($"{name} was not uploaded by the free-address certificate job, so it is not removed.");
        var (status, body) = await SendAsync(HttpMethod.Delete, CertificateUri(name), null, cancellationToken);
        if (status != HttpStatusCode.NotFound) EnsureSuccess(status, body, "remove the certificate " + name);
    }

    // Only this job's own uploads, only once expired, never the one just bound. The bought certificate stays: the
    // owner removes it in the portal after it expires, if he wants to.
    private async Task RemoveExpiredAsync(IReadOnlyList<HeldCertificate> held, string boundThumbprint, DateTime now, CancellationToken cancellationToken)
    {
        foreach (var certificate in held)
        {
            if (!IsOurs(certificate.Name) || certificate.Expires is not { } expires || expires > now) continue;
            if (string.Equals(certificate.Thumbprint, boundThumbprint, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                await DeleteCertificateAsync(certificate.Name, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Free-address certificate: the expired {Name} could not be removed; the next renewal tries again", certificate.Name);
            }
        }
    }

    private async Task<bool> WaitUntilServedAsync(string host, string thumbprint, CancellationToken cancellationToken)
    {
        var until = UtcNow() + ServeWait;
        while (true)
        {
            string? served = null;
            try
            {
                served = await ServedThumbprint(host, cancellationToken);
            }
            catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Not answering this minute: asked again below.
            }
            if (string.Equals(served, thumbprint, StringComparison.OrdinalIgnoreCase)) return true;
            if (UtcNow() >= until)
            {
                _logger.LogWarning("Free-address certificate: {Host} still served {Served} five minutes after {Thumbprint} was bound", host, served ?? "nothing readable", thumbprint);
                return false;
            }
            await Delay(TimeSpan.FromSeconds(15), cancellationToken);
        }
    }

    private static async Task<string?> ReadServedThumbprintAsync(string host, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(host, 443, timeout.Token);
        // Inspected, not trusted: the certificate is read whatever it is.
        await using var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token);
        return ssl.RemoteCertificate is X509Certificate2 certificate ? certificate.Thumbprint
            : ssl.RemoteCertificate == null ? null : X509CertificateLoader.LoadCertificate(ssl.RemoteCertificate.GetRawCertData()).Thumbprint;
    }

    // ------------------------------------------------------------------------------------------------ plumbing --

    private string MissingSettings()
    {
        var missing = new List<string>();
        var binding = _azure.MissingBindingSettingsSummary();
        if (binding.Length > 0) missing.Add(binding);
        if (string.IsNullOrWhiteSpace(_azure.AppServicePlanResourceId)) missing.Add("AppServicePlanResourceId");
        if (string.IsNullOrWhiteSpace(_azure.Location)) missing.Add("Location");
        return string.Join(", ", missing);
    }

    // Stricter than AzureDomainAutomationService: only the web app itself, which serves the free addresses and runs the
    // schedule, rebinds its own certificate.
    internal bool RunningAsTheWebApp(out string reason)
    {
        var siteName = AzureDomainAutomationService.SiteNameProvider();
        if (string.IsNullOrWhiteSpace(siteName))
        {
            reason = $"this process is not running in Azure App Service (WEBSITE_SITE_NAME is unset), so it may not change '{_azure.WebAppName}'";
            return false;
        }
        if (!string.Equals(siteName.Trim(), _azure.WebAppName, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"this process is running as '{siteName}', not '{_azure.WebAppName}'";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string uri, string? json, CancellationToken cancellationToken)
    {
        var token = await AccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (response.StatusCode, body);
    }

    private static void EnsureSuccess(HttpStatusCode status, string body, string what)
    {
        if ((int)status is >= 200 and < 300) return;
        throw new InvalidOperationException($"Azure refused to {what}: {(int)status} {AzureError(body)}");
    }

    private async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) && _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5)) return _accessToken;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://login.microsoftonline.com/{Uri.EscapeDataString(_azure.TenantId)}/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _azure.ClientId,
                ["client_secret"] = _azure.ClientSecret,
                ["grant_type"] = "client_credentials",
                ["scope"] = "https://management.azure.com/.default"
            })
        };
        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Azure sign-in for the free-address certificate failed: " + AzureError(body));

        using var json = JsonDocument.Parse(body);
        _accessToken = json.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(_accessToken)) throw new InvalidOperationException("Azure's sign-in answer held no access token.");
        _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(json.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600);
        return _accessToken;
    }

    private static string ManagementUri(string path, string apiVersion) =>
        $"https://management.azure.com/{path.TrimStart('/')}?api-version={Uri.EscapeDataString(apiVersion)}";

    private static string AzureError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "no detail given";
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString()!;
                if (error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) return c.GetString()!;
            }
            if (json.RootElement.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String) return d.GetString()!;
        }
        catch (JsonException)
        {
            // Plain text.
        }
        return body.Length > 300 ? body[..300] : body;
    }
}
