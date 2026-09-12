using System;
using System.Collections.Generic;
using System.IO;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 484 (2026-09-12). With ipromortgages.com live (483) the owner saw it redirect to
// app.iproadvisers.com/mortgage and asked for the opposite: the brand name stays in the address bar
// and shows the mortgage page. So an alias host that maps to a landing page now SERVES that page
// under its own name -- the request is re-addressed to the platform host and the landing path and
// carries on down the pipeline -- and the files that page loads and the live preview frame come along
// the same way. Everything else on the brand name (Register, Sign in, the preview pages, Terms,
// Privacy, an old deep link) still goes to the platform, permanently, with its path and query kept,
// because accounts and the rest of the site live there. The iproadvisers.com names (no landing path)
// keep redirecting to the platform home: they are the same brand as app.iproadvisers.com. Links from
// the landing pages to sections of the platform home are absolute now, since "/#i2-pricing" on a
// brand name would be the brand's own root.
public class BrandDomainLandingTests
{
    private static IConfiguration Config(string baseUrl = "https://app.iproadvisers.com") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:AliasHosts"] = "www.iproadvisers.com,iproadvisers.com,www.iproaccountants.com=/accountants,www.ipromortgages.com=/mortgage,ipromortgages.com=/mortgage",
            ["App:BaseUrl"] = baseUrl
        }).Build();

    private static DefaultHttpContext Request(string host, string path, string query = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        return context;
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("", true)]
    [InlineData("/css/landing-page.css", true)]
    [InlineData("/images/landing/icon-website.svg", true)]
    [InlineData("/images/ipro-advisers-logo.png", true)]
    [InlineData("/favicon.ico", true)]
    [InlineData("/Preview/Site", true)]
    [InlineData("/Account/Register", false)]
    [InlineData("/Account/Login", false)]
    [InlineData("/Preview", false)]
    [InlineData("/Preview/Show", false)]
    [InlineData("/terms", false)]
    [InlineData("/mortgage", false)]
    [InlineData("/about-us", false)]
    public void What_a_brand_domain_serves_under_its_own_name(string path, bool inPlace)
    {
        Assert.Equal(inPlace, PlatformAliasHosts.ServesInPlace(new PathString(path)));
    }

    [Fact]
    public void The_brand_domains_root_is_the_landing_page_served_as_the_platform()
    {
        var context = Request("www.ipromortgages.com", "/");
        Assert.False(PlatformAliasHosts.TryHandle(Config(), context));
        Assert.Equal("app.iproadvisers.com", context.Request.Host.Value);
        Assert.Equal("/mortgage", context.Request.Path.Value);
        Assert.Equal(200, context.Response.StatusCode);

        var apex = Request("ipromortgages.com", "");
        Assert.False(PlatformAliasHosts.TryHandle(Config(), apex));
        Assert.Equal("/mortgage", apex.Request.Path.Value);
    }

    [Fact]
    public void The_pages_files_and_the_live_preview_frame_come_along_under_the_brand_name()
    {
        var css = Request("www.ipromortgages.com", "/css/landing-page.css", "?v=abc");
        Assert.False(PlatformAliasHosts.TryHandle(Config(), css));
        Assert.Equal("app.iproadvisers.com", css.Request.Host.Value);
        Assert.Equal("/css/landing-page.css", css.Request.Path.Value);
        Assert.Equal("?v=abc", css.Request.QueryString.Value);

        var frame = Request("www.ipromortgages.com", "/Preview/Site", "?businessType=Mortgage");
        Assert.False(PlatformAliasHosts.TryHandle(Config(), frame));
        Assert.Equal("app.iproadvisers.com", frame.Request.Host.Value);
        Assert.Equal("/Preview/Site", frame.Request.Path.Value);
        Assert.Equal("?businessType=Mortgage", frame.Request.QueryString.Value);
    }

    [Fact]
    public void Everything_else_on_the_brand_name_goes_to_the_platform_with_its_path_and_query()
    {
        var register = Request("www.ipromortgages.com", "/Account/Register", "?businessType=Mortgage");
        Assert.True(PlatformAliasHosts.TryHandle(Config(), register));
        Assert.Equal(301, register.Response.StatusCode);
        Assert.Equal("https://app.iproadvisers.com/Account/Register?businessType=Mortgage", register.Response.Headers.Location.ToString());

        var terms = Request("ipromortgages.com", "/terms");
        Assert.True(PlatformAliasHosts.TryHandle(Config(), terms));
        Assert.Equal("https://app.iproadvisers.com/terms", terms.Response.Headers.Location.ToString());
    }

    [Fact]
    public void The_iproadvisers_names_keep_redirecting_to_the_platform_home()
    {
        var root = Request("www.iproadvisers.com", "/");
        Assert.True(PlatformAliasHosts.TryHandle(Config(), root));
        Assert.Equal(301, root.Response.StatusCode);
        Assert.Equal("https://app.iproadvisers.com/", root.Response.Headers.Location.ToString());

        var deep = Request("iproadvisers.com", "/accountants", "?x=1");
        Assert.True(PlatformAliasHosts.TryHandle(Config(), deep));
        Assert.Equal("https://app.iproadvisers.com/accountants?x=1", deep.Response.Headers.Location.ToString());

        // No landing page of its own, so nothing is served in place, not even a file.
        var file = Request("www.iproadvisers.com", "/css/landing-page.css");
        Assert.True(PlatformAliasHosts.TryHandle(Config(), file));
        Assert.Equal("https://app.iproadvisers.com/css/landing-page.css", file.Response.Headers.Location.ToString());
    }

    [Fact]
    public void The_platform_host_agent_sites_and_well_known_pass_untouched()
    {
        foreach (var (host, path) in new[] { ("app.iproadvisers.com", "/mortgage"), ("bahmanmotamed.247advisers.com", "/"), ("www.ipromortgages.com", "/.well-known/acme-challenge/token") })
        {
            var context = Request(host, path);
            Assert.False(PlatformAliasHosts.TryHandle(Config(), context));
            Assert.Equal(host, context.Request.Host.Value);
            Assert.Equal(path, context.Request.Path.Value);
            Assert.Equal(200, context.Response.StatusCode);
        }
    }

    [Fact]
    public void A_local_platform_base_keeps_its_port()
    {
        var context = Request("www.ipromortgages.com", "/");
        Assert.False(PlatformAliasHosts.TryHandle(Config("http://localhost:5100"), context));
        Assert.Equal("localhost:5100", context.Request.Host.Value);
        Assert.Equal("/mortgage", context.Request.Path.Value);
    }

    [Fact]
    public void The_web_app_hands_every_request_to_the_alias_rule_before_routing()
    {
        var program = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Program.cs"));
        var call = program.IndexOf("PlatformAliasHosts.TryHandle(", StringComparison.Ordinal);
        var routing = program.IndexOf("app.UseRouting()", StringComparison.Ordinal);
        Assert.True(call >= 0, "the alias rule is not wired");
        Assert.True(routing < 0 || call < routing, "the alias rule must run before routing");

        var source = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Infrastructure\PlatformAliasHosts.cs"));
        Assert.Contains("/.well-known", source);      // certificate validation on the old names keeps working
        Assert.Contains("permanent: true", source);   // the redirects stay permanent
    }

    [Fact]
    public void The_landing_pages_link_the_platform_homes_sections_absolutely()
    {
        var footer = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\_LandingFooter.cshtml"));
        foreach (var anchor in new[] { "#i2-platform", "#i2-start", "#i2-trust", "#i2-contact" })
            Assert.Contains("href=\"@platformBase/" + anchor + "\"", footer);
        Assert.DoesNotContain("href=\"/#i2-", footer);

        var pricing = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\_LandingPricing.cshtml"));
        Assert.Contains("href=\"@platformBase/#i2-pricing\"", pricing);
        Assert.DoesNotContain("href=\"/#i2-", pricing);
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
