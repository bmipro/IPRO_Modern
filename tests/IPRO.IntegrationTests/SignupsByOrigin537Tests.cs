using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// 537 (2026-10-01). SuperAdmin -> Reports -> Visitors showed "linkedin / organic_social / ipro_relaunch: 1"
// under Sign-ups by origin, and the owner: "we know someone did but we dont know who it was. Can we have the
// number of signups to be clickable that will show who had signed up". The origin was already stored against
// the adviser who signed up (512); the report only counted. Now each count opens the names: who, their
// company, email and package, when, and the page they landed on, each linked to the adviser's record.
public class SignupsByOrigin537Tests
{
    // Mid-month on purpose: the visitor hash changes with the month, so a view on 28 September can
    // never place a sign-up on 1 October (512's rule, and its own test).
    private static readonly DateTime Now = new(2026, 10, 15, 15, 0, 0, DateTimeKind.Utc);
    private const string Chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Safari/537.36";

    [Fact]
    public async Task Each_origin_names_who_signed_up_newest_first()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await View(db, "www.iproadvisers.com", "/", "linkedin", "organic_social", "ipro_relaunch", "www.linkedin.com", "203.0.113.1", Now.AddDays(-3));
        await View(db, "www.iproaccountants.com", "/accountants", "linkedin", "organic_social", "ipro_relaunch", "www.linkedin.com", "203.0.113.2", Now.AddDays(-1));
        await View(db, "www.iproadvisers.com", "/", "", "", "", "", "203.0.113.3", Now.AddHours(-5));
        var first = await SeedAgentAsync(db, "Dana", "Whitcombe", "Whitcombe Financial", "IPro Gold");
        var second = await SeedAgentAsync(db, "Omar", "Haddad", "", "IPro Silver");
        var direct = await SeedAgentAsync(db, "Lee", "Park", "Park Books", "IPro Platinum");
        await PlatformVisits.RecordSignupOriginAsync(db, first.Id, PlatformVisits.VisitorHash("203.0.113.1", Chrome, Now), Now.AddDays(-3).AddMinutes(20));
        await PlatformVisits.RecordSignupOriginAsync(db, second.Id, PlatformVisits.VisitorHash("203.0.113.2", Chrome, Now), Now.AddDays(-1).AddMinutes(5));
        await PlatformVisits.RecordSignupOriginAsync(db, direct.Id, PlatformVisits.VisitorHash("203.0.113.3", Chrome, Now), Now.AddHours(-4));

        var report = await PlatformVisits.ReportAsync(db, 30, Now);

        var linkedin = report.SignUpsByOrigin.Single(r => r.Label == "linkedin / organic_social / ipro_relaunch");
        Assert.Equal(2, linkedin.SignUps);
        Assert.Equal(new[] { second.Id, first.Id }, linkedin.Who.Select(w => w.AgentUserId).ToArray());   // newest first
        var newest = linkedin.Who[0];
        Assert.Equal("Omar Haddad", newest.Name);
        Assert.Equal(string.Empty, newest.Company);
        Assert.Equal(second.Email, newest.Email);
        Assert.Equal("IPro Silver", newest.Package);
        Assert.Equal("www.iproaccountants.com/accountants", newest.LandedOn);
        Assert.Equal(AgentLocalTime.FromUtc(Now.AddDays(-1).AddMinutes(5), null), newest.SignedUpAt);
        Assert.Equal("Whitcombe Financial", linkedin.Who[1].Company);

        var unplaced = report.SignUpsByOrigin.Single(r => r.Label == PlatformVisits.DirectLabel);
        Assert.Equal("Lee Park", Assert.Single(unplaced.Who).Name);
        // The counts are what they were: every sign-up in the period, and every one of them named.
        Assert.Equal(3, report.SignUps);
        Assert.Equal(2, report.SignUpsWithOrigin);
        Assert.All(report.SignUpsByOrigin, r => Assert.Equal(r.SignUps, r.Who.Count));

