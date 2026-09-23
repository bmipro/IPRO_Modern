using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using Xunit;

namespace IPRO.IntegrationTests;

// 517 (2026-09-22). The Visitors report's "By day" grouped page views by the server's UTC day: at
// 8:42 p.m. in Toronto the owner saw a "Wed, Sep 23" row with 47 views ("this is not based on
// Eastern Standard time ... it is not September 23 yet"). A day is now the platform's own day --
// Admin:TimeZone, Eastern when unset, the same clock the SuperAdmin header shows. The defect test
// observed RED on the pre-fix code.
public class VisitorsDay517Tests
{
    private static PlatformPageView View(DateTime utc, string visitor) => new()
    {
        Host = "www.iproadvisers.com", Path = "/", ReferrerHost = string.Empty, Source = string.Empty, Medium = string.Empty, Campaign = string.Empty,
        VisitorHash = visitor, CreatedAt = utc
    };

    [Fact]
    public async Task A_visit_at_a_quarter_to_nine_in_Toronto_belongs_to_that_Toronto_day()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        db.PlatformPageViews.AddRange(
            View(new DateTime(2026, 9, 22, 16, 0, 0, DateTimeKind.Utc), "a"),    // noon in Toronto
            View(new DateTime(2026, 9, 23, 0, 42, 0, DateTimeKind.Utc), "b"),    // 8:42 p.m. in Toronto: still the 22nd
            View(new DateTime(2026, 9, 23, 4, 30, 0, DateTimeKind.Utc), "c"));   // 12:30 a.m. on the 23rd in Toronto
        await db.SaveChangesAsync();
        var now = new DateTime(2026, 9, 23, 5, 0, 0, DateTimeKind.Utc);

        var eastern = await PlatformVisits.ReportAsync(db, 7, now, "(GMT-05:00) Eastern Time (US & Canada)");
        Assert.Equal(new[] { (new DateTime(2026, 9, 22), 2, 2), (new DateTime(2026, 9, 23), 1, 1) }, eastern.Daily.Select(d => (d.Date, d.Views, d.Visitors)));

        var pacific = await PlatformVisits.ReportAsync(db, 7, now, "(GMT-08:00) Pacific Time (US & Canada)");
        Assert.Equal(new[] { (new DateTime(2026, 9, 22), 3, 3) }, pacific.Daily.Select(d => (d.Date, d.Views, d.Visitors)));   // 9:30 p.m. on the coast

        var unset = await PlatformVisits.ReportAsync(db, 7, now);   // Eastern when no zone is configured
        Assert.Equal(eastern.Daily.Select(d => d.Date), unset.Daily.Select(d => d.Date));
    }

    [Fact]
    public void The_report_page_asks_for_the_platforms_own_day()
    {
        var controller = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Controllers\VisitorsController.cs"));
        Assert.Contains("AdminClock.Zone(", controller);
        Assert.Contains("PlatformVisits.ReportAsync(", controller);   // 512's pin stays
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
