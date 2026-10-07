using System.Collections.Generic;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// 555 (2026-10-07). crm.to, a short name the owner has held for years, was pointed back at the
// platform as a name that only forwards. It landed on app.iproadvisers.com -- the same home page, under
// an address nobody is given. The owner: "make it land on www.iproadvisers.com". A forward-only name
// now goes to the address the home page is known by.
public class ForwardOnlyNames555Tests
{
    private static IConfiguration Live() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["App:AliasHosts"] = "www.ipromortgages.com=/mortgage,www.iproadvisers.com=/,iproadvisers.com=/,www.iproaccountants.com=/accountants,www.crm.to,crm.to",
        ["App:BaseUrl"] = "https://app.iproadvisers.com"
    }).Build();

    [Fact]
    public void A_name_that_only_forwards_lands_on_the_home_pages_own_address()
    {
        var config = Live();
        Assert.Equal("https://www.iproadvisers.com/", PlatformAliasHosts.RedirectTarget(config, "crm.to"));
        Assert.Equal("https://www.iproadvisers.com/", PlatformAliasHosts.RedirectTarget(config, "WWW.CRM.TO"));
        // A campaign link keeps its parameters.
        Assert.Equal("https://www.iproadvisers.com/?utm_source=card",
            PlatformAliasHosts.RedirectTarget(config, "crm.to", new PathString("/"), new QueryString("?utm_source=card")));
        // Anything deeper still goes to the platform, where accounts and the rest of the site live.
        Assert.Equal("https://app.iproadvisers.com/Account/Register?type=x",
            PlatformAliasHosts.RedirectTarget(config, "crm.to", new PathString("/Account/Register"), new QueryString("?type=x")));
        // A brand name with a page of its own is unchanged.
        Assert.Equal("https://app.iproadvisers.com/mortgage", PlatformAliasHosts.RedirectTarget(config, "www.ipromortgages.com"));
    }

    [Fact]
    public void The_whole_request_is_answered_that_way()
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("crm.to");
        context.Request.Path = "/";
        Assert.True(PlatformAliasHosts.TryHandle(Live(), context));
        Assert.Equal(StatusCodes.Status301MovedPermanently, context.Response.StatusCode);
        Assert.Equal("https://www.iproadvisers.com/", context.Response.Headers.Location.ToString());

        // An old-style page address on a forward-only name goes to the same front page.
        var legacy = new DefaultHttpContext();
        legacy.Request.Host = new HostString("www.crm.to");
        legacy.Request.Path = "/index.php";
        Assert.True(PlatformAliasHosts.TryHandle(Live(), legacy));
        Assert.Equal("https://www.iproadvisers.com/", legacy.Response.Headers.Location.ToString());
    }

    [Fact]
    public void Without_a_name_that_serves_the_home_page_it_is_the_platform_as_before()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:AliasHosts"] = "crm.to",
            ["App:BaseUrl"] = "https://app.iproadvisers.com"
        }).Build();
        Assert.Equal("https://app.iproadvisers.com/", PlatformAliasHosts.RedirectTarget(config, "crm.to"));
    }
}
