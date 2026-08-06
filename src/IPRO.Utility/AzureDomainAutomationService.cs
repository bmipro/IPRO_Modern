using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IPRO.Utility;

public class AzureDomainAutomationService : IAzureDomainAutomationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AzureDomainAutomationOptions _options;
    private readonly ILogger<AzureDomainAutomationService> _logger;
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    public AzureDomainAutomationService(
        IHttpClientFactory httpClientFactory,
        IOptions<AzureDomainAutomationOptions> options,
        ILogger<AzureDomainAutomationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => _options.HasRequiredBindingSettings;

    public async Task<AzureDomainAutomationResult> EnsureDomainAsync(string hostName, CancellationToken cancellationToken = default)
    {
        hostName = (hostName ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(hostName))
        {
            return AzureDomainAutomationResult.Failed("Domain name is missing.");
        }

        if (!_options.Enabled)
        {
            return AzureDomainAutomationResult.Skipped("Azure domain automation is disabled. Set AzureDomainAutomation__Enabled=true.");
        }

        if (!_options.HasRequiredBindingSettings)
        {
            return AzureDomainAutomationResult.Skipped($"Azure domain automation settings are incomplete. Missing: {_options.MissingBindingSettingsSummary()}.");
        }

        try
        {
            await PutHostNameBindingAsync(hostName, null, cancellationToken);

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
                // Azure accepted the certificate request and returned no thumbprint. On this
                // subscription that is the *permanent* outcome, not a transient one: App Service
                // Managed Certificates have never issued here (reproduced across two hostnames in
                // July 2026, which is why the platform domains run on lego). Retrying cannot clear
                // it, so this must not be reported as progress -- the site is live, HTTPS-only, and
                // serving a certificate for the wrong name until a human issues one.
                // See DOCS/20_CERTIFICATES.md; the fix is ops/New-AgentCert.ps1.
                _logger.LogWarning(
                    "Custom domain {HostName} is bound but has NO certificate. Azure returned no thumbprint. " +
                    "The site is unreachable (HTTPS-only + wrong certificate) until one is issued manually: " +
                    "ops/New-AgentCert.ps1 -Domain {HostName}",
                    hostName, hostName);

                return new AzureDomainAutomationResult
                {
                    Success = true,
                    BindingCreated = true,
                    CertificateCreated = false,
                    CertificateNeedsManualIssue = true,
                    Message = "Azure custom-domain binding was created, but no SSL certificate could be issued automatically. " +
                              "The site will show visitors a security warning until IPRO issues one."
                };
            }

            await PutHostNameBindingAsync(hostName, thumbprint, cancellationToken);
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
            return AzureDomainAutomationResult.Failed("Azure automation failed: " + ex.Message);
        }
    }

    public async Task<AzureDomainAutomationResult> RemoveDomainAsync(string hostName, CancellationToken cancellationToken = default)
    {
        hostName = (hostName ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(hostName))
        {
            return AzureDomainAutomationResult.Skipped("Domain name is missing.");
        }

        if (!_options.HasRequiredBindingSettings)
        {
            return AzureDomainAutomationResult.Skipped($"Azure domain automation settings are incomplete. Missing: {_options.MissingBindingSettingsSummary()}.");
        }

        try
        {
            var bindingUri = ManagementUri(
                $"subscriptions/{_options.SubscriptionId}/resourceGroups/{_options.ResourceGroup}/providers/Microsoft.Web/sites/{_options.WebAppName}/hostNameBindings/{Uri.EscapeDataString(hostName)}");
            using var bindingRequest = new HttpRequestMessage(HttpMethod.Delete, bindingUri);
            await SendManagementDeleteAsync(bindingRequest, cancellationToken);

            if (_options.HasRequiredCertificateSettings)
            {
                var certificateName = "managed-" + hostName.Replace(".", "-", StringComparison.OrdinalIgnoreCase);
                if (certificateName.Length > 80)
                {
                    certificateName = certificateName[..80];
                }

                var certUri = ManagementUri(
                    $"subscriptions/{_options.SubscriptionId}/resourceGroups/{_options.ResourceGroup}/providers/Microsoft.Web/certificates/{Uri.EscapeDataString(certificateName)}");
                using var certRequest = new HttpRequestMessage(HttpMethod.Delete, certUri);
                await SendManagementDeleteAsync(certRequest, cancellationToken);
            }

            return new AzureDomainAutomationResult { Success = true, Message = "Azure hostname binding removed." };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure domain cleanup failed for {HostName}", hostName);
            return AzureDomainAutomationResult.Failed("Azure cleanup failed: " + ex.Message);
        }
    }

    private async Task PutHostNameBindingAsync(string hostName, string? thumbprint, CancellationToken cancellationToken)
    {
        var uri = ManagementUri(
            $"subscriptions/{_options.SubscriptionId}/resourceGroups/{_options.ResourceGroup}/providers/Microsoft.Web/sites/{_options.WebAppName}/hostNameBindings/{Uri.EscapeDataString(hostName)}");

        var properties = new Dictionary<string, object?>
        {
            ["siteName"] = _options.WebAppName,
            ["hostNameType"] = "Verified",
            ["customHostNameDnsRecordType"] = "CName"
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
        var certificateName = "managed-" + hostName.Replace(".", "-", StringComparison.OrdinalIgnoreCase);
        if (certificateName.Length > 80)
        {
            certificateName = certificateName[..80];
        }

        var uri = ManagementUri(
            $"subscriptions/{_options.SubscriptionId}/resourceGroups/{_options.ResourceGroup}/providers/Microsoft.Web/certificates/{Uri.EscapeDataString(certificateName)}");

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
            return thumbprint.GetString();
        }

        return null;
    }

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
