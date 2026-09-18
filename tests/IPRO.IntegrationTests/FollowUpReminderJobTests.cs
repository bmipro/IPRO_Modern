using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Scheduler;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using EmailSendResult = IPRO.Email.EmailSendResult;

namespace IPRO.IntegrationTests;

// 498 (2026-09-18). The plan table sold an "Email reminder" on every plan with nothing behind it (its
// job was removed on 2026-09-09); the truth sweep found it, 496 withdrew the row, and the owner's
// answer the same afternoon was to build the real thing at once. FollowUpReminderJob: one email in
// the adviser's own morning listing what is due today and what is overdue. Hourly, so it is its own
// retry when the send gate defers; each adviser's day is claimed before the mail is sent; an item
// nobody completes is reminded for a week and then left alone; lapsed accounts, plans without the
// feature and advisers who turned it off get nothing; and the mail queues behind the transactional
// reserve, so a morning's reminders can never crowd out a password reset.
public class FollowUpReminderJobTests
{
    // Monday 2026-09-21, 11:30 UTC: 07:30 in Toronto, 04:30 in Vancouver.
    private static readonly DateTime MondayMorning = new(2026, 9, 21, 11, 30, 0, DateTimeKind.Utc);
    private const string Pacific = "(GMT-08:00) Pacific Time (US & Canada)";

    [Fact]
    public async Task One_morning_email_lists_what_is_due_and_overdue_and_goes_once_a_day()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agent = await SeedAgentAsync(db, AgentLocalTime.DefaultTimeZone);
        var client = await SeedClientAsync(db, agent.Id, "Dana", "Whitcombe");
        // DueAt is the adviser's OWN calendar date, stored exactly as typed: the follow-up form posts a
        // date with no time (ClientsController.AddFollowUp), the appointment scheduler a local date and
        // time (PortalRequestsController.Schedule); nothing converts either to UTC, and the follow-up list
        // compares DueAt.Date with today. So "due 21 September" is 2026-09-21 00:00 -- and read as a UTC
        // instant that is 20 September, 8 p.m. in Toronto: due today would be mailed as overdue.
        db.AddRange(
            new ClientFollowUp { ClientId = client.Id, Title = "Send the renewal summary", Notes = "PRIVATE NOTE", DueAt = new DateTime(2026, 9, 18) },
            new ClientFollowUp { ClientId = client.Id, Title = "Call about the <quote>", DueAt = new DateTime(2026, 9, 21) },
            new ClientFollowUp { ClientId = client.Id, Title = "Appointment: Dana Whitcombe", DueAt = new DateTime(2026, 9, 21, 14, 0, 0) },
            new ClientFollowUp { ClientId = client.Id, Title = "Already done", DueAt = new DateTime(2026, 9, 21), IsCompleted = true },
            new ClientFollowUp { ClientId = client.Id, Title = "Tomorrow's task", DueAt = new DateTime(2026, 9, 22) });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var email = new RecordingEmailService();
        var job = NewJob(db, email, new StubEntitlements());
        job.Clock = () => MondayMorning;
        await job.RunAsync();

        var mail = Assert.Single(email.Sent);
        Assert.Equal(agent.Email, mail.To);
        Assert.Equal("2 follow-ups due today, 1 overdue", mail.Subject);
        Assert.Contains("Good morning Morgan.", mail.Html);
        Assert.Contains("Dana Whitcombe", mail.Html);
        Assert.Contains("Due today (2)", mail.Html);
        Assert.Contains("Call about the &lt;quote&gt;", mail.Html);      // the adviser's own words are encoded
        Assert.Contains("2:00 PM", mail.Html);                            // an appointment keeps the time it was booked for
        Assert.DoesNotContain("12:00 AM", mail.Html);                     // a date typed without a time has none to show
        Assert.True(mail.Html.IndexOf("Call about the", StringComparison.Ordinal) < mail.Html.IndexOf("Overdue (1)", StringComparison.Ordinal),
            "an item due today was listed as overdue");
        Assert.Contains("Overdue (1)", mail.Html);
        Assert.Contains("Send the renewal summary", mail.Html);
        Assert.Contains("since Sep 18", mail.Html);
        Assert.DoesNotContain("Already done", mail.Html);
        Assert.DoesNotContain("Tomorrow", mail.Html);
        Assert.DoesNotContain("PRIVATE NOTE", mail.Html);                 // notes never leave the portal
        Assert.DoesNotContain("PRIVATE NOTE", mail.Text);
        // The list's real address: the action is FollowUpQueue but the route is Clients/FollowUps, and
        // /Clients/FollowUpQueue answers 404 on the live host.
        Assert.Contains("https://app.example.test/portal/Clients/FollowUps?status=open", mail.Html);
        Assert.DoesNotContain("FollowUpQueue", mail.Html);
        Assert.Contains("https://app.example.test/Account/Profile", mail.Html);   // where to turn it off
        Assert.Contains("https://app.example.test/portal/Clients/FollowUps?status=open", mail.Text);
        // Behind the transactional reserve under the hourly cap.
        Assert.True(EmailSendGate.IsBulk(mail.CustomArgs));

