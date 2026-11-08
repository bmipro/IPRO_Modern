using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Scheduler;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 650 (634's first). The birthday and life-event reminder job read the 500 rows it had checked longest ago,
// platform-wide, and checked only those; a reminder's window is a week, so above about 4,000 clients with a
// birth date reminders were silently missed. Now every active row is looked at every day and only the due
// ones are changed. These tests use more rows than one batch and put the due ones last, where the rotation
// would never have reached them on the day.
public class ReminderSweep650Tests
{
    private static readonly DateTime Today = new(2026, 11, 8);

    [Fact]
    public async Task Every_due_birthday_gets_its_reminder_however_many_clients_there_are()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        int agentId;
        await using (var db = testDb.CreateContext())
        {
            agentId = await SeedAgentAsync(db);
            // 1,150 clients whose birthdays are months away (more than a batch, more than the old 500), then 3 due.
            db.Clients.AddRange(Enumerable.Range(0, 1150).Select(i => Client(agentId, $"Far{i}", new DateTime(1970, 5, 1 + i % 28))));
            await db.SaveChangesAsync();
            db.Clients.Add(Client(agentId, "InThreeDays", new DateTime(1980, 11, 11)));
            db.Clients.Add(Client(agentId, "Today", new DateTime(1990, 11, 8)));
            db.Clients.Add(Client(agentId, "InSevenDays", new DateTime(1985, 11, 15)));
            db.Clients.Add(Client(agentId, "InEightDays", new DateTime(1985, 11, 16)));   // outside the week: not yet
            await db.SaveChangesAsync();
        }

        await RunAsync(testDb, Today);

        await using (var db = testDb.CreateContext())
        {
            var made = await db.ClientFollowUps.AsNoTracking().Include(f => f.Client).OrderBy(f => f.DueAt).ToListAsync();
            Assert.Equal(new[] { "Today", "InThreeDays", "InSevenDays" }, made.Select(f => f.Client.FirstName));
            Assert.Equal(new[] { new DateTime(2026, 11, 8), new DateTime(2026, 11, 11), new DateTime(2026, 11, 15) }, made.Select(f => f.DueAt));
            Assert.All(made, f => Assert.StartsWith("\U0001F382 Birthday: ", f.Title));
            Assert.Equal(3, await db.Clients.CountAsync(c => c.LastBirthdayReminderYear == 2026));
            // The rows that were not due are not written at all.
            Assert.Equal(0, await db.Clients.CountAsync(c => c.BirthdayReminderLastCheckedAt != null));
        }

        // The same day again, and the next day: nothing twice; the eighth-day client's turn comes the next day.
        await RunAsync(testDb, Today);
        await RunAsync(testDb, Today.AddDays(1));
        await using (var db = testDb.CreateContext())
        {
            Assert.Equal(4, await db.ClientFollowUps.CountAsync());
            Assert.Equal(1, await db.ClientFollowUps.CountAsync(f => f.Client.FirstName == "InEightDays"));
        }
    }

    [Fact]
    public async Task Every_due_life_event_gets_its_reminder_by_its_own_lead_time_and_an_orphan_stops_nothing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        int clientId;
        await using (var db = testDb.CreateContext())
        {
            var agentId = await SeedAgentAsync(db);
            var client = Client(agentId, "Avery", null);
            db.Clients.Add(client);
            await db.SaveChangesAsync();
            clientId = client.Id;
            db.ClientLifeEvents.AddRange(Enumerable.Range(0, 1050).Select(i => LifeEvent(clientId, $"Far{i}", new DateTime(2015, 4, 1 + i % 28), 7)));
            db.ClientLifeEvents.Add(LifeEvent(clientId, "Anniversary", new DateTime(2010, 11, 30), 30));      // 22 days off, 30-day lead: due
            db.ClientLifeEvents.Add(LifeEvent(clientId, "Policy renewal", new DateTime(2020, 11, 30), 7));    // 22 days off, 7-day lead: not yet
            db.ClientLifeEvents.Add(LifeEvent(clientId, "Retired", new DateTime(2001, 11, 9), 7, isActive: false));
            await db.SaveChangesAsync();

            // A life event whose client was deleted: the production table has no foreign key, so it can exist.
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SET FOREIGN_KEY_CHECKS = 0");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO `ClientLifeEvents` (`ClientId`, `EventType`, `Label`, `EventDate`, `ReminderDaysBefore`, `IsActive`, `CreatedAt`) " +
                "VALUES (987654, 0, 'Orphan', '2000-11-09', 7, 1, UTC_TIMESTAMP(6))");
            await db.Database.ExecuteSqlRawAsync("SET FOREIGN_KEY_CHECKS = 1");
            await db.Database.CloseConnectionAsync();
        }

        await RunAsync(testDb, Today);

        await using (var db = testDb.CreateContext())
        {
            var made = await db.ClientFollowUps.AsNoTracking().ToListAsync();
            Assert.Equal("Anniversary: Avery Reid", Assert.Single(made).Title);
            Assert.Equal(new DateTime(2026, 11, 30), made[0].DueAt);
            Assert.Equal(2026, (await db.ClientLifeEvents.AsNoTracking().SingleAsync(e => e.Label == "Anniversary")).LastReminderYear);
            Assert.Null((await db.ClientLifeEvents.AsNoTracking().SingleAsync(e => e.Label == "Policy renewal")).LastReminderYear);
        }
    }

    [Fact]
    public async Task An_adviser_without_the_feature_gets_none_and_stays_due()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        int agentId;
        await using (var db = testDb.CreateContext())
        {
            agentId = await SeedAgentAsync(db);
            db.Clients.Add(Client(agentId, "Blair", new DateTime(1975, 11, 10)));
            await db.SaveChangesAsync();
        }
        var plan = new Plan();
        plan.NotEntitled.Add(agentId);

        await RunAsync(testDb, Today, plan);
        await using (var db = testDb.CreateContext())
        {
            Assert.Equal(0, await db.ClientFollowUps.CountAsync());
            Assert.Null((await db.Clients.AsNoTracking().SingleAsync()).LastBirthdayReminderYear);
        }
        Assert.Equal(PackageFeatureCodes.LifeEventReminders, plan.LastFeatureAsked);

        // Entitled again before the day: the reminder comes.
        await RunAsync(testDb, Today.AddDays(1));
        await using (var db = testDb.CreateContext())
            Assert.Equal(1, await db.ClientFollowUps.CountAsync());
    }

    [Theory]
    [InlineData("2026-12-28", "1980-01-02", true, "2027-01-02")]   // over the year end
    [InlineData("2026-12-28", "1980-01-05", false, "2027-01-05")]  // eight days on
    [InlineData("2027-02-22", "1984-02-29", true, "2027-02-28")]   // 29 February in a year without one
    [InlineData("2028-02-22", "1984-02-29", true, "2028-02-29")]
    [InlineData("2026-11-08", "1990-11-07", false, "2027-11-07")]  // yesterday: next year's, far off
    public void The_decision_is_the_one_the_job_always_made(string today, string date, bool due, string next)
    {
        var day = DateTime.Parse(today);
        Assert.Equal(DateTime.Parse(next), ClientLifeEventReminderJob.NextOccurrence(DateTime.Parse(date), day));
        Assert.Equal(due, ClientLifeEventReminderJob.IsDue(DateTime.Parse(date), 7, null, day));
        Assert.False(ClientLifeEventReminderJob.IsDue(DateTime.Parse(date), 7, DateTime.Parse(next).Year, day));   // reminded already
    }

    [Fact]
    public void The_rotation_is_gone()
    {
        var job = File.ReadAllText(FindRepoFile(@"src\IPRO.Scheduler\ClientLifeEventReminderJob.cs"));
        Assert.DoesNotContain("Take(500)", job);
        Assert.DoesNotContain("LastCheckedAt = DateTime.UtcNow", job);
        Assert.Contains("public Task RunAsync() => RunForDayAsync(DateTime.UtcNow.Date);", job);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static async Task RunAsync(TestDatabase testDb, DateTime today, Plan? plan = null)
    {
        await using var db = testDb.CreateContext();
        await new ClientLifeEventReminderJob(db, plan ?? new Plan(), NullLogger<ClientLifeEventReminderJob>.Instance).RunForDayAsync(today);
    }

    private static Client Client(int agentId, string firstName, DateTime? birth) => new()
    {
        AgentUserId = agentId,
        FirstName = firstName,
        LastName = "Reid",
        Email = $"{Guid.NewGuid():N}@example.test",
        DateOfBirth = birth
    };

    private static ClientLifeEvent LifeEvent(int clientId, string label, DateTime date, int daysBefore, bool isActive = true) => new()
    {
        ClientId = clientId,
        Label = label,
        EventDate = date,
        ReminderDaysBefore = daysBefore,
        IsActive = isActive
    };

    private static async Task<int> SeedAgentAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T650-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t650-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Morgan", LastName = "Reid",
            DomainName = ($"t650-{Guid.NewGuid():N}")[..24],
            Country = "Canada", Province = "Ontario",
            PackageId = rule.Id, IsActive = true
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private sealed class Plan : IPackageEntitlementService
    {
        public HashSet<int> NotEntitled { get; } = new();
        public string? LastFeatureAsked { get; private set; }
        public Task<PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) => throw new NotSupportedException();
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => throw new NotSupportedException();
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode)
        {
            LastFeatureAsked = featureCode;
            var answer = agentIds.Distinct().ToDictionary(id => id, id => !NotEntitled.Contains(id));
            NotEntitled.Clear();   // entitled again from the next run on
            return Task.FromResult(answer);
        }
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(false);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "IPRO.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
