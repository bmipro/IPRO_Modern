using System;
using System.Collections.Generic;
using System.IO;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// 507 (2026-09-21, launch morning). The evening the four public names went live the owner typed
// www.iproadvisers.com, watched it turn into app.iproadvisers.com, and asked for what he had asked
// for ipromortgages.com in 484: the name stays in the address bar. Asked the next morning, he chose
// the same for the bare name, and www.iproadvisers.com as the address search engines should know the
// home page by. So a name listed as "name=/" is a brand domain whose own page is the HOME page; a
// name listed bare ("name") still only forwards. And because the forwards had been going out as
// permanent redirects with no cache lifetime -- which a browser may remember indefinitely, so every
// visitor since the switch keeps landing on app. after this fix -- a forward now says how long it
// may be remembered. Every defect test observed RED on the pre-fix code.
public class HomeUnderItsOwnName507Tests
{
    private const string Live = "www.ipromortgages.com=/mortgage,ipromortgages.com=/mortgage,www.iproadvisers.com=/,iproadvisers.com=/,www.iproaccountants.com=/accountants,iproaccountants.com=/accountants";

    private static IConfiguration Config(string? aliasHosts = Live, string baseUrl = "https://app.iproadvisers.com")
    {
        var values = new Dictionary<string, string?> { ["App:BaseUrl"] = baseUrl };
        if (aliasHosts != null) values["App:AliasHosts"] = aliasHosts;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static DefaultHttpContext Request(string host, string path, string query = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        return context;
    }

    // ---- the home page under its own name ------------------------------------------------------

    [Theory]
    [InlineData("www.iproadvisers.com", "/")]
    [InlineData("iproadvisers.com", "/")]
    [InlineData("WWW.IPROADVISERS.COM", "")]
    public void A_name_whose_own_page_is_the_home_serves_it_and_stays_in_the_address_bar(string host, string path)
    {
        var context = Request(host, path);

        Assert.False(PlatformAliasHosts.TryHandle(Config(), context));   // no redirect: the pipeline carries on
        Assert.Equal("app.iproadvisers.com", context.Request.Host.Value);
        Assert.True(!context.Request.Path.HasValue || context.Request.Path == "/");
        Assert.Equal(200, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Location"));
    }

    [Fact]
    public void The_home_pages_files_come_along_under_the_name()
    {
        var logo = Request("www.iproadvisers.com", "/images/ipro-advisers-logo.png", "?v=9");

        Assert.False(PlatformAliasHosts.TryHandle(Config(), logo));
        Assert.Equal("app.iproadvisers.com", logo.Request.Host.Value);
        Assert.Equal("/images/ipro-advisers-logo.png", logo.Request.Path.Value);
        Assert.Equal("?v=9", logo.Request.QueryString.Value);
    }

    [Fact]
    public void Control_everything_else_on_the_name_still_goes_to_the_platform_with_its_path()
    {
        var register = Request("www.iproadvisers.com", "/Account/Register", "?package=2");
        Assert.True(PlatformAliasHosts.TryHandle(Config(), register));
        Assert.Equal(301, register.Response.StatusCode);
        Assert.Equal("https://app.iproadvisers.com/Account/Register?package=2", register.Response.Headers.Location.ToString());

        var market = Request("iproadvisers.com", "/accountants");
        Assert.True(PlatformAliasHosts.TryHandle(Config(), market));
        Assert.Equal("https://app.iproadvisers.com/accountants", market.Response.Headers.Location.ToString());
    }

    [Fact]
    public void Control_a_name_listed_without_a_page_of_its_own_still_only_forwards()
    {
        var config = Config("www.iproadvisers.com,iproadvisers.com");

        var root = Request("www.iproadvisers.com", "/");
        Assert.True(PlatformAliasHosts.TryHandle(config, root));
        Assert.Equal(301, root.Response.StatusCode);
        Assert.Equal("https://app.iproadvisers.com/", root.Response.Headers.Location.ToString());
    }

    [Fact]
    public void Control_the_brand_names_are_untouched()
    {
        var mortgage = Request("www.ipromortgages.com", "/");
        Assert.False(PlatformAliasHosts.TryHandle(Config(), mortgage));
        Assert.Equal("/mortgage", mortgage.Request.Path.Value);

        var accountants = Request("iproaccountants.com", "/");
        Assert.False(PlatformAliasHosts.TryHandle(Config(), accountants));
        Assert.Equal("/accountants", accountants.Request.Path.Value);
    }

    // ---- the address the home page is known by -------------------------------------------------

    [Fact]
    public void The_home_page_is_known_by_the_first_name_that_serves_it()
    {
        Assert.Equal("https://www.iproadvisers.com", PlatformAliasHosts.HomeBase(Config()));
        Assert.Equal("https://iproadvisers.com", PlatformAliasHosts.HomeBase(Config("www.ipromortgages.com=/mortgage,iproadvisers.com=/,www.iproadvisers.com=/")));
    }

    [Fact]
    public void Control_with_no_such_name_the_home_page_is_known_by_the_platform_address()
    {
        Assert.Equal("https://app.iproadvisers.com", PlatformAliasHosts.HomeBase(Config("www.iproadvisers.com,iproadvisers.com,www.ipromortgages.com=/mortgage")));
        Assert.Equal("https://app.iproadvisers.com", PlatformAliasHosts.HomeBase(Config(aliasHosts: null)));
        Assert.Equal("http://localhost:5100", PlatformAliasHosts.HomeBase(Config(aliasHosts: null, baseUrl: "http://localhost:5100/")));
    }

    [Fact]
    public void The_home_page_tells_search_engines_that_address()
    {
        var home = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\Index.cshtml"));

        Assert.Contains("PlatformAliasHosts.HomeBase(HomeSeoConfig)", home);
        Assert.Contains("<link rel=\"canonical\" href=\"@(seoOrigin)/\"/>", home);
        Assert.Contains("<meta property=\"og:url\" content=\"@(seoOrigin)/\"/>", home);
    }

    [Fact]
    public void The_landing_pages_send_visitors_to_the_home_pages_sections_at_that_address()
    {
        var footer = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\_LandingFooter.cshtml"));
        var pricing = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\_LandingPricing.cshtml"));

        Assert.Contains("PlatformAliasHosts.HomeBase(", footer);
        Assert.Contains("PlatformAliasHosts.HomeBase(", pricing);
        foreach (var anchor in new[] { "#i2-platform", "#i2-start", "#i2-trust", "#i2-contact" })
            Assert.Contains("href=\"@homeBase/" + anchor + "\"", footer);
        Assert.Contains("href=\"@homeBase/#i2-pricing\"", pricing);
    }

    // ---- a forward says how long it may be remembered -------------------------------------------

    [Theory]
    [InlineData("www.iproadvisers.com,iproadvisers.com", "www.iproadvisers.com", "/")]
    [InlineData(Live, "www.iproadvisers.com", "/Account/Login")]
    [InlineData(Live, "www.ipromortgages.com", "/terms")]
    public void A_forward_is_not_remembered_for_ever(string aliasHosts, string host, string path)
    {
        var context = Request(host, path);

        Assert.True(PlatformAliasHosts.TryHandle(Config(aliasHosts), context));
        Assert.Equal(301, context.Response.StatusCode);
        Assert.Equal("public, max-age=3600", context.Response.Headers.CacheControl.ToString());
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
