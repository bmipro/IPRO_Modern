using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// 512 (2026-09-22). The owner asked on launch day whether SuperAdmin's analytics cover site visits and
// their origin. They did not: visit analytics existed per ADVISER site only, and the platform's own
// front door -- the home page, the accountants and mortgage pages, the registration page -- was
// counted nowhere. Approved the same day: the same first-party, cookie-less counting for the
// platform's public pages, plus the campaign tags a link carries, a SuperAdmin report by day, page,
// name and origin, and sign-ups by origin. Every defect test observed RED on the pre-fix code.
public class PlatformVisitors512Tests
{
    private static readonly DateTime Now = new(2026, 9, 22, 18, 30, 0, DateTimeKind.Utc);
    private const string Chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Safari/537.36";
    private const string Live = "www.ipromortgages.com=/mortgage,ipromortgages.com=/mortgage,www.iproadvisers.com=/,iproadvisers.com=/,www.iproaccountants.com=/accountants,iproaccountants.com=/accountants";

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["App:AliasHosts"] = Live,
        ["App:BaseUrl"] = "https://app.iproadvisers.com"
    }).Build();

    // ---- what one view records --------------------------------------------------------------------

    [Fact]
    public void A_view_keeps_the_name_the_page_the_referrer_and_the_campaign_tags_and_no_address()
    {
        var view = PlatformVisits.Build("WWW.iProAccountants.com", "/accountants", "LinkedIn", "Social", "Launch-2026",
            "www.linkedin.com", Chrome, doNotTrack: false, ipAddress: "203.0.113.7", nowUtc: Now);

        Assert.NotNull(view);
        Assert.Equal("www.iproaccountants.com", view!.Host);
        Assert.Equal("/accountants", view.Path);
        Assert.Equal("www.linkedin.com", view.ReferrerHost);
        Assert.Equal("linkedin", view.Source);
        Assert.Equal("social", view.Medium);
        Assert.Equal("launch-2026", view.Campaign);
        Assert.Equal(64, view.VisitorHash.Length);
        Assert.DoesNotContain("203.0.113.7", view.VisitorHash);
        Assert.Equal(Now, view.CreatedAt);
    }

    [Fact]
    public void The_visitor_hash_is_the_same_person_within_a_month_and_nobody_across_months()
    {
        var same = PlatformVisits.VisitorHash("203.0.113.7", Chrome, Now);
        Assert.Equal(same, PlatformVisits.VisitorHash("203.0.113.7", Chrome, Now.AddDays(3)));
        Assert.NotEqual(same, PlatformVisits.VisitorHash("203.0.113.7", Chrome, Now.AddMonths(1)));
        Assert.NotEqual(same, PlatformVisits.VisitorHash("203.0.113.8", Chrome, Now));
        Assert.NotEqual(same, PlatformVisits.VisitorHash("203.0.113.7", Chrome + " Edg/129.0", Now));
    }

    [Theory]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)")]
    [InlineData("LinkedInBot/1.0 (compatible; Mozilla/5.0; Apache-HttpClient +http://www.linkedin.com)")]
    [InlineData("curl/8.4.0")]
    [InlineData("python-requests/2.32")]
    [InlineData("UptimeRobot/2.0")]
    [InlineData("")]
    public void A_crawler_a_link_preview_a_monitor_or_no_browser_at_all_is_not_a_visit(string userAgent)
    {
        Assert.Null(PlatformVisits.Build("www.iproadvisers.com", "/", null, null, null, "", userAgent, false, "203.0.113.7", Now));
    }

    [Fact]
    public void Do_not_track_is_honoured_and_a_normal_browser_is_counted()
    {
        Assert.Null(PlatformVisits.Build("www.iproadvisers.com", "/", null, null, null, "", Chrome, doNotTrack: true, "203.0.113.7", Now));
        Assert.NotNull(PlatformVisits.Build("www.iproadvisers.com", "/", null, null, null, "", Chrome, doNotTrack: false, "203.0.113.7", Now));
    }

    [Fact]
    public void Tags_and_names_are_trimmed_and_cut_to_size()
    {
        var view = PlatformVisits.Build("www.iproadvisers.com", "/", "  " + new string('x', 150) + "  ", null, null, "", Chrome, false, "203.0.113.7", Now);

        Assert.Equal(100, view!.Source.Length);
        Assert.Equal(string.Empty, view.Medium);
        Assert.Equal(string.Empty, view.Campaign);
    }

    [Theory]
    [InlineData("https://www.linkedin.com/feed/", "www.iproaccountants.com", "www.linkedin.com")]
    [InlineData("https://www.google.ca/", "www.iproadvisers.com", "www.google.ca")]
    [InlineData("https://www.iproaccountants.com/", "app.iproadvisers.com", "")]     // our own accountants page -> Register
    [InlineData("https://app.iproadvisers.com/", "www.iproadvisers.com", "")]        // our own platform host
    [InlineData("https://iproadvisers.com/", "www.iproadvisers.com", "")]            // the same site, another name
    [InlineData("not a url", "www.iproadvisers.com", "")]
    [InlineData("", "www.iproadvisers.com", "")]
    public void Only_a_referrer_from_outside_our_own_names_is_an_origin(string referer, string publicHost, string expected)
    {
        Assert.Equal(expected, PlatformVisitRecorder.ExternalReferrerHost(referer, Config(), publicHost));
    }

    [Theory]
    [InlineData("linkedin", "social", "launch", "www.linkedin.com", "linkedin / social / launch")]
    [InlineData("linkedin", "", "", "", "linkedin")]
    [InlineData("", "", "", "www.google.ca", "www.google.ca")]
    [InlineData("", "", "", "", "Direct / unknown")]
    public void An_origin_reads_as_its_campaign_then_its_referrer_then_direct(string source, string medium, string campaign, string referrer, string expected)
    {
        Assert.Equal(expected, PlatformVisits.OriginLabel(source, medium, campaign, referrer));
    }

    // ---- recording, on the real database --------------------------------------------------------------

    [Fact]
    public async Task A_view_is_written_and_a_sign_up_is_tied_to_the_campaign_that_brought_the_visitor()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var hash = PlatformVisits.VisitorHash("203.0.113.7", Chrome, Now);

        // Ten days ago from LinkedIn, then twice direct (the home page, then Register), then the sign-up.
        await PlatformVisits.RecordAsync(db, PlatformVisits.Build("www.iproaccountants.com", "/accountants", "linkedin", "social", "launch", "www.linkedin.com", Chrome, false, "203.0.113.7", Now.AddDays(-10))!);
        await PlatformVisits.RecordAsync(db, PlatformVisits.Build("www.iproadvisers.com", "/", null, null, null, "", Chrome, false, "203.0.113.7", Now.AddHours(-1))!);
        await PlatformVisits.RecordAsync(db, PlatformVisits.Build("app.iproadvisers.com", "/Account/Register", null, null, null, "", Chrome, false, "203.0.113.7", Now.AddMinutes(-5))!);
        var agentId = await SeedAgentAsync(db, "camp");

        await PlatformVisits.RecordSignupOriginAsync(db, agentId, hash, Now);

        Assert.Equal(3, await db.PlatformPageViews.CountAsync(v => v.VisitorHash == hash));
        var origin = await db.PlatformSignupOrigins.AsNoTracking().SingleAsync(o => o.AgentUserId == agentId);
        Assert.Equal("www.iproaccountants.com", origin.Host);
        Assert.Equal("/accountants", origin.Path);
        Assert.Equal("linkedin", origin.Source);
        Assert.Equal("launch", origin.Campaign);
        Assert.Equal("www.linkedin.com", origin.ReferrerHost);
        Assert.Equal(Now.AddDays(-10), origin.FirstSeenAt);
        Assert.Equal(Now, origin.RecordedAt);
    }

    [Fact]
    public async Task A_sign_up_with_no_earlier_view_is_still_counted_as_a_sign_up_from_nowhere_known()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, "direct");

        await PlatformVisits.RecordSignupOriginAsync(db, agentId, PlatformVisits.VisitorHash("203.0.113.9", Chrome, Now), Now);
        await PlatformVisits.RecordSignupOriginAsync(db, agentId, PlatformVisits.VisitorHash("203.0.113.9", Chrome, Now), Now);   // twice is not an error

        var origin = await db.PlatformSignupOrigins.AsNoTracking().SingleAsync(o => o.AgentUserId == agentId);
        Assert.Equal(string.Empty, origin.Source);
        Assert.Equal(string.Empty, origin.ReferrerHost);
        Assert.Null(origin.FirstSeenAt);
    }

    [Fact]
    public async Task A_view_by_the_same_person_last_month_does_not_count_as_this_sign_ups_origin()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var lastMonth = Now.AddDays(-40);
        await PlatformVisits.RecordAsync(db, PlatformVisits.Build("www.iproadvisers.com", "/", "newsletter", "email", "aug", "", Chrome, false, "203.0.113.7", lastMonth)!);
        var agentId = await SeedAgentAsync(db, "stale");

        await PlatformVisits.RecordSignupOriginAsync(db, agentId, PlatformVisits.VisitorHash("203.0.113.7", Chrome, Now), Now);

        var origin = await db.PlatformSignupOrigins.AsNoTracking().SingleAsync(o => o.AgentUserId == agentId);
        Assert.Equal(string.Empty, origin.Source);
    }

    [Fact]
    public async Task Deleting_the_agent_takes_the_origin_row_with_them_and_the_eraser_knows_the_table()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, "gone");
        await PlatformVisits.RecordSignupOriginAsync(db, agentId, "x", Now);

        await db.AgentUsers.Where(a => a.Id == agentId).ExecuteDeleteAsync();

        Assert.Equal(0, await db.PlatformSignupOrigins.CountAsync(o => o.AgentUserId == agentId));
        Assert.Contains("(\"PlatformSignupOrigins\",", File.ReadAllText(FindRepoFile(@"src\IPRO.DataAccess\AgentDataEraser.cs")));
    }

    // ---- the report ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_report_counts_the_period_by_day_page_name_referrer_and_campaign_and_sign_ups_by_origin()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        // Two people from LinkedIn (one twice), one from Google, one direct; one view outside the period.
        await Seed(db, "www.iproaccountants.com", "/accountants", "linkedin", "social", "launch", "www.linkedin.com", "203.0.113.1", Now.AddDays(-2));
        await Seed(db, "www.iproaccountants.com", "/accountants", "linkedin", "social", "launch", "www.linkedin.com", "203.0.113.1", Now.AddDays(-2).AddHours(1));
        await Seed(db, "www.iproadvisers.com", "/", "linkedin", "social", "launch", "www.linkedin.com", "203.0.113.2", Now.AddDays(-1));
        await Seed(db, "www.ipromortgages.com", "/mortgage", "", "", "", "www.google.ca", "203.0.113.3", Now.AddHours(-3));
        await Seed(db, "www.iproadvisers.com", "/", "", "", "", "", "203.0.113.4", Now.AddMinutes(-10));
        await Seed(db, "www.iproadvisers.com", "/", "", "", "", "", "203.0.113.5", Now.AddDays(-40));
        var a = await SeedAgentAsync(db, "ra");
        var b = await SeedAgentAsync(db, "rb");
        await PlatformVisits.RecordSignupOriginAsync(db, a, PlatformVisits.VisitorHash("203.0.113.1", Chrome, Now), Now.AddDays(-2).AddHours(2));
        await PlatformVisits.RecordSignupOriginAsync(db, b, PlatformVisits.VisitorHash("203.0.113.4", Chrome, Now), Now);

        var report = await PlatformVisits.ReportAsync(db, 30, Now);

        Assert.Equal(30, report.PeriodDays);
        Assert.Equal(5, report.TotalViews);
        Assert.Equal(4, report.UniqueVisitors);
        Assert.Equal(1, report.PreviousViews);
        Assert.Equal(2, report.SignUps);
        Assert.Equal(1, report.SignUpsWithOrigin);
        Assert.Equal(new[] { ("/", 2, 2), ("/accountants", 2, 1), ("/mortgage", 1, 1) }, report.ByPage.Select(r => (r.Label, r.Views, r.Visitors)).OrderBy(r => r.Label));
        Assert.Equal(new[] { ("www.iproaccountants.com", 2), ("www.iproadvisers.com", 2), ("www.ipromortgages.com", 1) }, report.ByHost.Select(r => (r.Label, r.Views)).OrderBy(r => r.Label));
        Assert.Equal(new[] { ("Direct / unknown", 1), ("www.google.ca", 1), ("www.linkedin.com", 3) }, report.ByReferrer.Select(r => (r.Label, r.Views)).OrderBy(r => r.Label));
        Assert.Equal(new[] { ("Direct / unknown", 1, 1), ("linkedin / social / launch", 3, 2), ("www.google.ca", 1, 1) }, report.ByCampaign.Select(r => (r.Label, r.Views, r.Visitors)).OrderBy(r => r.Label));
        Assert.Equal(new[] { ("Direct / unknown", 1), ("linkedin / social / launch", 1) }, report.SignUpsByOrigin.Select(r => (r.Label, r.SignUps)).OrderBy(r => r.Label));
        Assert.Equal(3, report.Daily.Count);
        Assert.Equal(5, report.Daily.Sum(d => d.Views));
        Assert.True(report.Daily.SequenceEqual(report.Daily.OrderBy(d => d.Date)));
    }

    [Fact]
    public async Task Control_an_empty_period_is_an_empty_report_not_an_error()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var report = await PlatformVisits.ReportAsync(db, 7, Now);

        Assert.Equal(0, report.TotalViews);
        Assert.Empty(report.ByPage);
        Assert.Empty(report.SignUpsByOrigin);
    }

    // ---- the wiring -----------------------------------------------------------------------------------------

    [Fact]
    public void The_four_public_pages_record_a_view_and_registration_records_the_origin()
    {
        var home = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\HomeController.cs"));
        Assert.Contains("PlatformVisitRecorder.RecordAsync(HttpContext, _db, \"/\")", home);
        Assert.Contains("PlatformVisitRecorder.RecordAsync(HttpContext, _db, \"/accountants\")", home);
        Assert.Contains("PlatformVisitRecorder.RecordAsync(HttpContext, _db, \"/mortgage\")", home);

        var account = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\AccountController.cs"));
        Assert.Contains("PlatformVisitRecorder.RecordAsync(HttpContext, _db, \"/Account/Register\")", account);
        Assert.Contains("PlatformVisitRecorder.RecordSignupAsync(HttpContext, _db, agent.Id)", account);
    }

    [Fact]
    public void Both_apps_create_the_tables_at_startup()
    {
        foreach (var program in new[] { @"src\IPRO.Web\Program.cs", @"src\IPRO.Admin\Program.cs" })
            Assert.Contains("StartupSchemaRepair.EnsurePlatformVisitorSchemaAsync(db)", File.ReadAllText(FindRepoFile(program)));
    }

    [Fact]
    public void SuperAdmin_has_a_Visitors_report_in_its_Reports_section()
    {
        var layout = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\Shared\_Layout.cshtml"));
        Assert.Contains("href=\"/Reports/Visitors\"", layout);

        var controller = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Controllers\VisitorsController.cs"));
        Assert.Contains("[HttpGet(\"/Reports/Visitors\")]", controller);
        Assert.Contains("PlatformVisits.ReportAsync(", controller);

        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\Visitors\Index.cshtml"));
        foreach (var marker in new[] { "Model.TotalViews", "Model.UniqueVisitors", "Model.SignUps", "Model.ByPage", "Model.ByCampaign", "Model.ByReferrer", "Model.SignUpsByOrigin", "Model.Daily" })
            Assert.Contains(marker, view);
    }

    // ---- plumbing --------------------------------------------------------------------------------------------

    private static Task Seed(IPRODbContext db, string host, string path, string source, string medium, string campaign, string referrer, string ip, DateTime at) =>
        PlatformVisits.RecordAsync(db, PlatformVisits.Build(host, path, source, medium, campaign, referrer, Chrome, false, ip, at)!);

    private static async Task<int> SeedAgentAsync(IPRODbContext db, string tag)
    {
        var rule = new BillingRule { PackageName = ($"PV-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"pv-{tag}-{Guid.NewGuid():N}")[..20],
            Email = $"pv-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Platform", LastName = "Visitor",
            DomainName = ($"pv-{Guid.NewGuid():N}")[..24],
            IsActive = true, PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
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
