using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using IPRO.Utility;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 446 (2026-09-02). The owner's first click of the morning, on his own domain: the AI Daily
// Assistant's "View" went to /WebsiteLeads?status=new and got the public site's 404. The URL-space
// rule says the portal lives ONLY under /portal on an agent host; tag-helper links comply because the
// portal route is registered first; string-built links did not, in four places. Every one of them
// worked on app.iproadvisers.com, which is why it survived until now.
public class PortalPathsTests
{
    // ---- the helper ---------------------------------------------------------------------------

    [Theory]
    [InlineData("/WebsiteLeads?status=new",          "/portal/WebsiteLeads?status=new")]
    [InlineData("/Clients/Details/74",               "/portal/Clients/Details/74")]
    [InlineData("Clients/FollowUps/9",               "/portal/Clients/FollowUps/9")]      // no leading slash
    [InlineData("/portal/WebsiteLeads?status=new",   "/portal/WebsiteLeads?status=new")]  // idempotent
    [InlineData("/PORTAL/Dashboard",                 "/PORTAL/Dashboard")]                // case-insensitive prefix check
    [InlineData("/portal",                           "/portal")]
    [InlineData("/portal?x=1",                       "/portal?x=1")]
    [InlineData("  /Newsletter/Edit/3  ",            "/portal/Newsletter/Edit/3")]        // trimmed
    public void Portal_relative_paths_get_the_prefix_exactly_once(string input, string expected)
    {
        Assert.Equal(expected, PortalPaths.Normalize(input));
        Assert.Equal(expected, PortalPaths.To(input));
    }

    [Theory]
    [InlineData("https://app.iproadvisers.com/portal/Dashboard")]
    [InlineData("http://example.com/x")]
    [InlineData("//cdn.example.com/a.js")]
    public void Absolute_and_protocol_relative_urls_are_left_alone(string input)
    {
        Assert.Equal(input, PortalPaths.Normalize(input));
    }

    [Fact]
    public void A_portal_ish_word_without_the_slash_is_not_mistaken_for_the_prefix()
    {
        // "/portalX" is a different first segment; it must still be prefixed.
        Assert.Equal("/portal/portalx/things", PortalPaths.Normalize("/portalx/things"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_is_null_for_Normalize_and_an_error_for_To(string? input)
    {
        Assert.Null(PortalPaths.Normalize(input));
        Assert.Throws<ArgumentException>(() => PortalPaths.To(input!));
    }

    // ---- the four writers and the one reader --------------------------------------------------

    [Fact]
    public void The_digest_job_builds_every_suggested_action_url_through_the_helper()
    {
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Scheduler\AiDailyDigestJob.cs"));
        // The three action types each produce a link, and none of them is a bare literal any more.
        Assert.True(Regex.Matches(src, @"actionUrl = IPRO\.Utility\.PortalPaths\.To\(").Count >= 3,
            "every actionUrl assignment must go through PortalPaths.To");
        Assert.DoesNotMatch(new Regex(@"actionUrl = \$?""/"), src);
    }

    [Fact]
    public void The_dashboard_repairs_insight_urls_already_stored_without_the_prefix()
    {
        // The job persists the URL, so rows written before this fix are still wrong in the
        // database. Read-time normalisation makes them right on the next page load.
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\DashboardController.cs"));
        Assert.Contains("SuggestedActionUrl = IPRO.Utility.PortalPaths.Normalize(dailyInsight.SuggestedActionUrl)", src);
    }

    [Theory]
    [InlineData(@"src\IPRO.Web\Controllers\ClientsController.cs",           2)]  // timeline: added + completed
    [InlineData(@"src\IPRO.Web\Controllers\MarketingCalendarController.cs", 3)]  // newsletter send + social post + drip step
    public void Timeline_and_calendar_links_are_built_through_the_helper(string file, int atLeast)
    {
        var src = File.ReadAllText(FindRepoFile(file));
        Assert.True(Regex.Matches(src, @"Url = IPRO\.Utility\.PortalPaths\.To\(").Count >= atLeast,
            $"{file}: expected at least {atLeast} Url assignments through PortalPaths.To");
        Assert.DoesNotMatch(new Regex(@"Url = \$?""/"), src);
    }

    [Fact]
    public void The_leads_return_url_is_portal_rooted_whether_supplied_or_defaulted()
    {
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\WebsiteLeadsController.cs"));
        var method = src[src.IndexOf("private static string SafeReturnUrl", StringComparison.Ordinal)..];
        method = method[..method.IndexOf(';', StringComparison.Ordinal)];
        Assert.Contains("IPRO.Utility.PortalPaths.To(returnUrl)", method);
        Assert.Contains("IPRO.Utility.PortalPaths.To(\"/WebsiteLeads\")", method);
    }

    // ---- the regression guard: no new bare portal links as strings ---------------------------

    [Fact]
    public void No_string_literal_starts_a_bare_link_into_a_shadowed_portal_controller()
    {
        // Account, Billing and the other never-shadowed prefixes are exempt by design and are not
        // in this list. Program.cs compares REQUEST paths and is excluded on purpose.
        var pattern = new Regex(
            @"(?<!PortalPaths\.(?:To|Normalize)\(\$?)\$?""/(?:WebsiteLeads|Clients|Newsletter|Campaigns|ECards|ELetters|EmailActivity|Polls|MarketingCalendar|FollowUps|Calendar|Articles|SocialPosts)(?:[/?""])");
        var offenders = Directory.EnumerateFiles(FindRepoFile(@"src\IPRO.Web\Controllers"), "*.cs")
            .Concat(Directory.EnumerateFiles(FindRepoFile(@"src\IPRO.Scheduler"), "*.cs", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(FindRepoFile(@"src\IPRO.Business"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(@"\obj\"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (file: f, line, n: i + 1)))
            .Where(x => pattern.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.file)}:{x.n}: {x.line.Trim()}")
            .ToList();
        Assert.True(offenders.Count == 0, "bare portal links as strings:\n" + string.Join("\n", offenders));
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
