using System;
using System.IO;
using IPRO.Admin.Infrastructure;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Xunit;

namespace IPRO.IntegrationTests;

// 502 (2026-09-19), seen in the owner's own support-ticket test two days before launch: he wrote a
// ticket at 12:38 p.m. Eastern (the SuperAdmin header clock said so) and BOTH apps stamped the
// message "4:38 PM" -- the four support screens printed the stored UTC value as it stood. A customer
// opening a ticket on launch day would have read a time four hours off. And the status badge read
// "InProgress", the enum's name. The portal now shows the adviser's own time zone, SuperAdmin shows
// the platform's (the same zone and label as its header clock), and the status reads as words.
public class SupportTicketDisplay502Tests
{
    private static readonly DateTime Written = new(2026, 9, 19, 16, 38, 0, DateTimeKind.Utc);

    [Fact]
    public void The_portal_prints_a_ticket_time_in_the_advisers_own_time_zone()
    {
        Assert.Equal("Sep 19, 2026 12:38 PM", SupportTicketDisplay.Time(Written, "(GMT-05:00) Eastern Time (US & Canada)"));
        Assert.Equal("Sep 19, 2026 9:38 AM", SupportTicketDisplay.Time(Written, "(GMT-08:00) Pacific Time (US & Canada)"));
        Assert.Equal("Sep 19, 2026 12:38 PM", SupportTicketDisplay.Time(Written, null));   // no zone on the profile: Eastern, like everything else
        // A value read back from MySQL has no Kind; it is still UTC.
        Assert.Equal("Sep 19, 2026 12:38 PM", SupportTicketDisplay.Time(DateTime.SpecifyKind(Written, DateTimeKind.Unspecified), null));
    }

    [Fact]
    public void SuperAdmin_prints_it_in_the_platforms_zone_with_the_label_its_header_clock_uses()
    {
        Assert.Equal("Sep 19, 2026 12:38 PM ET", AdminClock.Format(Written, null));
    }

    [Theory]
    [InlineData(SupportTicketStatus.Open, "Open")]
    [InlineData(SupportTicketStatus.InProgress, "In progress")]
    [InlineData(SupportTicketStatus.Resolved, "Resolved")]
    [InlineData(SupportTicketStatus.Closed, "Closed")]
    public void A_status_reads_as_words(SupportTicketStatus status, string expected) =>
        Assert.Equal(expected, status.ToDisplayText());

    [Theory]
    [InlineData(@"src\IPRO.Web\Views\Support\Index.cshtml")]
    [InlineData(@"src\IPRO.Web\Views\Support\Details.cshtml")]
    [InlineData(@"src\IPRO.Admin\Views\SupportTickets\Index.cshtml")]
    [InlineData(@"src\IPRO.Admin\Views\SupportTickets\Details.cshtml")]
    public void No_support_screen_prints_a_stored_time_or_a_status_name_as_it_stands(string view)
    {
        var source = File.ReadAllText(FindRepoFile(view));
        Assert.DoesNotContain("CreatedAt.ToString(", source);
        Assert.DoesNotContain("LastMessageAt.ToString(", source);
        Assert.DoesNotContain("Status</span>", source);          // @Model.Status / @ticket.Status inside the badge
        Assert.Contains("ToDisplayText()", source);
        Assert.Contains(view.Contains("IPRO.Admin") ? "AdminClock.Format(" : "SupportTicketDisplay.Time(", source);
    }

    [Fact]
    public void Both_controllers_hand_their_views_the_zone()
    {
        var portal = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\SupportController.cs"));
        Assert.Contains("AgentTimeZoneHelper.ResolveForAgentAsync(_db, AgentId)", portal);
        var admin = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Controllers\SupportTicketsController.cs"));
        Assert.Contains("ViewBag.Zone = AdminClock.Zone(_configuration);", admin);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "IPRO.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
