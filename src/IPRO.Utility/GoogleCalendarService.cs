using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IPRO.Utility;

public record GoogleCalendarEventData(string GoogleEventId, string Title, DateTime StartAt, DateTime? EndAt, bool IsCancelled);
public record GoogleTokenResult(string AccessToken, string RefreshToken, DateTime ExpiresAt, string AccountEmail);
public record GoogleEventListResult(IReadOnlyList<GoogleCalendarEventData> Events, string? NextSyncToken, bool RequiresFullResync);

public interface IGoogleCalendarService
{
    string BuildAuthorizationUrl(string redirectUri, string state);
    Task<GoogleTokenResult> ExchangeCodeAsync(string code, string redirectUri);
    Task<(string AccessToken, DateTime ExpiresAt)> RefreshAccessTokenAsync(string refreshToken);
    Task<GoogleEventListResult> ListEventsAsync(string accessToken, string calendarId, string? syncToken);
    Task<string> CreateEventAsync(string accessToken, string calendarId, string title, DateTime startAtUtc, DateTime? endAtUtc);
    Task UpdateEventAsync(string accessToken, string calendarId, string googleEventId, string title, DateTime startAtUtc, DateTime? endAtUtc);
    Task DeleteEventAsync(string accessToken, string calendarId, string googleEventId);
    Task RevokeTokenAsync(string token);
}

public class GoogleCalendarService : IGoogleCalendarService
{
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    private const string UserInfoEndpoint = "https://www.googleapis.com/oauth2/v2/userinfo";
    private const string CalendarApiBase = "https://www.googleapis.com/calendar/v3";
    private const string Scope = "https://www.googleapis.com/auth/calendar";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GoogleCalendarSettings _settings;
    private readonly ILogger<GoogleCalendarService> _logger;

    public GoogleCalendarService(IHttpClientFactory httpClientFactory, IOptions<GoogleCalendarSettings> settings, ILogger<GoogleCalendarService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    public string BuildAuthorizationUrl(string redirectUri, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = _settings.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = Scope,
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["state"] = state
        };

        var queryString = string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{AuthEndpoint}?{queryString}";
    }