        // The next hourly pass the same day sends nothing more...
        job.Clock = () => MondayMorning.AddHours(1);
        await job.RunAsync();
        Assert.Single(email.Sent);
        // ...and the next morning it goes again, with Monday's item now overdue.
        job.Clock = () => MondayMorning.AddDays(1);
        await job.RunAsync();
        Assert.Equal(2, email.Sent.Count);
        Assert.Equal("1 follow-up due today, 3 overdue", email.Sent[1].Subject);
    }

    [Theory]
    [InlineData(1, 0, "1 follow-up due today")]
    [InlineData(3, 0, "3 follow-ups due today")]
    [InlineData(0, 1, "1 follow-up overdue")]
    [InlineData(0, 4, "4 follow-ups overdue")]
    [InlineData(2, 5, "2 follow-ups due today, 5 overdue")]
    public void The_subject_says_exactly_what_is_waiting(int dueToday, int overdue, string expected) =>
        Assert.Equal(expected, FollowUpReminderJob.Subject(dueToday, overdue));

    [Fact]
    public async Task Nothing_goes_before_the_advisers_own_morning_after_noon_or_to_one_who_turned_it_off()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var vancouver = await SeedAgentAsync(db, Pacific);
        var optedOut = await SeedAgentAsync(db, AgentLocalTime.DefaultTimeZone);
        db.AgentFollowUpReminders.Add(new AgentFollowUpReminder { AgentUserId = optedOut.Id, IsEnabled = false });
        foreach (var a in new[] { vancouver, optedOut })
        {
            var c = await SeedClientAsync(db, a.Id, "Due", "Today");
            db.Add(new ClientFollowUp { ClientId = c.Id, Title = "Something due", DueAt = MondayMorning.AddHours(-30) });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var email = new RecordingEmailService();
        var job = NewJob(db, email, new StubEntitlements());
        job.Clock = () => MondayMorning;               // 04:30 in Vancouver
        await job.RunAsync();
        Assert.Empty(email.Sent);
        Assert.False(await db.AgentFollowUpReminders.AsNoTracking().AnyAsync(r => r.AgentUserId == vancouver.Id));

        job.Clock = () => MondayMorning.AddHours(3);    // 07:30 in Vancouver
        await job.RunAsync();
        Assert.Equal(vancouver.Email, Assert.Single(email.Sent).To);

        // A "good morning" at half past noon is worse than no mail: the first pass after a deploy, or a
        // long deferral, must not send one. The day is left undecided, not claimed.
        var late = await SeedAgentAsync(db, AgentLocalTime.DefaultTimeZone);
        var lateClient = await SeedClientAsync(db, late.Id, "Due", "Today");
        db.Add(new ClientFollowUp { ClientId = lateClient.Id, Title = "Something due", DueAt = MondayMorning.AddHours(-30) });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        job.Clock = () => new DateTime(2026, 9, 21, 16, 30, 0, DateTimeKind.Utc);   // 12:30 in Toronto, 09:30 in Vancouver
        await job.RunAsync();
        Assert.Single(email.Sent);
        Assert.False(await db.AgentFollowUpReminders.AsNoTracking().AnyAsync(r => r.AgentUserId == late.Id));
    }

    [Fact]
    public async Task An_item_nobody_completes_is_reminded_for_a_week_and_then_left_alone()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var recent = await SeedAgentAsync(db, AgentLocalTime.DefaultTimeZone);
        var stale = await SeedAgentAsync(db, AgentLocalTime.DefaultTimeZone);
        var mixed = await SeedAgentAsync(db, AgentLocalTime.DefaultTimeZone);
        var recentClient = await SeedClientAsync(db, recent.Id, "Recent", "Client");
        var staleClient = await SeedClientAsync(db, stale.Id, "Stale", "Client");
        var mixedClient = await SeedClientAsync(db, mixed.Id, "Mixed", "Client");
        db.AddRange(
            new ClientFollowUp { ClientId = recentClient.Id, Title = "Three days late", DueAt = MondayMorning.AddDays(-3) },
            new ClientFollowUp { ClientId = staleClient.Id, Title = "Imported years ago", DueAt = MondayMorning.AddDays(-400) },
            new ClientFollowUp { ClientId = mixedClient.Id, Title = "Old but still open", DueAt = MondayMorning.AddDays(-400) },
            new ClientFollowUp { ClientId = mixedClient.Id, Title = "Due this afternoon", DueAt = MondayMorning.AddHours(6) });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var email = new RecordingEmailService();
        var job = NewJob(db, email, new StubEntitlements());
        job.Clock = () => MondayMorning;
        await job.RunAsync();

        Assert.Equal(2, email.Sent.Count);
        Assert.Contains(email.Sent, m => m.To == recent.Email && m.Subject == "1 follow-up overdue");
        Assert.DoesNotContain(email.Sent, m => m.To == stale.Email);
        // The old item no longer CAUSES a mail, but it is still listed when one goes.
        var both = email.Sent.Single(m => m.To == mixed.Email);
        Assert.Equal("1 follow-up due today, 1 overdue", both.Subject);
        Assert.Contains("Old but still open", both.Html);
        // And "no mail" was today's decision for the stale account, made once.
        Assert.Equal(new DateTime(2026, 9, 21), (await db.AgentFollowUpReminders.AsNoTracking().SingleAsync(r => r.AgentUserId == stale.Id)).LastDecidedOn);
    }

    [Fact]
    public async Task Todays_items_come_first_and_a_pile_of_old_ones_is_capped_newest_first()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agent = await SeedAgentAsync(db, AgentLocalTime.DefaultTimeZone);
        var client = await SeedClientAsync(db, agent.Id, "Busy", "Client");
        db.AddRange(
            new ClientFollowUp { ClientId = client.Id, Title = "Today first", DueAt = MondayMorning.AddHours(2) },
            new ClientFollowUp { ClientId = client.Id, Title = "Today second", DueAt = MondayMorning.AddHours(5) },
            new ClientFollowUp { ClientId = client.Id, Title = "Recently overdue", DueAt = MondayMorning.AddDays(-2) });
        for (var i = 1; i <= 14; i++)
            db.Add(new ClientFollowUp { ClientId = client.Id, Title = $"Old item {i:00}", DueAt = MondayMorning.AddDays(-100 - i) });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var email = new RecordingEmailService();
        var job = NewJob(db, email, new StubEntitlements());
        job.Clock = () => MondayMorning;
        await job.RunAsync();

        var mail = Assert.Single(email.Sent);
        Assert.Equal("2 follow-ups due today, 15 overdue", mail.Subject);
        var html = mail.Html;
        Assert.True(html.IndexOf("Today first", StringComparison.Ordinal) < html.IndexOf("Today second", StringComparison.Ordinal));
        Assert.True(html.IndexOf("Today second", StringComparison.Ordinal) < html.IndexOf("Recently overdue", StringComparison.Ordinal));
        Assert.True(html.IndexOf("Recently overdue", StringComparison.Ordinal) < html.IndexOf("Old item 01", StringComparison.Ordinal));
        // Fifteen rows in all: the two of today, then the thirteen most recently overdue.
        Assert.Contains("Old item 12", html);
        Assert.DoesNotContain("Old item 13", html);
        Assert.DoesNotContain("Old item 14", html);
        Assert.Contains("...and 2 more on your list.", html);
    }

    [Fact]
    public async Task A_morning_with_nothing_due_sends_nothing_and_is_decided_once()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agent = await SeedAgentAsync(db, AgentLocalTime.DefaultTimeZone);

        var email = new RecordingEmailService();
        var job = NewJob(db, email, new StubEntitlements());
        job.Clock = () => MondayMorning;
        await job.RunAsync();

        Assert.Empty(email.Sent);
        var row = await db.AgentFollowUpReminders.AsNoTracking().SingleAsync(r => r.AgentUserId == agent.Id);
        Assert.True(row.IsEnabled);
        Assert.Equal(new DateTime(2026, 9, 21), row.LastDecidedOn);
    }

    [Fact]
    public async Task A_deferred_send_hands_the_day_back_and_ends_the_pass_so_the_next_hour_retries()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var first = await SeedAgentAsync(db, AgentLocalTime.DefaultTimeZone);
        var second = await SeedAgentAsync(db, AgentLocalTime.DefaultTimeZone);
        foreach (var a in new[] { first, second })
        {
            var c = await SeedClientAsync(db, a.Id, "Due", "Today");
            db.Add(new ClientFollowUp { ClientId = c.Id, Title = "Something due", DueAt = MondayMorning.AddHours(2) });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var deferring = new RecordingEmailService { Defer = true };
        var job = NewJob(db, deferring, new StubEntitlements());
        job.Clock = () => MondayMorning;
        await job.RunAsync();

        Assert.Equal(1, deferring.Calls);   // the pass ended at the first deferral
        Assert.All(await db.AgentFollowUpReminders.AsNoTracking().ToListAsync(), r => Assert.Null(r.LastDecidedOn));

        var working = new RecordingEmailService();
        var nextHour = NewJob(db, working, new StubEntitlements());
        nextHour.Clock = () => MondayMorning.AddHours(1);
        await nextHour.RunAsync();
        Assert.Equal(2, working.Sent.Count);
    }

    [Fact]
    public async Task A_plan_without_the_feature_and_a_lapsed_account_get_no_mail()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var notEntitled = await SeedAgentAsync(db, AgentLocalTime.DefaultTimeZone);
        var lapsed = await SeedAgentAsync(db, AgentLocalTime.DefaultTimeZone);
        var inactive = await SeedAgentAsync(db, AgentLocalTime.DefaultTimeZone, isActive: false);
        foreach (var a in new[] { notEntitled, lapsed, inactive })
        {
            var c = await SeedClientAsync(db, a.Id, "Due", "Today");
            db.Add(new ClientFollowUp { ClientId = c.Id, Title = "Something due", DueAt = MondayMorning.AddHours(2) });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var email = new RecordingEmailService();
        var entitlements = new StubEntitlements();
        entitlements.NotEntitled.Add(notEntitled.Id);
        entitlements.Gated.Add(lapsed.Id);
        var job = NewJob(db, email, entitlements);
        job.Clock = () => MondayMorning;
        await job.RunAsync();

        Assert.Empty(email.Sent);
        Assert.Equal(PackageFeatureCodes.EmailReminder, entitlements.LastFeatureAsked);
    }

    // A job that mails many advisers, shipped three days before launch, gets two switches that need no
    // deploy (App Service settings FollowUpReminders__Enabled and FollowUpReminders__NotBefore): off
    // altogether, and "not before this date". A NotBefore nobody can read holds the mail rather than
    // sending it: whoever typed it meant to wait.
    [Theory]
    [InlineData("FollowUpReminders:Enabled", "false", 0, 0)]
    [InlineData("FollowUpReminders:NotBefore", "2026-09-22", 0, 1)]
    [InlineData("FollowUpReminders:NotBefore", "2026-09-21", 1, 1)]
    [InlineData("FollowUpReminders:NotBefore", "next monday", 0, 0)]
    public async Task The_job_can_be_held_or_switched_off_without_a_deploy(string key, string value, int sentOnMonday, int sentByTuesday)
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agent = await SeedAgentAsync(db, AgentLocalTime.DefaultTimeZone);
        var client = await SeedClientAsync(db, agent.Id, "Due", "Today");
        db.Add(new ClientFollowUp { ClientId = client.Id, Title = "Something due", DueAt = MondayMorning.AddHours(2) });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var email = new RecordingEmailService();
        var job = NewJob(db, email, new StubEntitlements(), (key, value));
        job.Clock = () => MondayMorning;
        await job.RunAsync();
        Assert.Equal(sentOnMonday, email.Sent.Count);
        if (sentOnMonday == 0)   // held is not "decided": the day is not claimed
            Assert.False(await db.AgentFollowUpReminders.AsNoTracking().AnyAsync(r => r.LastDecidedOn != null));

        job.Clock = () => MondayMorning.AddDays(1);
        await job.RunAsync();
        Assert.Equal(sentByTuesday, email.Sent.Count(m => m.To == agent.Email && m.Subject.Contains("overdue")));
    }

    [Fact]
    public async Task The_profile_switch_defaults_to_on_and_turns_off_and_back_on()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agent = await SeedAgentAsync(db, AgentLocalTime.DefaultTimeZone);
        var other = await SeedAgentAsync(db, AgentLocalTime.DefaultTimeZone);

        Assert.True(await FollowUpReminderPreference.IsEnabledAsync(db, agent.Id));
        await FollowUpReminderPreference.SetAsync(db, agent.Id, true);          // "on" with no row writes nothing
        Assert.False(await db.AgentFollowUpReminders.AsNoTracking().AnyAsync());

        await FollowUpReminderPreference.SetAsync(db, agent.Id, false);
        await FollowUpReminderPreference.SetAsync(db, agent.Id, false);         // saving the profile twice is not an error
        Assert.False(await FollowUpReminderPreference.IsEnabledAsync(db, agent.Id));
        Assert.True(await FollowUpReminderPreference.IsEnabledAsync(db, other.Id));

        // Off means off: nothing goes, whatever is due.
        var client = await SeedClientAsync(db, agent.Id, "Due", "Today");
        db.Add(new ClientFollowUp { ClientId = client.Id, Title = "Something due", DueAt = MondayMorning.AddHours(2) });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var email = new RecordingEmailService();
        var job = NewJob(db, email, new StubEntitlements());
        job.Clock = () => MondayMorning;
        await job.RunAsync();
        Assert.Empty(email.Sent);

        await FollowUpReminderPreference.SetAsync(db, agent.Id, true);
        Assert.True(await FollowUpReminderPreference.IsEnabledAsync(db, agent.Id));
        job.Clock = () => MondayMorning.AddHours(1);
        await job.RunAsync();
        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task The_schema_repair_creates_a_table_the_model_can_use_and_is_safe_to_repeat()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agent = await SeedAgentAsync(db, AgentLocalTime.DefaultTimeZone);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE `AgentFollowUpReminders`");   // production before this deploy

        await StartupSchemaRepair.EnsureFollowUpReminderSchemaAsync(db);
        await StartupSchemaRepair.EnsureFollowUpReminderSchemaAsync(db);

        await FollowUpReminderPreference.SetAsync(db, agent.Id, false);
        var row = await db.AgentFollowUpReminders.AsNoTracking().SingleAsync();
        Assert.Equal(agent.Id, row.AgentUserId);
        Assert.False(row.IsEnabled);
        Assert.Null(row.LastDecidedOn);

        // Goes with the account.
        await db.AgentUsers.Where(a => a.Id == agent.Id).ExecuteDeleteAsync();
        Assert.False(await db.AgentFollowUpReminders.AsNoTracking().AnyAsync());
    }

    [Fact]
    public void The_job_is_scheduled_the_table_is_repaired_in_both_apps_and_the_profile_has_the_switch()
    {
        var web = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Program.cs"));
        Assert.Contains("RecurringJob.AddOrUpdate<FollowUpReminderJob>(\"follow-up-reminders\", job => job.RunAsync(), \"5 * * * *\");", web);
        const string step = "await StartupGuard.RunStepAsync(\"StartupSchemaRepair.EnsureFollowUpReminderSchemaAsync\", () => StartupSchemaRepair.EnsureFollowUpReminderSchemaAsync(db), db, app.Logger);";
        Assert.Contains(step, web);
        Assert.Contains(step, File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Program.cs")));

        // The advisers table itself is left alone: see AgentFollowUpReminder for why.
        var agentUser = File.ReadAllText(FindRepoFile(@"src\IPRO.Entities\AgentUser.cs"));
        Assert.DoesNotContain("FollowUp", agentUser);

        var profile = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Account\Profile.cshtml"));
        Assert.Contains("asp-for=\"FollowUpReminderEmails\"", profile);
        Assert.Contains("Email me my follow-ups each morning", profile);
        // And the follow-up list says the email exists and where its switch is.
        Assert.Contains("A morning email lists what is due and overdue.", File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Clients\FollowUpQueue.cshtml")));
        var controller = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\AccountController.cs"));
        Assert.Contains("FollowUpReminderPreference.IsEnabledAsync(_db, agent.Id)", controller);
        Assert.Contains("FollowUpReminderPreference.SetAsync(_db, agent.Id, model.FollowUpReminderEmails)", controller);
    }

    [Fact]
    public async Task The_plan_row_is_back_on_every_plan_under_a_name_that_says_what_it_is()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await PackageEntitlementSeeder.SeedAsync(db);

        var rows = await db.PackageFeatures.AsNoTracking().Where(f => f.FeatureCode == PackageFeatureCodes.EmailReminder).ToListAsync();
        Assert.Equal(await db.BillingRules.CountAsync(), rows.Count);
        Assert.All(rows, f =>
        {
            Assert.Equal("Daily follow-up reminder email", f.FeatureName);
            Assert.True(f.IsIncluded);
        });

        // A database that still carries the row under its old name is renamed, not left selling
        // "Email reminder"; and one where 496 already removed it gets it back.
        await db.PackageFeatures.Where(f => f.FeatureCode == PackageFeatureCodes.EmailReminder)
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.FeatureName, "Email reminder"));
        db.ChangeTracker.Clear();   // the seeder must read the rows as the next start-up would, not from this context's cache
        await PackageEntitlementSeeder.SeedAsync(db);
        Assert.All(await db.PackageFeatures.AsNoTracking().Where(f => f.FeatureCode == PackageFeatureCodes.EmailReminder).ToListAsync(),
            f => Assert.Equal("Daily follow-up reminder email", f.FeatureName));

        await db.PackageFeatures.Where(f => f.FeatureCode == PackageFeatureCodes.EmailReminder).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
        await PackageEntitlementSeeder.SeedAsync(db);
        Assert.Equal(rows.Count, await db.PackageFeatures.CountAsync(f => f.FeatureCode == PackageFeatureCodes.EmailReminder));
    }

    // ---- harness ----------------------------------------------------------------------------

    private static FollowUpReminderJob NewJob(IPRODbContext db, IEmailService email, IPackageEntitlementService entitlements, params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.example.test" };
        foreach (var (key, value) in settings) values[key] = value;
        return new(db, entitlements, email, new ConfigurationBuilder().AddInMemoryCollection(values).Build(), NullLogger<FollowUpReminderJob>.Instance);
    }

    private static async Task<AgentUser> SeedAgentAsync(IPRODbContext db, string timeZone, bool isActive = true)
    {
        var rule = new BillingRule { PackageName = ($"T498-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t498-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Morgan", LastName = "Reid",
            DomainName = ($"t498-{Guid.NewGuid():N}")[..24],
            Country = "Canada", Province = "Ontario",
            PackageId = rule.Id, TimeZone = timeZone, IsActive = isActive
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent;
    }

    private static async Task<Client> SeedClientAsync(IPRODbContext db, int agentId, string first, string last)
    {
        var client = new Client { AgentUserId = agentId, FirstName = first, LastName = last, Email = $"{Guid.NewGuid():N}@example.test" };
        db.Add(client);
        await db.SaveChangesAsync();
        return client;
    }

    private sealed record SentMail(string To, string Subject, string Html, string Text, IDictionary<string, string>? CustomArgs);

    private sealed class RecordingEmailService : IEmailService
    {
        public bool Defer { get; init; }
        public int Calls { get; private set; }
        public List<SentMail> Sent { get; } = new();

        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        {
            Calls++;
            if (Defer) return Task.FromResult(EmailSendResult.Deferred(TimeSpan.FromMinutes(40)));
            Sent.Add(new SentMail(toEmail, subject, htmlBody, textBody ?? string.Empty, customArgs));
            return Task.FromResult(EmailSendResult.Sent($"msg-{Calls}"));
        }

        public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => throw new NotSupportedException();
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => throw new NotSupportedException();
    }

    private sealed class StubEntitlements : IPackageEntitlementService
    {
        public HashSet<int> NotEntitled { get; } = new();
        public HashSet<int> Gated { get; } = new();
        public string? LastFeatureAsked { get; private set; }

        public Task<PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) => throw new NotSupportedException();
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => throw new NotSupportedException();
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode)
        {
            LastFeatureAsked = featureCode;
            return Task.FromResult(agentIds.Distinct().ToDictionary(id => id, id => !NotEntitled.Contains(id)));
        }
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(Gated.Contains(agentId));
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "IPRO.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
