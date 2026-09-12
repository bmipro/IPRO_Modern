using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 485 (2026-09-12). Google Calendar connect failed with redirect_uri_mismatch on every host --
// an agent's domain, the platform host, the temporary domain -- the owner's test on 09-12. Connect
// and Callback built the OAuth redirect_uri with Url.ActionLink, which reflects the route table; since
// the portal got its own URL space on 2026-08-07 (the "portal" route registered ahead of "default")
// that produced https://app.iproadvisers.com/portal/GoogleCalendar/Callback, and Google only has the
// unprefixed address registered. Nobody had connected a calendar in production in between.
//
// Fix: the redirect_uri is the one out-of-band address Google is told to call, so it is built from
// the canonical base and a literal path, never from routes, and the Callback action has its own
// explicit route at that path so it is reachable there whatever the conventional routes do.
public class GoogleCalendarRedirectUriTests
{
    private static IConfiguration Config(params (string key, string? value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.key, p => p.value)).Build();

    [Fact]
    public void The_callback_address_comes_from_configuration_and_a_literal_path_never_from_routes()
    {
        Assert.Equal("https://app.iproadvisers.com/GoogleCalendar/Callback",
            PortalUrlHelper.GoogleCalendarRedirectUri(Config(("App:BaseUrl", "https://app.iproadvisers.com/"))));
        Assert.Equal("https://app.iproadvisers.com/GoogleCalendar/Callback",
            PortalUrlHelper.GoogleCalendarRedirectUri(Config(("App:PortalBaseUrl", "https://app.iproadvisers.com"))));

        // Nothing configured: whatever the platform's fallback base is, the path is the registered one.
        var fallback = PortalUrlHelper.GoogleCalendarRedirectUri(Config());
        Assert.EndsWith("/GoogleCalendar/Callback", fallback);
        Assert.StartsWith("https://", fallback);
        Assert.DoesNotContain("/portal/", fallback);
    }

    [Fact]
    public void Connect_and_Callback_send_the_same_fixed_address_and_Callback_answers_at_it()
    {
        var source = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\GoogleCalendarController.cs"));
        Assert.True(source.Split("PortalUrlHelper.GoogleCalendarRedirectUri(").Length - 1 >= 2,
            "Connect and Callback must both take the redirect_uri from PortalUrlHelper.GoogleCalendarRedirectUri");
        Assert.DoesNotContain("Url.ActionLink(nameof(Callback))", source);   // the route-table-dependent form

        var callback = typeof(GoogleCalendarController).GetMethod("Callback");
        Assert.NotNull(callback);
        var routes = callback!.GetCustomAttributes(typeof(HttpGetAttribute), false).Cast<HttpGetAttribute>().Select(a => a.Template).ToList();
        Assert.Contains("/GoogleCalendar/Callback", routes);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
