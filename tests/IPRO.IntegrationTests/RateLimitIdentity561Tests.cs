using System;
using System.IO;
using System.Linq;
using System.Net;
using AspNetCoreRateLimit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// 561 (2026-10-11). Found while reading the live log before an email step: 69 unhandled errors in two
// days, all "An invalid IP address was specified" from the rate limiter. A request with the header
// `X-Real-IP: not-an-ip` answered 500 on the platform, on a customer's site and on the admin host; a
// well-formed made-up address was accepted as the visitor, so the limits on sign-in, registration,
// password reset and the public forms could be side-stepped by changing the header each time. The
// 2026-07-24 audit (H-1) had "fixed" this by nulling IpRateLimitOptions.RealIpHeader; the library
// falls back to ClientRateLimitOptions.RealIpHeader, whose default is the same header.
public class RateLimitIdentity561Tests
{
    private static DefaultHttpContext Request(string? realIpHeader, string connection = "203.0.113.9")
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(connection);
        if (realIpHeader != null) context.Request.Headers["X-Real-IP"] = realIpHeader;
        context.Request.Headers["X-ClientId"] = "made-up-client";
        return context;
    }

    [Fact]
    public void The_librarys_own_configuration_still_reads_the_header_after_the_July_fix()
    {
        // Exactly what both apps did: the IP options' header name forced to null, nothing else.
        var library = new RateLimitConfiguration(
            Options.Create(new IpRateLimitOptions { RealIpHeader = null }),
            Options.Create(new ClientRateLimitOptions()));
        library.RegisterResolvers();

        Assert.Equal(2, library.IpResolvers.Count);                         // the header resolver is still first
        Assert.IsType<IpHeaderResolveContributor>(library.IpResolvers[0]);
        // A made-up address is taken as the visitor ...
        Assert.Equal("198.51.100.77", library.IpResolvers[0].ResolveIp(Request("198.51.100.77")));
        // ... and a malformed one throws, which is the 500 seen live.
        Assert.Throws<FormatException>(() => library.IpResolvers[0].ResolveIp(Request("not-an-ip")));
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("198.51.100.77")]
    [InlineData("' OR 1=1 --")]
    [InlineData(null)]
    public void A_visitor_is_known_by_the_connection_and_by_nothing_they_can_type(string? header)
    {
        foreach (var configuration in new RateLimitConfiguration[]
        {
            new IPRO.Web.Infrastructure.ConnectionOnlyRateLimitConfiguration(Options.Create(new IpRateLimitOptions()), Options.Create(new ClientRateLimitOptions())),
            new IPRO.Admin.Infrastructure.ConnectionOnlyRateLimitConfiguration(Options.Create(new IpRateLimitOptions()), Options.Create(new ClientRateLimitOptions())),
        })
        {
            // The options keep the library's defaults here on purpose: the configuration must not depend on them.
            configuration.RegisterResolvers();
            configuration.RegisterResolvers();                              // registering twice adds nothing

            var only = Assert.Single(configuration.IpResolvers);
            Assert.IsType<IpConnectionResolveContributor>(only);
            Assert.Empty(configuration.ClientResolvers);                    // no X-ClientId either
            Assert.Equal("203.0.113.9", only.ResolveIp(Request(header)));
        }
    }

    [Fact]
    public void Both_apps_register_it_and_clear_both_header_names()
    {
        foreach (var (file, type) in new[]
        {
            (@"src\IPRO.Web\Program.cs", "IPRO.Web.Infrastructure.ConnectionOnlyRateLimitConfiguration"),
            (@"src\IPRO.Admin\Program.cs", "IPRO.Admin.Infrastructure.ConnectionOnlyRateLimitConfiguration"),
        })
        {
            var program = Read(file);
            Assert.Contains($"builder.Services.AddSingleton<IRateLimitConfiguration, {type}>();", program);
            Assert.DoesNotContain("AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>", program);
            Assert.Contains("builder.Services.PostConfigure<IpRateLimitOptions>(o => o.RealIpHeader = null);", program);
            Assert.Contains("builder.Services.PostConfigure<ClientRateLimitOptions>(o => { o.RealIpHeader = null; o.ClientIdHeader = null; });", program);
            // The address the limiter sees is the forwarded one, set before the limiter runs.
            Assert.True(program.IndexOf("app.UseForwardedHeaders();", StringComparison.Ordinal) < program.IndexOf("app.UseIpRateLimiting();", StringComparison.Ordinal));
        }
        // No configuration file names a header for the limiter to trust.
        foreach (var settings in new[] { @"src\IPRO.Web\appsettings.json", @"src\IPRO.Admin\appsettings.json" })
        {
            var json = Read(settings);
            Assert.DoesNotContain("RealIpHeader", json);
            Assert.DoesNotContain("ClientIdHeader", json);
            Assert.DoesNotContain("ClientWhitelist", json);
        }
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative)).Replace("\r\n", "\n");
    }
}
