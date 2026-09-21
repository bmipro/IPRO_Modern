using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// 509 (2026-09-21, launch day). The owner had the three public names reviewed independently before
// his LinkedIn launch and asked which findings to act on. Checked against the live site and the code,
// five were real and cheap: the accountants and mortgage pages stay on their own names (his 484
// decision) but told search engines their address was app.iproadvisers.com/...; old indexed pages
// (/websites_for_advisers.html) answered 404; there was no robots.txt or sitemap.xml on any public
// name; the two vertical pages had no share image or Twitter card (and the home page's was the
// 2583x629 logo, which LinkedIn crops); HEAD answered 405; and /mortgages duplicated /mortgage. One
// more was already fixed by 507 that morning (marketing parameters lost on the way to app.), with a
// gap left in the code for names that only forward. Every defect test observed RED on the pre-fix code.
public class PublicFrontDoor509Tests
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

    // ---- 1. each public page is known by its own brand address -----------------------------------

    [Theory]
    [InlineData("/accountants", "https://www.iproaccountants.com/")]
    [InlineData("/mortgage", "https://www.ipromortgages.com/")]
    [InlineData("accountants/", "https://www.iproaccountants.com/")]
    public void A_page_a_brand_name_serves_is_known_by_that_name(string path, string expected)
    {
        Assert.Equal(expected, PlatformAliasHosts.PageUrl(Config(), path));
    }

    [Fact]
    public void Control_the_home_page_and_a_page_no_name_serves_keep_their_addresses()
    {
        Assert.Equal("https://app.iproadvisers.com/terms", PlatformAliasHosts.PageUrl(Config(), "/terms"));
        Assert.Equal("https://app.iproadvisers.com/accountants", PlatformAliasHosts.PageUrl(Config("www.ipromortgages.com=/mortgage"), "/accountants"));
        Assert.Equal("https://app.iproadvisers.com/mortgage", PlatformAliasHosts.PageUrl(Config(aliasHosts: null), "/mortgage"));
        Assert.Equal("https://www.iproadvisers.com", PlatformAliasHosts.HomeBase(Config()));
    }

    [Theory]
    [InlineData(@"src\IPRO.Web\Views\Home\Accountants.cshtml", "/accountants")]
    [InlineData(@"src\IPRO.Web\Views\Home\Mortgage.cshtml", "/mortgage")]
    public void The_vertical_pages_tell_search_engines_their_brand_address(string view, string path)
    {
        var source = File.ReadAllText(FindRepoFile(view));

        Assert.Contains("PlatformAliasHosts.PageUrl(", source);
        Assert.Contains("\"" + path + "\"", source);
        Assert.Contains("<link rel=\"canonical\" href=\"@pageUrl\">", source);
        Assert.Contains("<meta property=\"og:url\" content=\"@pageUrl\">", source);
        Assert.DoesNotContain("href=\"@seoOrigin" + path + "\"", source);
    }

    // ---- 2. a name that only forwards keeps the visitor's parameters ------------------------------

    [Fact]
    public void A_forward_from_the_root_keeps_the_marketing_parameters()
    {
        var context = Request("www.iproadvisers.com", "/", "?utm_source=linkedin&utm_campaign=launch");

        Assert.True(PlatformAliasHosts.TryHandle(Config("www.iproadvisers.com,iproadvisers.com"), context));
        Assert.Equal("https://app.iproadvisers.com/?utm_source=linkedin&utm_campaign=launch", context.Response.Headers.Location.ToString());
    }

    // ---- 3. old indexed pages ----------------------------------------------------------------------

    [Theory]
    [InlineData("www.iproaccountants.com", "/websites_for_advisers.html", "https://www.iproaccountants.com/")]
    [InlineData("iproaccountants.com", "/Services/Tax.HTM", "https://iproaccountants.com/")]
    [InlineData("www.iproadvisers.com", "/index.php", "https://www.iproadvisers.com/")]
    [InlineData("www.ipromortgages.com", "/old/rates.aspx", "https://www.ipromortgages.com/")]
    public void An_old_style_page_address_goes_to_the_names_own_front_page(string host, string path, string expected)
    {
        var context = Request(host, path, "?ref=google");

        Assert.True(PlatformAliasHosts.TryHandle(Config(), context));
        Assert.Equal(301, context.Response.StatusCode);
        Assert.Equal(expected, context.Response.Headers.Location.ToString());
        Assert.Equal(PlatformAliasHosts.RedirectLifetime, context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public void An_old_style_page_on_a_name_that_only_forwards_goes_to_the_platform_home()
    {
        var context = Request("www.iproadvisers.com", "/websites_for_advisers.html");

        Assert.True(PlatformAliasHosts.TryHandle(Config("www.iproadvisers.com,iproadvisers.com"), context));
        Assert.Equal("https://app.iproadvisers.com/", context.Response.Headers.Location.ToString());
    }

    [Theory]
    [InlineData("/css/landing-page.css")]
    [InlineData("/images/share/ipro-accountants.png")]
    [InlineData("/robots.txt")]
    [InlineData("/sitemap.xml")]
    public void Control_the_pages_own_files_are_still_served_under_the_name(string path)
    {
        var context = Request("www.iproaccountants.com", path);

        Assert.False(PlatformAliasHosts.TryHandle(Config(), context));
        Assert.Equal("app.iproadvisers.com", context.Request.Host.Value);
        Assert.Equal(path, context.Request.Path.Value);
    }

    // ---- 4. robots.txt and sitemap.xml ---------------------------------------------------------------

    [Fact]
    public void A_request_served_under_a_brand_name_remembers_the_name_it_came_in_on()
    {
        var context = Request("www.iproaccountants.com", "/robots.txt");

        Assert.False(PlatformAliasHosts.TryHandle(Config(), context));
        Assert.Equal("www.iproaccountants.com", PlatformAliasHosts.PublicHost(context));

        var platform = Request("app.iproadvisers.com", "/robots.txt");
        Assert.False(PlatformAliasHosts.TryHandle(Config(), platform));
        Assert.Equal("app.iproadvisers.com", PlatformAliasHosts.PublicHost(platform));
    }

    [Fact]
    public void The_platform_and_its_names_answer_robots_and_sitemap_and_no_other_host_does()
    {
        Assert.True(PlatformSeoFiles.IsPlatformOrAlias(Config(), "app.iproadvisers.com"));
        Assert.True(PlatformSeoFiles.IsPlatformOrAlias(Config(), "WWW.IPROACCOUNTANTS.COM"));
        Assert.False(PlatformSeoFiles.IsPlatformOrAlias(Config(), "bahmanmotamed.247advisers.com"));
        Assert.False(PlatformSeoFiles.IsPlatformOrAlias(Config(), ""));

        Assert.Equal("User-agent: *\nAllow: /\nDisallow: /portal/\nSitemap: https://www.iproaccountants.com/sitemap.xml\n",
            PlatformSeoFiles.Robots("www.iproaccountants.com"));
    }

    [Fact]
    public void The_sitemap_lists_each_public_page_once_at_the_address_it_is_known_by()
    {
        var xml = PlatformSeoFiles.Sitemap(Config());

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", xml);
        foreach (var loc in new[]
        {
            "https://www.iproadvisers.com/", "https://www.iproaccountants.com/", "https://www.ipromortgages.com/",
            "https://app.iproadvisers.com/terms", "https://app.iproadvisers.com/privacy"
        })
        {
            Assert.Contains("<loc>" + loc + "</loc>", xml);
        }
        Assert.DoesNotContain("app.iproadvisers.com/accountants", xml);
        Assert.DoesNotContain("<loc>https://iproadvisers.com/</loc>", xml);
        Assert.Equal(5, xml.Split("<url>").Length - 1);
    }

    [Fact]
    public void The_public_site_controller_hands_the_platforms_names_to_those_files()
    {
        var controller = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\PublicWebsiteController.cs"));

        Assert.Contains("PlatformSeoFiles.Robots(", controller);
        Assert.Contains("PlatformSeoFiles.Sitemap(", controller);
        Assert.Contains("PlatformSeoFiles.IsPlatformOrAlias(", controller);
    }

    // ---- 5. share cards -----------------------------------------------------------------------------

    [Theory]
    [InlineData(@"src\IPRO.Web\Views\Home\Index.cshtml", "ipro-advisers.png")]
    [InlineData(@"src\IPRO.Web\Views\Home\Accountants.cshtml", "ipro-accountants.png")]
    [InlineData(@"src\IPRO.Web\Views\Home\Mortgage.cshtml", "ipro-mortgages.png")]
    public void Each_public_page_has_a_share_card_of_the_size_LinkedIn_asks_for(string view, string image)
    {
        var source = File.ReadAllText(FindRepoFile(view));
        Assert.Contains("/images/share/" + image, source);
        Assert.Contains("og:image:width", source);
        Assert.Contains("summary_large_image", source);

        var bytes = File.ReadAllBytes(FindRepoFile(@"src\IPRO.Web\wwwroot\images\share\" + image));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
        Assert.Equal(1200, (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19]);
        Assert.Equal(627, (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23]);
        Assert.True(bytes.Length < 1_000_000, "a share card should stay well under a megabyte");
    }

    // ---- 6. HEAD ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/", true)]
    [InlineData("", true)]
    [InlineData("/accountants", true)]
    [InlineData("/mortgage", true)]
    [InlineData("/terms", true)]
    [InlineData("/privacy", true)]
    [InlineData("/health/version", true)]
    [InlineData("/health", true)]
    [InlineData("/robots.txt", true)]
    [InlineData("/sitemap.xml", true)]
    [InlineData("/Account/Register", true)]
    [InlineData("/Account/Login", true)]
    // Never the addresses where a GET does something: a mail scanner's HEAD must not count as an
    // open, a click, a vote or an unsubscribe.
    [InlineData("/t/o/abc", false)]
    [InlineData("/Poll/Vote", false)]
    [InlineData("/unsubscribe", false)]
    [InlineData("/portal/Clients", false)]
    [InlineData("/billing/webhook", false)]
    [InlineData("/Account/Logout", false)]
    public void HEAD_is_answered_for_the_public_pages_and_nothing_with_side_effects(string path, bool answered)
    {
        Assert.Equal(answered, HeadRequests.IsAnswered(new PathString(path)));
    }

    [Fact]
    public async Task A_HEAD_for_a_public_page_runs_as_a_GET_with_nowhere_to_write_and_is_put_back_afterwards()
    {
        var context = Request("www.iproaccountants.com", "/accountants");
        context.Request.Method = "HEAD";
        var realBody = new MemoryStream();
        context.Response.Body = realBody;
        string? methodSeenByThePage = null;

        await HeadRequests.AnswerAsync(context, async () =>
        {
            methodSeenByThePage = context.Request.Method;
            await context.Response.WriteAsync("<html>the whole page</html>");
        });

        Assert.Equal("GET", methodSeenByThePage);          // so an [HttpGet] action matches instead of 405
        Assert.Equal(0, realBody.Length);                   // and no body is produced for a HEAD
        Assert.Same(realBody, context.Response.Body);
        Assert.Equal("HEAD", context.Request.Method);       // logged as what it was
    }

    [Theory]
    [InlineData("HEAD", "/Poll/Vote")]
    [InlineData("GET", "/accountants")]
    [InlineData("POST", "/Account/Register")]
    public async Task Control_anything_else_passes_through_untouched(string method, string path)
    {
        var context = Request("app.iproadvisers.com", path);
        context.Request.Method = method;
        var realBody = new MemoryStream();
        context.Response.Body = realBody;
        string? seen = null;

        await HeadRequests.AnswerAsync(context, async () =>
        {
            seen = context.Request.Method;
            await context.Response.WriteAsync("body");
        });

        Assert.Equal(method, seen);
        Assert.Equal(4, realBody.Length);
    }

    [Fact]
    public void The_web_app_answers_HEAD_before_routing()
    {
        var program = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Program.cs"));
        var head = program.IndexOf("HeadRequests.AnswerAsync(", StringComparison.Ordinal);
        var routing = program.IndexOf("app.UseRouting()", StringComparison.Ordinal);

        Assert.True(head >= 0, "HEAD is not wired");
        Assert.True(routing < 0 || head < routing, "HEAD must be turned into GET before routing");
    }

    // ---- 7. one address for the mortgage page ------------------------------------------------------------

    [Fact]
    public void The_plural_mortgage_address_forwards_to_the_singular_one()
    {
        var controller = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\HomeController.cs"));

        Assert.Contains("RedirectPermanent(\"/mortgage\" + Request.QueryString.Value)", controller);
        Assert.Equal(1, controller.Split("[HttpGet(\"/mortgages\")]").Length - 1);
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
