using System;
using System.Net.Http;
using System.Web;
using IPRO.Utility;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 486 (2026-09-12). Google's app verification is per scope, and the sync only ever lists,
// inserts, updates and deletes events on the adviser's chosen calendar -- it never reads calendar
// settings, sharing or the calendar list. So the app asks for calendar.events, the narrowest scope
// that covers that, plus userinfo.email to show which account was connected. The full calendar
// scope it used to ask for would have to be justified in the verification and was never needed.
// The console's Data Access page must list the same scope, or Google answers
// ACCESS_TOKEN_SCOPE_INSUFFICIENT (DOCS/09, the 2026-07 incident).
public class GoogleCalendarScopeTests
{
    [Fact]
    public void The_app_asks_google_for_events_access_and_the_account_email_and_nothing_wider()
    {
        var service = new GoogleCalendarService(new NoHttp(), Options.Create(new GoogleCalendarSettings { ClientId = "client-id", ClientSecret = "secret" }), NullLogger<GoogleCalendarService>.Instance);

        var url = service.BuildAuthorizationUrl("https://app.iproadvisers.com/GoogleCalendar/Callback", "state");

        var query = HttpUtility.ParseQueryString(new Uri(url).Query);
        var scopes = (query["scope"] ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("https://www.googleapis.com/auth/calendar.events", scopes);
        Assert.Contains("https://www.googleapis.com/auth/userinfo.email", scopes);
        Assert.DoesNotContain("https://www.googleapis.com/auth/calendar", scopes);   // the full scope: settings, sharing, every calendar
        Assert.Equal(2, scopes.Length);

        // The rest of the request as Google expects it for a refresh-token flow.
        Assert.Equal("offline", query["access_type"]);
        Assert.Equal("consent", query["prompt"]);
        Assert.Equal("https://app.iproadvisers.com/GoogleCalendar/Callback", query["redirect_uri"]);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no HTTP in this test");
    }
}
