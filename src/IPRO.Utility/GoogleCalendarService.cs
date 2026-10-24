using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IPRO.Utility;

// 601 (2026-10-24): StartAt/EndAt are a UTC instant for a timed event and the calendar date for an all-day one
// (IsAllDay; Google's end date is the day after). TimeZone is the zone the event's start was written in, as
// Google reports it (null when it sends none) -- the sync job uses it to recognise events the code before 601
// wrote with timeZone "UTC".
public record GoogleCalendarEventData(string GoogleEventId, string Title, DateTime StartAt, DateTime? EndAt, bool IsCancelled, bool IsAllDay = false, string? TimeZone = null);
public record GoogleTokenResult(string AccessToken, string RefreshToken, DateTime ExpiresAt, string AccountEmail);
public record GoogleEventListResult(IReadOnlyList<GoogleCalendarEventData> Events, string? NextSyncToken, bool RequiresFullResync);

public interface IGoogleCalendarService
{
    string BuildAuthorizationUrl(string redirectUri, string state);
    Task<GoogleTokenResult> ExchangeCodeAsync(string code, string redirectUri);
    Task<(string AccessToken, DateTime ExpiresAt)> RefreshAccessTokenAsync(string refreshToken);
    Task<GoogleEventListResult> ListEventsAsync(string accessToken, string calendarId, string? syncToken);
    // 601 (2026-10-24): times are the adviser's own wall clock (a follow-up's DueAt as stored, never converted)
    // plus the adviser's zone; BuildEventPayload turns them into what Google wants.
    Task<string> CreateEventAsync(string accessToken, string calendarId, string title, DateTime localStart, DateTime? localEnd, TimeZoneInfo zone);
    Task UpdateEventAsync(string accessToken, string calendarId, string googleEventId, string title, DateTime localStart, DateTime? localEnd, TimeZoneInfo zone);
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
    // 486 (2026-09-12): the narrowest scope that covers what the sync does -- list, insert, update and
    // delete events on the adviser's chosen calendar -- plus the account's email so the adviser can see
    // which Google account is connected. Google's app verification is per scope; the full "calendar"
    // scope (settings, sharing, every calendar) was never needed. The console's Data Access page must
    // list this exact scope or Google answers ACCESS_TOKEN_SCOPE_INSUFFICIENT (DOCS/09, DOCS/27).
    private const string Scope = "https://www.googleapis.com/auth/calendar.events https://www.googleapis.com/auth/userinfo.email";

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