    public async Task<GoogleTokenResult> ExchangeCodeAsync(string code, string redirectUri)
    {
        var client = _httpClientFactory.CreateClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = _settings.ClientId,
            ["client_secret"] = _settings.ClientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code"
        });

        using var response = await client.PostAsync(TokenEndpoint, content);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Google token exchange failed: {Response}", json);
            throw new InvalidOperationException("Google rejected the authorization code. Please try connecting again.");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var accessToken = root.GetProperty("access_token").GetString() ?? string.Empty;
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? string.Empty : string.Empty;
        var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("Google did not return a refresh token. Disconnect any existing IPRO connection for this Google account, then reconnect (Google only issues a refresh token the first time an app is authorized).");
        }

        var accountEmail = await GetAccountEmailAsync(accessToken);

        return new GoogleTokenResult(accessToken, refreshToken, DateTime.UtcNow.AddSeconds(expiresIn), accountEmail);
    }

    public async Task<(string AccessToken, DateTime ExpiresAt)> RefreshAccessTokenAsync(string refreshToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["refresh_token"] = refreshToken,
            ["client_id"] = _settings.ClientId,
            ["client_secret"] = _settings.ClientSecret,
            ["grant_type"] = "refresh_token"
        });

        using var response = await client.PostAsync(TokenEndpoint, content);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Google token refresh failed: {Response}", json);
            throw new InvalidOperationException("Google Calendar connection has expired or was revoked. Please reconnect.");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var accessToken = root.GetProperty("access_token").GetString() ?? string.Empty;
        var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;

        return (accessToken, DateTime.UtcNow.AddSeconds(expiresIn));
    }

    public async Task RevokeTokenAsync(string token)
    {
        var client = _httpClientFactory.CreateClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
        using var response = await client.PostAsync(RevokeEndpoint, content);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Google token revoke returned {Status} - proceeding with local disconnect anyway", response.StatusCode);
        }
    }

    public async Task<GoogleEventListResult> ListEventsAsync(string accessToken, string calendarId, string? syncToken)
    {
        var client = AuthorizedClient(accessToken);
        var url = string.IsNullOrWhiteSpace(syncToken)
            ? $"{CalendarApiBase}/calendars/{Uri.EscapeDataString(calendarId)}/events?singleEvents=true&showDeleted=true&timeMin={Uri.EscapeDataString(DateTime.UtcNow.AddDays(-30).ToString("o"))}&timeMax={Uri.EscapeDataString(DateTime.UtcNow.AddYears(1).ToString("o"))}"
            : $"{CalendarApiBase}/calendars/{Uri.EscapeDataString(calendarId)}/events?singleEvents=true&showDeleted=true&syncToken={Uri.EscapeDataString(syncToken)}";

        var events = new List<GoogleCalendarEventData>();
        string? nextSyncToken = null;
        var pageUrl = url;

        while (true)
        {
            using var response = await client.GetAsync(pageUrl);
            var json = await response.Content.ReadAsStringAsync();

            if (response.StatusCode == System.Net.HttpStatusCode.Gone)
            {
                // syncToken expired/invalid - caller must clear it and do a fresh full sync.
                return new GoogleEventListResult(Array.Empty<GoogleCalendarEventData>(), null, true);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Google events.list failed: {Response}", json);
                throw new InvalidOperationException("Could not read events from Google Calendar.");
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString() ?? string.Empty;
                    var status = item.TryGetProperty("status", out var st) ? st.GetString() : null;
                    var isCancelled = status == "cancelled";

                    if (isCancelled)
                    {
                        events.Add(new GoogleCalendarEventData(id, string.Empty, DateTime.MinValue, null, true));
                        continue;
                    }

                    var title = item.TryGetProperty("summary", out var s) ? s.GetString() ?? string.Empty : string.Empty;
                    var start = ParseEventDateTime(item, "start");
                    var end = ParseEventDateTime(item, "end");
                    if (start == null) continue;

                    events.Add(new GoogleCalendarEventData(id, title, start.Value, end, false));
                }
            }

            if (root.TryGetProperty("nextSyncToken", out var nst))
            {
                nextSyncToken = nst.GetString();
            }

            if (root.TryGetProperty("nextPageToken", out var npt) && npt.GetString() is { } nextPage)
            {
                var separator = url.Contains('?') ? "&" : "?";
                pageUrl = $"{url}{separator}pageToken={Uri.EscapeDataString(nextPage)}";
                continue;
            }

            break;
        }

        return new GoogleEventListResult(events, nextSyncToken, false);
    }

    public async Task<string> CreateEventAsync(string accessToken, string calendarId, string title, DateTime startAtUtc, DateTime? endAtUtc)
    {
        var client = AuthorizedClient(accessToken);
        var payload = BuildEventPayload(title, startAtUtc, endAtUtc);

        using var response = await client.PostAsync(
            $"{CalendarApiBase}/calendars/{Uri.EscapeDataString(calendarId)}/events",
            new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"));

        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Google event create failed: {Response}", json);
            throw new InvalidOperationException("Could not create the event in Google Calendar.");
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("id").GetString() ?? string.Empty;
    }

    public async Task UpdateEventAsync(string accessToken, string calendarId, string googleEventId, string title, DateTime startAtUtc, DateTime? endAtUtc)
    {
        var client = AuthorizedClient(accessToken);
        var payload = BuildEventPayload(title, startAtUtc, endAtUtc);

        var request = new HttpRequestMessage(HttpMethod.Patch, $"{CalendarApiBase}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(googleEventId)}")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            var json = await response.Content.ReadAsStringAsync();
            _logger.LogError("Google event update failed: {Response}", json);
            throw new InvalidOperationException("Could not update the event in Google Calendar.");
        }
    }

    public async Task DeleteEventAsync(string accessToken, string calendarId, string googleEventId)
    {
        var client = AuthorizedClient(accessToken);
        using var response = await client.DeleteAsync($"{CalendarApiBase}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(googleEventId)}");
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound && response.StatusCode != System.Net.HttpStatusCode.Gone)
        {
            var json = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Google event delete returned {Status}: {Response}", response.StatusCode, json);
        }
    }

    private HttpClient AuthorizedClient(string accessToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private async Task<string> GetAccountEmailAsync(string accessToken)
    {
        var client = AuthorizedClient(accessToken);
        using var response = await client.GetAsync(UserInfoEndpoint);
        if (!response.IsSuccessStatusCode) return string.Empty;

        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("email", out var email) ? email.GetString() ?? string.Empty : string.Empty;
    }

    private static object BuildEventPayload(string title, DateTime startAtUtc, DateTime? endAtUtc)
    {
        var start = DateTime.SpecifyKind(startAtUtc, DateTimeKind.Utc);
        var end = endAtUtc.HasValue ? DateTime.SpecifyKind(endAtUtc.Value, DateTimeKind.Utc) : start.AddHours(1);

        return new
        {
            summary = title,
            start = new { dateTime = start.ToString("o"), timeZone = "UTC" },
            end = new { dateTime = end.ToString("o"), timeZone = "UTC" }
        };
    }

    private static DateTime? ParseEventDateTime(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var node)) return null;

        if (node.TryGetProperty("dateTime", out var dt) && dt.GetString() is { } dateTimeString)
        {
            return DateTimeOffset.Parse(dateTimeString).UtcDateTime;
        }

        if (node.TryGetProperty("date", out var d) && d.GetString() is { } dateString)
        {
            return DateTime.SpecifyKind(DateTime.Parse(dateString), DateTimeKind.Utc);
        }

        return null;
    }
}
