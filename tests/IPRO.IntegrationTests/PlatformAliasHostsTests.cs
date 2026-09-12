using System;
using System.Collections.Generic;
using System.IO;
using IPRO.Web.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 477 (2026-09-11), the code half. At launch www.iproaccountants.com and www.iproadvisers.com
// (and both apexes) point at the new site. The app answers a request on any of those names with a
// permanent redirect to the platform -- the home, or the host's own landing page such as
// /accountants, so each domain keeps addressing its audience -- so the old names keep working over HTTPS and search
// engines move their rankings. The names come from App:AliasHosts; nothing happens until it is set,
// so this ships ahead of the DNS change and the switch itself is DNS only.
public class PlatformAliasHostsTests
{
    [Fact]
    public void Alias_hosts_come_from_configuration_and_nothing_else_is_an_alias()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:AliasHosts"] = " www.iproaccountants.com=/accountants, iproaccountants.com = accountants/ ; WWW.iproadvisers.com ,iproadvisers.com ; www.ipromortgages.com=/mortgage",
            ["App:BaseUrl"] = "https://app.iproadvisers.com"
        }).Build();

        Assert.True(PlatformAliasHosts.IsAlias(config, "www.iproaccountants.com"));
        Assert.True(PlatformAliasHosts.IsAlias(config, "IPROACCOUNTANTS.COM"));
        Assert.True(PlatformAliasHosts.IsAlias(config, "www.iproadvisers.com"));
        Assert.True(PlatformAliasHosts.IsAlias(config, "iproadvisers.com"));
        Assert.False(PlatformAliasHosts.IsAlias(config, "app.iproadvisers.com"));
        Assert.False(PlatformAliasHosts.IsAlias(config, "bahmanmotamed.247advisers.com"));
        Assert.False(PlatformAliasHosts.IsAlias(config, ""));
        // Each domain lands on its own audience's page; the platform's own name lands on the home.
        Assert.Equal("https://app.iproadvisers.com/accountants", PlatformAliasHosts.RedirectTarget(config, "www.iproaccountants.com"));
        Assert.Equal("https://app.iproadvisers.com/accountants", PlatformAliasHosts.RedirectTarget(config, "IPROACCOUNTANTS.COM"));
        Assert.Equal("https://app.iproadvisers.com/mortgage", PlatformAliasHosts.RedirectTarget(config, "www.ipromortgages.com"));
        Assert.Equal("https://app.iproadvisers.com/", PlatformAliasHosts.RedirectTarget(config, "www.iproadvisers.com"));
        Assert.Equal("https://app.iproadvisers.com/", PlatformAliasHosts.RedirectTarget(config, "iproadvisers.com"));
        Assert.Equal("https://app.iproadvisers.com/", PlatformAliasHosts.RedirectTarget(config));
    }

    [Fact]
    public void With_nothing_configured_no_host_is_an_alias()
    {
        var config = new ConfigurationBuilder().Build();
        Assert.False(PlatformAliasHosts.IsAlias(config, "www.iproadvisers.com"));
        Assert.False(PlatformAliasHosts.IsAlias(config, "iproaccountants.com"));
    }

    // The wiring pin (the rule runs before routing, /.well-known is left alone, the redirects are
    // permanent) lives in BrandDomainLandingTests since 484.

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
