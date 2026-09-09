using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 462(b) (2026-09-09). CalendarReminderJob ran every hour and emailed an agent one hour before
// a CalendarEvents row started -- and no portal page has ever written a CalendarEvents row (the
// client Calendar is appointments, follow-ups and Google events, all held elsewhere). A job that
// reads a table nothing writes can only do nothing or fail. It is removed; the stale recurring
// definition is dropped from Hangfire storage at startup so the dashboard does not show an hourly
// job whose type no longer exists; and the job count in the startup log line is pinned to the
// registrations so it cannot drift again.
public class CalendarReminderJobRemovedTests
{
    [Fact]
    public void The_dead_calendar_reminder_job_is_gone_and_its_schedule_entry_is_removed()
    {
        Assert.False(File.Exists(FindRepoFile(@"src\IPRO.Scheduler\CalendarReminderJob.cs")),
            "CalendarReminderJob.cs still exists");

        var program = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Program.cs"));
        Assert.DoesNotContain("AddOrUpdate<CalendarReminderJob>", program);
        Assert.Contains("RecurringJob.RemoveIfExists(\"calendar-reminders\")", program);
    }

    [Fact]
    public void The_startup_log_counts_the_jobs_actually_registered()
    {
        var program = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Program.cs"));
        var registered = Regex.Matches(program, @"RecurringJob\.AddOrUpdate<").Count;
        var logged = Regex.Match(program, @"recurring jobs registered\.""\s*,\s*(\d+)\)");
        Assert.True(logged.Success, "the startup log line with the job count was not found");
        Assert.Equal(registered, int.Parse(logged.Groups[1].Value));
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
