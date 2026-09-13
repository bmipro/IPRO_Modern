using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Utility;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 487 (2026-09-12). The owner reconnected his calendar on the new build and every sync run then
// failed with Google's 403 "insufficient authentication scopes": Google's consent screen shows the
// calendar permission as a box the person can leave unticked, and the token that comes back then
// carries only the email. The connection had been stored as a success and failed silently every
// 15 minutes. Now the token exchange reads the scopes Google actually granted and refuses the
// connection, with a message that says what to do, while the person is still looking.
public class GoogleCalendarGrantedScopeTests
{
    private const string Events = "https://www.googleapis.com/auth/calendar.events";
    private const string Full = "https://www.googleapis.com/auth/calendar";
    private const string Email = "https://www.googleapis.com/auth/userinfo.email";

    [Theory]
    [InlineData(Events + " " + Email, true)]
    [InlineData(Email + " " + Full, true)]            // an older, wider grant still counts
    [InlineData(Email + " openid", false)]            // the calendar box left unticked
    [InlineData("https://www.googleapis.com/auth/calendar.readonly " + Email, false)]   // read-only cannot sync follow-ups out
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Calendar_access_is_recognised_in_the_scopes_google_actually_granted(string? scopes, bool expected)
    {
        Assert.Equal(expected, GoogleCalendarService.GrantsCalendarAccess(scopes));
    }

    [Fact]
    public async Task A_consent_without_the_calendar_box_is_refused_at_connect_time_with_a_plain_message()
    {
        var google = new FakeGoogle(grantedScopes: Email + " openid");
        var service = NewService(google);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExchangeCodeAsync("code", "https://app.iproadvisers.com/GoogleCalendar/Callback"));

        Assert.Contains("calendar", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tick", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_consent_with_the_calendar_box_connects()
    {
        var google = new FakeGoogle(grantedScopes: Events + " " + Email);
        var service = NewService(google);

        var result = await service.ExchangeCodeAsync("code", "https://app.iproadvisers.com/GoogleCalendar/Callback");

        Assert.Equal("agent@example.com", result.AccountEmail);
        Assert.Equal("access-1", result.AccessToken);
        Assert.Equal("refresh-1", result.RefreshToken);
    }

    private static GoogleCalendarService NewService(FakeGoogle google) =>
        new(new Factory(google), Options.Create(new GoogleCalendarSettings { ClientId = "client-id", ClientSecret = "secret" }), NullLogger<GoogleCalendarService>.Instance);

    // Google's two endpoints the exchange talks to: the token endpoint (with the granted scopes in the
    // answer, as Google returns them) and the userinfo endpoint.
    private sealed class FakeGoogle : HttpMessageHandler
    {
        private readonly string _grantedScopes;
        public FakeGoogle(string grantedScopes) => _grantedScopes = grantedScopes;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            string body = path.EndsWith("/token", StringComparison.Ordinal)
                ? "{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\",\"expires_in\":3600,\"token_type\":\"Bearer\",\"scope\":\"" + _grantedScopes + "\"}"
                : path.Contains("userinfo", StringComparison.Ordinal)
                    ? "{\"email\":\"agent@example.com\",\"verified_email\":true}"
                    : null!;
            if (body == null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public Factory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
