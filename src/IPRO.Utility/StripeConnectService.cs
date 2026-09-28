using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IPRO.Utility;

// 527 (2026-09-27): iPro's own Stripe platform account. The keys are App Service settings (empty
// means "not set up", and the Payments page says so); test keys in appsettings.Development.json
// drive Stripe's test mode locally.
public class StripeSettings
{
    public string SecretKey { get; set; } = string.Empty;       // sk_test_... or sk_live_...
    public string ClientId { get; set; } = string.Empty;        // ca_... (the Connect client id)
    public string WebhookSecret { get; set; } = string.Empty;   // whsec_... (the Connect webhook endpoint)
    public bool IsConfigured => !string.IsNullOrWhiteSpace(SecretKey) && !string.IsNullOrWhiteSpace(ClientId);
    public bool IsLive => SecretKey.StartsWith("sk_live_", StringComparison.Ordinal);
}

public record StripeConnectedAccount(string AccountId, bool LiveMode, string DisplayName);

public interface IStripeConnectService
{
    bool IsConfigured { get; }
    string BuildAuthorizationUrl(string redirectUri, string state);
    Task<StripeConnectedAccount> ExchangeCodeAsync(string code);
    Task DeauthorizeAsync(string accountId);
}

// Stripe Connect with Standard accounts. The adviser signs in at Stripe and grants iPro access;
// Stripe hands back the connected account's id, and from then on every call for that adviser is
// made with iPro's own key plus a Stripe-Account header. Nothing secret of the adviser's is ever
// stored -- an account id is not a secret -- and the money moves straight to their account.
public class StripeConnectService : IStripeConnectService
{
    private const string AuthorizeEndpoint = "https://connect.stripe.com/oauth/authorize";
    private const string TokenEndpoint = "https://connect.stripe.com/oauth/token";
    private const string DeauthorizeEndpoint = "https://connect.stripe.com/oauth/deauthorize";
    private const string ApiBase = "https://api.stripe.com/v1";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly StripeSettings _settings;
    private readonly ILogger<StripeConnectService> _logger;

    public StripeConnectService(IHttpClientFactory httpClientFactory, IOptions<StripeSettings> settings, ILogger<StripeConnectService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    public bool IsConfigured => _settings.IsConfigured;

    public string BuildAuthorizationUrl(string redirectUri, string state) => AuthorizationUrl(_settings.ClientId, redirectUri, state);

    // read_write is the scope a platform needs to create Checkout Sessions on the connected
    // account; read_only would only let us look.
    public static string AuthorizationUrl(string clientId, string redirectUri, string state) =>
        $"{AuthorizeEndpoint}?response_type=code&client_id={Uri.EscapeDataString(clientId)}&scope=read_write" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(state)}";

    public async Task<StripeConnectedAccount> ExchangeCodeAsync(string code)
    {
        var client = _httpClientFactory.CreateClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_secret"] = _settings.SecretKey,
            ["code"] = code,
            ["grant_type"] = "authorization_code"
        });
        using var response = await client.PostAsync(TokenEndpoint, form);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Stripe Connect token exchange failed ({Status}): {Body}", (int)response.StatusCode, Clip(body));
            throw new InvalidOperationException("Stripe did not accept the connection. Please try again.");
        }

        using var doc = JsonDocument.Parse(body);
        var accountId = doc.RootElement.TryGetProperty("stripe_user_id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new InvalidOperationException("Stripe did not return an account id.");
        }
        var liveMode = doc.RootElement.TryGetProperty("livemode", out var liveElement) && liveElement.ValueKind == JsonValueKind.True;
        var displayName = await TryReadDisplayNameAsync(client, accountId);
        return new StripeConnectedAccount(accountId, liveMode, displayName);
    }

    public async Task DeauthorizeAsync(string accountId)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, DeauthorizeEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _settings.ClientId,
                ["stripe_user_id"] = accountId
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.SecretKey);
        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            // The local record closes regardless; the adviser can also revoke iPro from their Stripe dashboard.
            _logger.LogWarning("Stripe Connect deauthorize for {AccountId} answered {Status}", accountId, (int)response.StatusCode);
        }
    }

    // The account's own name for the Payments page: dashboard display name, then business name,
    // then the account email. Best effort -- a connection without a name is still a connection.
    private async Task<string> TryReadDisplayNameAsync(HttpClient client, string accountId)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/accounts/{Uri.EscapeDataString(accountId)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.SecretKey);
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return string.Empty;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("settings", out var settings) && settings.TryGetProperty("dashboard", out var dashboard)
                && dashboard.TryGetProperty("display_name", out var display) && display.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(display.GetString()))
                return display.GetString()!;
            if (root.TryGetProperty("business_profile", out var profile) && profile.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()))
                return name.GetString()!;
            if (root.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String)
                return email.GetString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the Stripe account name for {AccountId}", accountId);
        }
        return string.Empty;
    }

    private static string Clip(string value) => value.Length <= 300 ? value : value[..300];
}

// The Stripe-Signature header: "t=<unix seconds>,v1=<hex hmac>[,v1=<older key's hmac>]", the
// HMAC-SHA256 of "<t>.<raw body>" with the endpoint's secret. Checked in constant time, and the
// timestamp must be within five minutes so a captured notice cannot be replayed later.
public static class StripeWebhookSignature
{
    public const int ToleranceSeconds = 300;

    public static bool IsValid(string payload, string? signatureHeader, string secret, long nowUnixSeconds)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(secret)) return false;

        long timestamp = 0;
        var signatures = new List<string>();
        foreach (var part in signatureHeader.Split(','))
        {
            var pair = part.Trim().Split('=', 2);
            if (pair.Length != 2) continue;
            if (pair[0] == "t") long.TryParse(pair[1], out timestamp);
            else if (pair[0] == "v1") signatures.Add(pair[1].Trim().ToLowerInvariant());
        }
        if (timestamp == 0 || signatures.Count == 0) return false;
        if (Math.Abs(nowUnixSeconds - timestamp) > ToleranceSeconds) return false;

        var expected = Encoding.ASCII.GetBytes(Sign(payload, secret, timestamp));
        foreach (var signature in signatures)
        {
            var actual = Encoding.ASCII.GetBytes(signature);
            if (actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected)) return true;
        }
        return false;
    }

    public static string Sign(string payload, string secret, long timestamp)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();
    }

    public static string Header(string payload, string secret, long timestamp) => $"t={timestamp},v1={Sign(payload, secret, timestamp)}";
}