    // 487 (2026-09-12): does the grant Google actually returned let the sync work? Its consent screen
    // shows the calendar permission as a box the person can leave unticked (and the console's Data
    // Access list can drop a scope it does not list); the token then carries only the email, and a
    // connection stored on it fails every sync with 403. calendar.events is what the app asks for;
    // an older, wider "calendar" grant still counts.
    public static bool GrantsCalendarAccess(string? grantedScopes)
    {
        if (string.IsNullOrWhiteSpace(grantedScopes)) return false;
        foreach (var scope in grantedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (scope == "https://www.googleapis.com/auth/calendar.events" || scope == "https://www.googleapis.com/auth/calendar") return true;
        }
        return false;
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

        // 487: refuse a grant without calendar access here, while the person is still looking, instead
        // of storing it as a success and failing every sync run afterwards (the owner's 09-12 evening).
        var grantedScopes = root.TryGetProperty("scope", out var sc) ? sc.GetString() : null;
        if (!GrantsCalendarAccess(grantedScopes))
        {
            _logger.LogWarning("Google granted '{Scopes}' without calendar access; the connection is refused.", grantedScopes ?? "(none)");
            throw new InvalidOperationException("Google did not grant access to your calendar. Connect again and, on Google's screen, tick the box for your calendar events before you allow.");
        }

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

                    events.Add(new GoogleCalendarEventData(id, title, start.Value.Value, end?.Value, false, start.Value.AllDay, start.Value.TimeZone));
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

    public async Task<string> CreateEventAsync(string accessToken, string calendarId, string title, DateTime localStart, DateTime? localEnd, TimeZoneInfo zone)
    {
        var client = AuthorizedClient(accessToken);
        var payload = BuildEventPayload(title, localStart, localEnd, zone);

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

    public async Task UpdateEventAsync(string accessToken, string calendarId, string googleEventId, string title, DateTime localStart, DateTime? localEnd, TimeZoneInfo zone)
    {
        var client = AuthorizedClient(accessToken);
        var payload = BuildEventPayload(title, localStart, localEnd, zone, forPatch: true);

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

    // 601 (2026-10-24): a follow-up's DueAt is the adviser's own wall clock (INVARIANTS rule 10). Until 601 this
    // sent it as UTC (timeZone "UTC"), so a 2 p.m. appointment landed in a Toronto calendar at 10 a.m. (9 a.m. in
    // winter) and a follow-up due 24 October at 8 p.m. on the 23rd. Now:
    // - a timed one goes out as that wall clock with the offset it has ON THAT DATE in the adviser's zone, plus the
    //   zone's IANA name -- the form Google's own examples use. The offset makes the instant unambiguous (a date
    //   after the November change carries -05:00 in Toronto, one before it -04:00); the name is the event's zone.
    // - a date with no time -- midnight, the product's rule for "no time" (FollowUpReminderJob.TimeToday) -- is an
    //   all-day event on that date (Google's end date is the next day), as TRUTH_SWEEP_2026-09-18 item 11 planned:
    //   a midnight-to-1 a.m. block would sit above the morning in Google's day view and read as a time.
    // forPatch: Google's PATCH merges into the event's stored start and end, so the kind not used is sent as null
    // to clear it -- that is how an event the old code wrote as timed becomes all-day.
    public static object BuildEventPayload(string title, DateTime localStart, DateTime? localEnd, TimeZoneInfo zone, bool forPatch = false)
    {
        if (IsDateOnly(localStart))
        {
            return new
            {
                summary = title,
                start = AllDay(localStart.Date, forPatch),
                end = AllDay(localStart.Date.AddDays(1), forPatch)
            };
        }

        var end = localEnd.HasValue && localEnd.Value > localStart ? localEnd.Value : localStart.AddHours(1);
        var zoneName = GoogleTimeZoneName(zone);
        return new
        {
            summary = title,
            start = Timed(localStart, zone, zoneName, forPatch),
            end = Timed(end, zone, zoneName, forPatch)
        };
    }

    // The product's rule: a DueAt at midnight was typed as a date with no time (the follow-up form, the life-event
    // job); an appointment keeps the time it was booked for.
    public static bool IsDateOnly(DateTime localDueAt) => localDueAt.TimeOfDay == TimeSpan.Zero;

    // Google takes IANA names ("America/Toronto"). The adviser's zone is found by an IANA id on every host .NET 6+
    // runs on; a Windows id is converted, and anything else falls back to UTC (what AgentLocalTime falls back to).
    public static string GoogleTimeZoneName(TimeZoneInfo zone)
    {
        if (zone.HasIanaId) return zone.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) && !string.IsNullOrEmpty(iana) ? iana : "UTC";
    }

    private static Dictionary<string, object?> AllDay(DateTime date, bool forPatch)
    {
        var node = new Dictionary<string, object?> { ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        if (forPatch)
        {
            node["dateTime"] = null;
            node["timeZone"] = null;
        }
        return node;
    }

    private static Dictionary<string, object?> Timed(DateTime local, TimeZoneInfo zone, string zoneName, bool forPatch)
    {
        var wallClock = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var withOffset = new DateTimeOffset(wallClock, zone.GetUtcOffset(wallClock));
        var node = new Dictionary<string, object?>
        {
            ["dateTime"] = withOffset.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ["timeZone"] = zoneName
        };
        if (forPatch) node["date"] = null;
        return node;
    }

    // 601 (2026-10-24): Google answers a timed event with an offset ("2026-11-03T10:30:00-05:00", or "Z"), which
    // is read as the instant it names; the sync job turns that into the adviser's wall clock. An all-day event is
    // a bare date and stays one -- no zone applies to it. The parse is culture-free (DateTime.Parse read the
    // server's culture before).
    private static (DateTime Value, bool AllDay, string? TimeZone)? ParseEventDateTime(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var node)) return null;

        var zone = node.TryGetProperty("timeZone", out var tz) && tz.ValueKind == JsonValueKind.String ? tz.GetString() : null;

        if (node.TryGetProperty("dateTime", out var dt) && dt.ValueKind == JsonValueKind.String && dt.GetString() is { } dateTimeString)
        {
            return (DateTimeOffset.Parse(dateTimeString, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcDateTime, false, zone);
        }

        if (node.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { } dateString)
        {
            return (DateTime.ParseExact(dateString, "yyyy-MM-dd", CultureInfo.InvariantCulture), true, zone);
        }

        return null;
    }
}
