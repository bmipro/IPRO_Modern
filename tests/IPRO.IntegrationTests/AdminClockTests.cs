using System;
using System.Collections.Generic;
using System.IO;
using IPRO.Admin.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 432 (2026-09-09). The Admin header clock rendered DateTime.Now, which on Linux App Service
// is UTC: it read "8:42 PM" at 4:42 in the afternoon in Toronto. It now shows the platform's own
// time zone -- Admin:TimeZone in configuration, one of the AgentLocalTime names, Eastern when
// unset -- with a short label so the reader knows which clock they are looking at.
public class AdminClockTests
{
    private static readonly DateTime Instant = new(2026, 9, 9, 17, 15, 0, DateTimeKind.Utc);

    [Fact]
    public void The_clock_shows_eastern_time_when_nothing_is_configured()
    {
        var config = new ConfigurationBuilder().Build();
        Assert.Equal("Sep 9, 2026 1:15 PM ET", AdminClock.Format(Instant, AdminClock.Zone(config)));
    }

    [Fact]
    public void The_clock_follows_the_configured_zone()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AdminClock.ConfigKey] = "(GMT-08:00) Pacific Time (US & Canada)" })
            .Build();
        Assert.Equal("Sep 9, 2026 10:15 AM PT", AdminClock.Format(Instant, AdminClock.Zone(config)));
    }

    [Fact]
    public void The_admin_layout_uses_the_clock_not_the_server_time()
    {
        var layout = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\Shared\_Layout.cshtml"));
        Assert.DoesNotContain("DateTime.Now", layout);
        Assert.Contains("AdminClock.Format(", layout);
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