        // An origin row cannot outlive its adviser (the foreign key cascades), which is why naming people
        // drops nobody from the count: a deleted account leaves the report altogether, as it did before.
        await db.AgentUsers.Where(a => a.Id == first.Id).ExecuteDeleteAsync();
        var after = await PlatformVisits.ReportAsync(db, 30, Now);
        Assert.Equal(2, after.SignUps);
        Assert.Equal(second.Id, Assert.Single(after.SignUpsByOrigin.Single(r => r.Label == "linkedin / organic_social / ipro_relaunch").Who).AgentUserId);
        Assert.False(await db.PlatformSignupOrigins.AsNoTracking().AnyAsync(o => o.AgentUserId == first.Id));
    }

    [Fact]
    public async Task An_adviser_with_no_name_or_no_plan_row_is_still_listed()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var nameless = await SeedAgentAsync(db, "", "", "", "IPro Gold");
        // The plan row is gone (a package deleted in SuperAdmin): the adviser is still named.
        await db.AgentUsers.Where(a => a.Id == nameless.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.PackageId, int.MaxValue));
        await PlatformVisits.RecordSignupOriginAsync(db, nameless.Id, PlatformVisits.VisitorHash("203.0.113.9", Chrome, Now), Now.AddHours(-2));

        var report = await PlatformVisits.ReportAsync(db, 30, Now);

        var who = Assert.Single(Assert.Single(report.SignUpsByOrigin).Who);
        Assert.Equal(PlatformVisits.DirectLabel, report.SignUpsByOrigin[0].Label);
        Assert.Equal(string.Empty, who.Name);
        Assert.Equal(nameless.Email, who.Email);
        Assert.Equal(string.Empty, who.Package);
        Assert.Equal(string.Empty, who.LandedOn);   // no view was matched, so no page to name
        Assert.Equal(1, report.SignUps);
    }

    [Fact]
    public void The_count_opens_the_names_and_each_name_opens_the_advisers_record()
    {
        var view = Read(@"src\IPRO.Admin\Views\Visitors\Index.cshtml");
        // A native details element: the count opens the names with no script to load or to be blocked.
        var origins = view[view.IndexOf("@foreach (var row in Model.SignUpsByOrigin)", StringComparison.Ordinal)..];
        var summary = origins[origins.IndexOf("<summary", StringComparison.Ordinal)..origins.IndexOf("</summary>", StringComparison.Ordinal)];
        Assert.Contains("@row.Label", summary);
        Assert.Contains("@row.SignUps.ToString(\"N0\")", summary);
        Assert.True(origins.IndexOf("<details>", StringComparison.Ordinal) < origins.IndexOf("<summary", StringComparison.Ordinal));
        Assert.True(origins.IndexOf("</summary>", StringComparison.Ordinal) < origins.IndexOf("row.Who", StringComparison.Ordinal));
        Assert.True(origins.IndexOf("row.Who", StringComparison.Ordinal) < origins.IndexOf("</details>", StringComparison.Ordinal));
        Assert.DoesNotContain("data-bs-toggle", view);
        Assert.Contains("href=\"/Agents/Details/@who.AgentUserId\"", view);
        // The email is the link when there is no name, so it is not repeated beside itself.
        Assert.Contains("@(who.Name.Length > 0 ? who.Name : who.Email.Length > 0 ? who.Email : $\"Adviser {who.AgentUserId}\")", view);
        Assert.Contains("@if (who.Name.Length > 0 && who.Email.Length > 0) { <span class=\"text-muted\">&middot; @who.Email</span> }", view);
        Assert.Contains("who.LandedOn", view);
        // The time is on the platform's clock and says which one, like the header.
        Assert.Contains("who.SignedUpAt.ToString(\"MMM d, yyyy h:mm tt\") @zoneLabel", view);
        Assert.Contains("AdminClock.Label(ViewBag.Zone as string)", view);
        Assert.Contains("ViewBag.Zone = zone;", Read(@"src\IPRO.Admin\Controllers\VisitorsController.cs"));
    }

    private static Task View(IPRODbContext db, string host, string path, string source, string medium, string campaign, string referrer, string ip, DateTime at) =>
        PlatformVisits.RecordAsync(db, PlatformVisits.Build(host, path, source, medium, campaign, referrer, Chrome, false, ip, at)!);

    private static async Task<AgentUser> SeedAgentAsync(IPRODbContext db, string firstName, string lastName, string company, string package)
    {
        var rule = new BillingRule { PackageName = package, MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"so-{Guid.NewGuid():N}")[..20],
            Email = $"so-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = firstName, LastName = lastName, CompanyName = company,
            DomainName = ($"so-{Guid.NewGuid():N}")[..24],
            IsActive = true, PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent;
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
