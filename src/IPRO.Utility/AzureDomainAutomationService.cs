using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IPRO.Utility;

public class AzureDomainAutomationService : IAzureDomainAutomationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AzureDomainAutomationOptions _options;
    private readonly ILogger<AzureDomainAutomationService> _logger;
    private readonly IConfiguration? _configuration;
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    public AzureDomainAutomationService(
        IHttpClientFactory httpClientFactory,
        IOptions<AzureDomainAutomationOptions> options,
        ILogger<AzureDomainAutomationService> logger,
        IConfiguration? configuration = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _configuration = configuration;
    }

    public bool IsConfigured => _options.HasRequiredBindingSettings;

    // Config-hazard fix (2026-08-29, from the staging review round): both apps SHIP
    // "WebAppName": "ipro-prod-web" in committed appsettings, and this service issues HTTP DELETE
    // against that app's hostname bindings and certificates. Any second boot of this codebase
    // with a copied config -- a staging box, a local run with prod settings pasted in, the
    // snapshot-rehearsal server -- would therefore point a loaded gun at PRODUCTION's customer
    // domains. Azure App Service stamps every instance with WEBSITE_SITE_NAME; requiring it to
    // MATCH the configured target means this service can only ever mutate the app it is actually
    // running as. Production matches itself and is unaffected; everything else goes inert with a
    // loud log line instead of a cross-environment delete.
    //
    // Test seam, same pattern as PublicHostGuard.ResolveHook / SendGridEmailService.ClientFactory.
    internal static Func<string?> SiteNameProvider = () => Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");

    internal bool RunningAsConfiguredApp(out string reason)
    {
        var siteName = SiteNameProvider();
        if (string.IsNullOrWhiteSpace(siteName))
        {
            reason = $"this process is not running in Azure App Service (WEBSITE_SITE_NAME is unset), so it may not manage '{_options.WebAppName}'";
            return false;
        }
        var allowed = _options.EffectiveManagerSiteNames;
        if (!allowed.Any(a => string.Equals(siteName.Trim(), a, StringComparison.OrdinalIgnoreCase)))
        {
            reason = $"this process is running as '{siteName}' but only [{string.Join(", ", allowed)}] may manage '{_options.WebAppName}' -- refusing to touch another app's domains";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    // 589: iPro's own names (PlatformHostNames) are never bound, re-bound or deleted here, whatever row or
    // caller asks: an adviser's My Website removal, the admin app's erasure of an adviser, the domain check.
    // A re-bind is no safer than a delete: its first PUT names no certificate, which can take HTTPS off a
    // live name, and its certificate order replaces whatever certificate already has that name. Checked
    // before every switch below, so a refusal is logged with automation off too.
    //
    // 589's review: and only a host name goes to Azure, exactly as it is stored (PlatformHostNames.TryCanonicalHost).
    // A stored name with anything else in it -- a backslash, a percent sign, a zero-width character, full-width
    // letters a resolver would read as another adviser's domain -- is refused, never converted: nothing here can
    // know how Azure would read it, and a converted name is no longer the one the claim check compared.
    internal AzureDomainAutomationResult? Refusal(string? hostName, string action, out string canonical)
    {
        if (!PlatformHostNames.TryCanonicalHost(hostName, out canonical))
        {
            _logger.LogWarning("Azure domain automation refused to {Action} {HostName}: it is not a valid host name", action, PlatformHostNames.Printable(hostName));
            return AzureDomainAutomationResult.Skipped("That is not a valid domain name, so it cannot be connected as a website address.");
        }

        if (PlatformHostNames.IsPlatformHost(canonical, _configuration) ||
            PlatformHostNames.SharesCertificateNameWithPlatformHost(canonical, _configuration))
        {
            _logger.LogWarning("Azure domain automation refused to {Action} {HostName}: it is one of iPro's own addresses, or shares a certificate name with one", action, canonical);
            return AzureDomainAutomationResult.Skipped($"{canonical} is one of IPRO's own addresses, or too close to one, so it cannot be connected as a website address.");
        }

        return null;
    }

    public Task<AzureDomainAutomationResult> EnsureDomainAsync(string hostName, CancellationToken cancellationToken = default) =>
        EnsureHostAsync(hostName, CNameRecord, cancellationToken);

    // 553: a short address (example.com) cannot carry a CNAME, so it reaches the app by an A record
    // and Azure proves it by the asuid TXT record. Everything after the binding -- the managed
    // certificate issuing a few minutes later, the second pass that attaches it -- is the same as
    // for a www name. (A bare name's certificate is validated by an HTTP token through the A
    // record; the platform's own bare names took two minutes each on 2026-09-20.)
    public Task<AzureDomainAutomationResult> EnsureRootDomainAsync(string hostName, CancellationToken cancellationToken = default) =>
        EnsureHostAsync(hostName, AddressRecord, cancellationToken);

    internal const string CNameRecord = "CName";
    internal const string AddressRecord = "A";

    private async Task<AzureDomainAutomationResult> EnsureHostAsync(string hostName, string dnsRecordType, CancellationToken cancellationToken)
    {
        var stored = hostName;   // 589: the guard reads the name exactly as stored, untrimmed
        hostName = (hostName ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(hostName))
        {
            return AzureDomainAutomationResult.Failed("Domain name is missing.");
        }

        if (Refusal(stored, "bind", out var canonical) is { } refused)
        {
            return refused;
        }
        hostName = canonical;

        if (!_options.Enabled)
        {
            return AzureDomainAutomationResult.Skipped("Azure domain automation is disabled. Set AzureDomainAutomation__Enabled=true.");
        }

        if (!_options.HasRequiredBindingSettings)
        {
            return AzureDomainAutomationResult.Skipped($"Azure domain automation settings are incomplete. Missing: {_options.MissingBindingSettingsSummary()}.");
        }

        if (!RunningAsConfiguredApp(out var identityReason))
        {
            _logger.LogError("Azure domain automation refused: {Reason}", identityReason);
            return AzureDomainAutomationResult.Skipped($"Azure domain automation refused: {identityReason}.");
        }

        // 553: whether the binding itself went through, so a failure AFTER it (the certificate
        // order) can say so. Azure refuses that order while its own DNS check still sees the name's
        // previous record ("Missing one DNS record", seen for an hour on 2026-09-20): the name is
        // bound, only its certificate is waiting, and the caller should simply come back.
        var bindingCreated = false;
        try
        {
            await PutHostNameBindingAsync(hostName, null, dnsRecordType, cancellationToken);
            bindingCreated = true;

            if (!_options.HasRequiredCertificateSettings)
            {
                return new AzureDomainAutomationResult
                {
                    Success = true,
                    BindingCreated = true,
                    Message = "Azure custom-domain binding was created. SSL automation is disabled or missing App Service plan settings."
                };
            }

            var thumbprint = await EnsureManagedCertificateAsync(hostName, cancellationToken);
            if (string.IsNullOrWhiteSpace(thumbprint))
            {
                // NORMAL, NOT A FAILURE. App Service Managed Certificates issue asynchronously: the
                // PUT returns before Azure has a thumbprint, so the first pass through here almost
                // always lands in this branch. The next domain check (5 minutes) re-PUTs the same
                // certificate resource -- same name, so it is an idempotent update, not a conflict --
                // and by then the thumbprint is populated and the bind below completes.
                //
                // This is how www.4ipro.com and www.drhug.ca both went live in July 2026 with no
                // human involvement, and how www.ouritems.ca did on 2026-08-06.
                //
                // DO NOT "fix" this by treating it as a failure. That was tried on 2026-08-06 on the
                // false premise that managed certificates never issue on this subscription; all three
                // certificates above disprove it. The cost of that mistake was a hand-issued Let's
                // Encrypt certificate that displaced a perfectly good auto-renewing one, plus an
                // alert that would have fired on every single new domain.
                _logger.LogInformation(
                    "Managed certificate for {HostName} is still being issued by Azure; the next domain check will bind it",
                    hostName);

                return new AzureDomainAutomationResult
                {
                    Success = true,
                    BindingCreated = true,
                    CertificateCreated = true,
                    Message = "Azure custom-domain binding was created. Managed certificate is being issued; SSL will be retried on the next check."
                };
            }

            await PutHostNameBindingAsync(hostName, thumbprint, dnsRecordType, cancellationToken);
            return new AzureDomainAutomationResult
            {
                Success = true,
                BindingCreated = true,
                CertificateCreated = true,
                SslBound = true,
                Message = "Azure custom-domain binding and managed SSL were completed."
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure domain automation failed for {HostName}", hostName);
            return new AzureDomainAutomationResult
            {
                Success = false,
                BindingCreated = bindingCreated,
                Message = "Azure automation failed: " + ex.Message
            };
        }
    }

    public async Task<AzureDomainAutomationResult> RemoveDomainAsync(string hostName, CancellationToken cancellationToken = default)
    {
        var stored = hostName;   // 589: the guard reads the name exactly as stored, untrimmed
        hostName = (hostName ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(hostName))
        {
            return AzureDomainAutomationResult.Skipped("Domain name is missing.");
        }

        if (Refusal(stored, "remove", out var canonical) is { } refused)
        {
            return refused;
        }
        hostName = canonical;

        // 492 (audit L5): the same switch AddDomainAsync honours. With automation off, nothing here
        // may delete a live binding or certificate either.
        if (!_options.Enabled)
        {
            return AzureDomainAutomationResult.Skipped("Azure domain automation is disabled. Set AzureDomainAutomation__Enabled=true.");
        }

        if (!_options.HasRequiredBindingSettings)
        {
            return AzureDomainAutomationResult.Skipped($"Azure domain automation settings are incomplete. Missing: {_options.MissingBindingSettingsSummary()}.");
        }

        if (!RunningAsConfiguredApp(out var identityReason))
        {
            _logger.LogError("Azure domain automation refused: {Reason}", identityReason);
            return AzureDomainAutomationResult.Skipped($"Azure domain automation refused: {identityReason}.");
        }

        try
        {
            var bindingUri = ManagementUri(
                $"subscriptions/{_options.SubscriptionId}/resourceGroups/{_options.ResourceGroup}/providers/Microsoft.Web/sites/{_options.WebAppName}/hostNameBindings/{Uri.EscapeDataString(hostName)}");
            using var bindingRequest = new HttpRequestMessage(HttpMethod.Delete, bindingUri);
            await SendManagementDeleteAsync(bindingRequest, cancellationToken);

            if (_options.HasRequiredCertificateSettings)
            {
                // 591: a certificate name is not this host's alone (ManagedCertificateNames), so each name the
                // host's certificate may have is read first, and deleted only when it was ordered for this host.
                // A name that holds nothing is left alone, and one that holds another host's certificate too.
                foreach (var certificateName in new[] { ManagedCertificateNames.For(hostName), ManagedCertificateNames.Legacy(hostName) })
                {
                    var certificate = await GetCertificateAsync(certificateName, cancellationToken);
                    if (certificate == null) continue;
                    if (!IsFor(certificate, hostName))
                    {
                        LogNotThisHosts(certificateName, certificate, hostName, "delete");
                        continue;
                    }

                    using var certRequest = new HttpRequestMessage(HttpMethod.Delete, CertificateUri(certificateName));
                    await SendManagementDeleteAsync(certRequest, cancellationToken);
                }
            }

            return new AzureDomainAutomationResult { Success = true, Message = "Azure hostname binding removed." };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure domain cleanup failed for {HostName}", hostName);
            return AzureDomainAutomationResult.Failed("Azure cleanup failed: " + ex.Message);
        }
    }

    private async Task PutHostNameBindingAsync(string hostName, string? thumbprint, string dnsRecordType, CancellationToken cancellationToken)
    {
        var uri = ManagementUri(
            $"subscriptions/{_options.SubscriptionId}/resourceGroups/{_options.ResourceGroup}/providers/Microsoft.Web/sites/{_options.WebAppName}/hostNameBindings/{Uri.EscapeDataString(hostName)}");

        var properties = new Dictionary<string, object?>
        {
            ["siteName"] = _options.WebAppName,
            ["hostNameType"] = "Verified",
            ["customHostNameDnsRecordType"] = dnsRecordType
        };

        if (!string.IsNullOrWhiteSpace(thumbprint))
        {
            properties["sslState"] = "SniEnabled";
            properties["thumbprint"] = thumbprint;
        }

        var payload = JsonSerializer.Serialize(new { properties });
        using var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        await SendManagementAsync(request, cancellationToken);
    }

    private async Task<string?> EnsureManagedCertificateAsync(string hostName, CancellationToken cancellationToken)
    {
        var certificateName = await CertificateNameToOrderAsync(hostName, cancellationToken);
        var uri = CertificateUri(certificateName);

        var payload = JsonSerializer.Serialize(new
        {
            location = _options.Location,
            properties = new
            {
                canonicalName = hostName,
                serverFarmId = _options.AppServicePlanResourceId
            }
        });

        using var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var response = await SendManagementAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        using var json = JsonDocument.Parse(body);
        if (json.RootElement.TryGetProperty("properties", out var properties) &&
            properties.TryGetProperty("thumbprint", out var thumbprint) &&
            thumbprint.ValueKind == JsonValueKind.String)
        {
            // 591: and the thumbprint goes on this host's binding only when the certificate is this host's.
            if (properties.TryGetProperty("canonicalName", out var canonical) && canonical.ValueKind == JsonValueKind.String &&
                !string.Equals(canonical.GetString(), hostName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Azure answered the certificate order {CertificateName} for {HostName} with a certificate for {CanonicalName}; its thumbprint is not bound",
                    certificateName, hostName, PlatformHostNames.Printable(canonical.GetString()));
                throw new InvalidOperationException($"The certificate {certificateName} was issued for another address, so it was not bound.");
            }

            return thumbprint.GetString();
        }

        return null;
    }

    // 591: which name to order this host's certificate under. A certificate ordered before 591 keeps its old
    // name while it was ordered for this host (canonicalName): it may be issuing, or issued and bound, and a
    // second order would replace nothing and leave two. Everything else is ordered under the host's own name.
    // A name that holds another host's certificate is never ordered over: that would re-order it for this
    // host -- another adviser's live certificate -- and the next pass would bind its thumbprint here.
    private async Task<string> CertificateNameToOrderAsync(string hostName, CancellationToken cancellationToken)
    {
        var legacyName = ManagedCertificateNames.Legacy(hostName);
        var legacy = await GetCertificateAsync(legacyName, cancellationToken);
        if (legacy != null)
        {
            if (IsFor(legacy, hostName)) return legacyName;
            LogNotThisHosts(legacyName, legacy, hostName, "order");
        }

        var certificateName = ManagedCertificateNames.For(hostName);
        var current = await GetCertificateAsync(certificateName, cancellationToken);
        if (current != null && !IsFor(current, hostName))
        {
            LogNotThisHosts(certificateName, current, hostName, "order");
            throw new InvalidOperationException($"The certificate name {certificateName} already holds a certificate for another address.");
        }

        return certificateName;
    }

    // 591: what a certificate name holds in Azure: null when it holds nothing (404), else the host the
    // certificate was ordered for (properties.canonicalName; null when it names none, e.g. one uploaded by
    // hand). Any other answer throws: nothing is ordered or deleted on a guess.
    private async Task<ManagedCertificate?> GetCertificateAsync(string certificateName, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, CertificateUri(certificateName));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {ExtractAzureError(body)}");
        }

        using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        string? canonicalName = null;
        if (json.RootElement.ValueKind == JsonValueKind.Object &&
            json.RootElement.TryGetProperty("properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object &&
            properties.TryGetProperty("canonicalName", out var canonical) &&
            canonical.ValueKind == JsonValueKind.String)
        {
            canonicalName = canonical.GetString();
        }

        return new ManagedCertificate(canonicalName);
    }

    private sealed record ManagedCertificate(string? CanonicalName);

    private static bool IsFor(ManagedCertificate certificate, string hostName) =>
        !string.IsNullOrEmpty(certificate.CanonicalName) &&
        string.Equals(certificate.CanonicalName, hostName, StringComparison.OrdinalIgnoreCase);

    // Warning: Information never reaches the container log, and this is the only record of why a certificate
    // was left alone.
    private void LogNotThisHosts(string certificateName, ManagedCertificate certificate, string hostName, string action) =>
        _logger.LogWarning(
            "Azure domain automation did not {Action} certificate {CertificateName} for {HostName}: it was ordered for {CanonicalName}",
            action, certificateName, hostName,
            string.IsNullOrEmpty(certificate.CanonicalName) ? "no address" : PlatformHostNames.Printable(certificate.CanonicalName));

    private string CertificateUri(string certificateName) => ManagementUri(
        $"subscriptions/{_options.SubscriptionId}/resourceGroups/{_options.ResourceGroup}/providers/Microsoft.Web/certificates/{Uri.EscapeDataString(certificateName)}");

    private async Task<HttpResponseMessage> SendManagementAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var client = _httpClientFactory.CreateClient();
        var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Dispose();
            throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {ExtractAzureError(body)}");
        }

        return response;
    }

    private async Task SendManagementDeleteAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {ExtractAzureError(body)}");
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) &&
            _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return _accessToken;
        }

        var tokenUri = $"https://login.microsoftonline.com/{Uri.EscapeDataString(_options.TenantId)}/oauth2/v2.0/token";
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUri)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["grant_type"] = "client_credentials",
                ["scope"] = "https://management.azure.com/.default"
            })
        };

        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Azure token request failed: " + ExtractAzureError(body));
        }

        using var json = JsonDocument.Parse(body);
        _accessToken = json.RootElement.GetProperty("access_token").GetString();
        var expiresIn = json.RootElement.TryGetProperty("expires_in", out var expires)
            ? expires.GetInt32()
            : 3600;
        _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

        if (string.IsNullOrWhiteSpace(_accessToken))
        {
            throw new InvalidOperationException("Azure token response did not include an access token.");
        }

        return _accessToken;
    }

    private string ManagementUri(string path)
    {
        return $"https://management.azure.com/{path.TrimStart('/')}?api-version={Uri.EscapeDataString(_options.ApiVersion)}";
    }

    private static string ExtractAzureError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "No response body was returned.";
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? body;
                }

                if (error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                {
                    return code.GetString() ?? body;
                }
            }
        }
        catch
        {
            // The Azure error body is sometimes plain text.
        }

        return body.Length > 500 ? body[..500] : body;
    }
}
