using System;
using System.IO;
using System.Threading.Tasks;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace IPRO.IntegrationTests;

// 649 (found 2026-11-08, tracing the slowdown of 2026-11-04). A scanner's "not found" pages on a customer
// site each built the site's own 404 from the database; 470 at once slowed every page for five minutes.
// Scanners' unmistakable addresses are now answered before the URL-space rule and any database work.
public class ScannerProbes649Tests
{
    // Taken from the 30 days of customer-site 404s before 2026-11-08 (Application Insights), and the usual others.
    [Theory]
    [InlineData("/.git/config")]
    [InlineData("/.git/HEAD")]
    [InlineData("/.env")]
    [InlineData("/.aws/credentials")]
    [InlineData("/.DS_Store")]
    [InlineData("/.htaccess")]
    [InlineData("/app/.env")]
    [InlineData("/wp-login.php")]
    [InlineData("/WP-LOGIN.PHP")]
    [InlineData("/wp-admin/")]
    [InlineData("/wp-admin/admin-ajax.php")]
    [InlineData("/wp-content/plugins/hellopress/wp_filemanager.php")]
    [InlineData("/wp-includes/js/jquery.js")]
    [InlineData("/wp-json/gravitysmtp/v1/tests/mock-data")]
    [InlineData("/wp-config.php")]
    [InlineData("/wordpress/wp-login.php")]
    [InlineData("/blog/wp-admin/setup-config.php")]
    [InlineData("/wp_blog_footer.php")]
    [InlineData("/xmlrpc.php")]
    [InlineData("/cgi-bin/luci")]
    [InlineData("/phpmyadmin/index.php")]
    [InlineData("/proc/self/environ")]
    [InlineData("/etc/passwd")]
    [InlineData("/vendor/phpunit/phpunit/src/Util/PHP/eval-stdin.php")]
    public void A_scanners_address_is_a_probe(string path) =>
        Assert.True(ScannerProbes.IsProbe(new PathString(path)), path);

    // Real pages, words that could be one, old-style addresses of a previous website, and iPro's own addresses.
    [Theory]
    [InlineData("/")]
    [InlineData("/terms")]
    [InlineData("/privacy")]
    [InlineData("/login")]
    [InlineData("/admin")]
    [InlineData("/dashboard")]
    [InlineData("/settings")]
    [InlineData("/wordpress")]
    [InlineData("/wp")]
    [InlineData("/wpforms-guide")]
    [InlineData("/services.php")]
    [InlineData("/about-us.html")]
    [InlineData("/index.php")]
    [InlineData("/admin.php")]
    [InlineData("/.well-known/traffic-advice")]
    [InlineData("/.well-known/acme-challenge/abc")]
    [InlineData("/.well-known")]
    [InlineData("/portal/Dashboard")]
    [InlineData("/pub/register.aspx")]
    [InlineData("/hangfire")]
    [InlineData("/health")]
    [InlineData("/css/site.css")]
    [InlineData("/blog/my-first.post")]
    [InlineData("/etcetera")]
    [InlineData("/procedures")]
    public void Everything_else_goes_on_as_before(string path) =>
        Assert.False(ScannerProbes.IsProbe(new PathString(path)), path);

    [Fact]
    public void No_address_is_no_probe()
    {
        Assert.False(ScannerProbes.IsProbe(PathString.Empty));
        Assert.False(ScannerProbes.IsProbe(new PathString("/")));
    }

    [Fact]
    public async Task The_answer_is_a_bare_404_with_a_body_of_its_own()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        await ScannerProbes.AnswerAsync(context);
        Assert.Equal(404, context.Response.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        context.Response.Body.Position = 0;
        Assert.Equal("Not found.", new StreamReader(context.Response.Body).ReadToEnd());

        var head = new DefaultHttpContext();
        head.Request.Method = "HEAD";
        head.Response.Body = new MemoryStream();
        await ScannerProbes.AnswerAsync(head);
        Assert.Equal(404, head.Response.StatusCode);
        Assert.Equal(0, head.Response.Body.Length);
    }

    [Fact]
    public void It_runs_after_static_files_and_the_address_limit_and_before_the_url_space_rule()
    {
        var program = Read(@"src\IPRO.Web\Program.cs");
        var limit = program.IndexOf("app.UseIpRateLimiting();", StringComparison.Ordinal);
        var staticFiles = program.IndexOf("app.UseStaticFiles();", StringComparison.Ordinal);
        var probes = program.IndexOf("if (IPRO.Web.Infrastructure.ScannerProbes.IsProbe(context.Request.Path))", StringComparison.Ordinal);
        var urlSpace = program.IndexOf("// THE URL-SPACE RULE.", StringComparison.Ordinal);
        var routing = program.IndexOf("app.UseRouting();", StringComparison.Ordinal);
        Assert.True(limit > 0 && staticFiles > limit && probes > staticFiles && urlSpace > probes && routing > urlSpace,
            "ScannerProbes must sit after the address limit and static files, and before the URL-space rule and routing");
        Assert.Contains("        await IPRO.Web.Infrastructure.ScannerProbes.AnswerAsync(context);\n        return;\n", program);
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative)).Replace("\r\n", "\n");
    }
}
