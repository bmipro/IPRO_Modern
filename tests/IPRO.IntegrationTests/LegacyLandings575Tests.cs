using System;
using System.Collections.Generic;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// 575 (2026-10-13). Google Search Console listed nine addresses of iproadvisers.com as "Not found".
// All nine are addresses of the site that lived on the name before the platform did (a WordPress
// site, and a Joomla one before it). Read one by one: three have a successor today and now go to it;
// one (the Joomla address) already went to the front page; five are old blog, category and
// portfolio pages with nothing to point at, and "not found" stays the honest answer for them.
// Before this change the three ended at a 404 on the platform host after one hop.
public class LegacyLandings575Tests
{
    private const string Live = "www.ipromortgages.com=/mortgage,ipromortgages.com=/mortgage,www.iproadvisers.com=/,iproadvisers.com=/,www.iproaccountants.com=/accountants,iproaccountants.com=/accountants";

    private static IConfiguration Config(string? aliasHosts = Live)
    {
        var values = new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.iproadvisers.com" };
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

    [Theory]
    // The addresses as Search Console reported them.
    [InlineData("iproadvisers.com", "/insurance-professionals/", "", "https://www.iproadvisers.com/")]
    [InlineData("iproadvisers.com", "/mortgage-professionals/", "", "https://www.ipromortgages.com/")]
    [InlineData("www.iproadvisers.com", "/2016/02/25/ipro-advisers-inc-right-online-partner-financial-advisors-brokers/", "", "https://www.iproadvisers.com/")]
    [InlineData("iproadvisers.com", "/ipro-advisers-inc-right-online-partner-financial-advisors-brokers/", "?replytocom=82", "https://www.iproadvisers.com/")]
    // The same pages as people type and link them.
    [InlineData("www.iproadvisers.com", "/insurance-professionals", "", "https://www.iproadvisers.com/")]
    [InlineData("www.iproadvisers.com", "/Insurance-Professionals/", "?utm_source=old", "https://www.iproadvisers.com/")]
    [InlineData("www.iproadvisers.com", "/mortgage-professionals", "", "https://www.ipromortgages.com/")]
    public void An_old_address_with_a_successor_goes_to_it_in_one_step(string host, string path, string query, string expected)
    {
        var context = Request(host, path, query);

        Assert.True(PlatformAliasHosts.TryHandle(Config(), context));

        Assert.Equal(301, context.Response.StatusCode);
        Assert.Equal(expected, context.Response.Headers.Location.ToString());
        // Permanent, but not for ever (507): the lifetime every forward from these names carries.
        Assert.Equal(PlatformAliasHosts.RedirectLifetime, context.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    // The five with nothing to point at: forwarded to the platform as before, which answers "not found".
    [InlineData("www.iproadvisers.com", "/2014/02/18/tech-savvy-online-marketing-techniques-financial-websites/")]
    [InlineData("iproadvisers.com", "/portfolio-posts/financial-agency/")]
    [InlineData("iproadvisers.com", "/category/web-designs/")]
    [InlineData("www.iproadvisers.com", "/blog-roll")]
    // And a known name deeper down, or under something that is not a date, is not that page.
    [InlineData("www.iproadvisers.com", "/category/insurance-professionals/")]
    [InlineData("www.iproadvisers.com", "/insurance-professionals/team/")]
    [InlineData("www.iproadvisers.com", "/20/02/25/mortgage-professionals/")]
    [InlineData("www.iproadvisers.com", "/about/02/25/mortgage-professionals/")]
    public void An_old_address_without_one_is_still_passed_on_and_not_sent_to_the_front_page(string host, string path)
    {
        Assert.Null(PlatformAliasHosts.LegacyLanding(path));
        var context = Request(host, path);

        Assert.True(PlatformAliasHosts.TryHandle(Config(), context));

        Assert.Equal("https://app.iproadvisers.com" + path, context.Response.Headers.Location.ToString());
    }

    [Fact]
    public void Only_the_platform_s_own_names_are_touched_and_a_setup_without_the_mortgage_name_still_lands()
    {
        // An adviser's own site may well have a page called "insurance-professionals": it is theirs.
        foreach (var host in new[] { "www.some-adviser.example", "someone.247advisers.com", "app.iproadvisers.com" })
        {
            var context = Request(host, "/insurance-professionals/");
            Assert.False(PlatformAliasHosts.TryHandle(Config(), context));
            Assert.Equal(200, context.Response.StatusCode);
            Assert.Equal("/insurance-professionals/", context.Request.Path.Value);
        }

        // Certificate validation on the old names is never answered with a redirect.
        Assert.False(PlatformAliasHosts.TryHandle(Config(), Request("iproadvisers.com", "/.well-known/acme-challenge/insurance-professionals")));

        // With no brand name for the mortgage page, the old address lands on the platform's page for it.
        var plain = Request("www.iproadvisers.com", "/mortgage-professionals/");
        Assert.True(PlatformAliasHosts.TryHandle(Config("www.iproadvisers.com=/,iproadvisers.com=/"), plain));
        Assert.Equal("https://app.iproadvisers.com/mortgage", plain.Response.Headers.Location.ToString());

        Assert.Null(PlatformAliasHosts.LegacyLanding(PathString.Empty));
        Assert.Null(PlatformAliasHosts.LegacyLanding("/"));
        Assert.Equal("/", PlatformAliasHosts.LegacyLanding("/INSURANCE-PROFESSIONALS"));
    }
}
