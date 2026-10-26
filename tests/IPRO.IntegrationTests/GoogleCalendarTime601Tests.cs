using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Scheduler;
using IPRO.Utility;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// 601 (2026-10-24). A follow-up's DueAt is the adviser's own wall clock (INVARIANTS rule 10), and the Google
// sync sent it, and read Google's answer back, as UTC: a 2 p.m. appointment landed in a Toronto calendar at
// 10 a.m. (9 a.m. in winter), a follow-up due 24 October showed on the evening of the 23rd, and an event moved
// to 10:30 in Google came back as 2:30 or 3:30 in the afternoon. These run the REAL GoogleCalendarService and
// the REAL sync job against a fake Google -- an HttpMessageHandler that keeps the events the way Google's API
// does, PATCH merging included -- and a real MySQL database. No call leaves the machine.
public class GoogleCalendarTime601Tests
{
    private const string Toronto = "(GMT-05:00) Eastern Time (US & Canada)";
    private const string Vancouver = "(GMT-08:00) Pacific Time (US & Canada)";

    // ---- push: iPro -> Google ---------------------------------------------------------------

    [Fact]
    public async Task A_timed_follow_up_goes_to_google_at_the_advisers_own_time_with_the_offset_of_its_date()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAsync(db, Toronto);
        var summer = await AddFollowUpAsync(db, seed, "Appointment: Summer Client", new DateTime(2026, 10, 28, 14, 0, 0));
        var lastSummerDay = await AddFollowUpAsync(db, seed, "Review the day before the change", new DateTime(2026, 10, 31, 9, 0, 0));
        var changeDay = await AddFollowUpAsync(db, seed, "Review on the change day", new DateTime(2026, 11, 1, 9, 0, 0));
        var winter = await AddFollowUpAsync(db, seed, "Appointment: Winter Client", new DateTime(2026, 11, 2, 9, 0, 0));

        await RunJobAsync(db, seed);

        // Clocks go back at 2 a.m. on Sunday 1 November 2026: before it Toronto is UTC-4, from it UTC-5. The
        // event carries the adviser's wall clock, the offset of THAT date and the zone, as Google's own examples do.
        AssertTimed(seed.Google.Created["Appointment: Summer Client"], "2026-10-28T14:00:00-04:00", "2026-10-28T15:00:00-04:00", "America/Toronto");
        AssertTimed(seed.Google.Created["Review the day before the change"], "2026-10-31T09:00:00-04:00", "2026-10-31T10:00:00-04:00", "America/Toronto");
        AssertTimed(seed.Google.Created["Review on the change day"], "2026-11-01T09:00:00-05:00", "2026-11-01T10:00:00-05:00", "America/Toronto");
        AssertTimed(seed.Google.Created["Appointment: Winter Client"], "2026-11-02T09:00:00-05:00", "2026-11-02T10:00:00-05:00", "America/Toronto");

        // The same run reads its own events back (Google lists what changed): nothing moves, nothing is re-written.
        Assert.Equal(new DateTime(2026, 10, 28, 14, 0, 0), (await ReloadAsync(db, summer)).DueAt);
        Assert.Equal(new DateTime(2026, 10, 31, 9, 0, 0), (await ReloadAsync(db, lastSummerDay)).DueAt);
        Assert.Equal(new DateTime(2026, 11, 1, 9, 0, 0), (await ReloadAsync(db, changeDay)).DueAt);
        Assert.Equal(new DateTime(2026, 11, 2, 9, 0, 0), (await ReloadAsync(db, winter)).DueAt);
        Assert.Empty(seed.Google.Patches);

        // And again on the next run, from the stored sync token.
        await RunJobAsync(db, seed);
        Assert.Equal(new DateTime(2026, 11, 2, 9, 0, 0), (await ReloadAsync(db, winter)).DueAt);
        Assert.Equal(4, seed.Google.Created.Count);
        Assert.Empty(seed.Google.Patches);
    }

    [Fact]
    public async Task A_follow_up_with_only_a_date_becomes_an_all_day_event_on_that_date()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        // No zone chosen on Profile: the product's default, Eastern.
        var seed = await SeedAsync(db, agentTimeZone: "");
        var due = await AddFollowUpAsync(db, seed, "Call about the renewal", new DateTime(2026, 10, 24));
        var changeDay = await AddFollowUpAsync(db, seed, "Birthday: Pat Client", new DateTime(2026, 11, 1));

        await RunJobAsync(db, seed);

        // The follow-up form posts a bare date, and the product's rule is that midnight means "no time"
        // (FollowUpReminderJob says "today", not "12:00 AM"). Google gets an all-day event on THAT date, not
        // the evening before (8 p.m. on the 23rd, what UTC midnight is in Toronto).
        AssertAllDay(seed.Google.Created["Call about the renewal"], "2026-10-24", "2026-10-25");
        AssertAllDay(seed.Google.Created["Birthday: Pat Client"], "2026-11-01", "2026-11-02");

        Assert.Equal(new DateTime(2026, 10, 24), (await ReloadAsync(db, due)).DueAt);
        Assert.Equal(new DateTime(2026, 11, 1), (await ReloadAsync(db, changeDay)).DueAt);
        Assert.Empty(seed.Google.Patches);
    }

    [Fact]
    public async Task An_adviser_in_another_zone_gets_their_own_zone()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAsync(db, Vancouver);
        var summer = await AddFollowUpAsync(db, seed, "Appointment: West Coast", new DateTime(2026, 10, 28, 14, 0, 0));
        await AddFollowUpAsync(db, seed, "Appointment: West Coast winter", new DateTime(2026, 11, 2, 9, 30, 0));

        await RunJobAsync(db, seed);

        AssertTimed(seed.Google.Created["Appointment: West Coast"], "2026-10-28T14:00:00-07:00", "2026-10-28T15:00:00-07:00", "America/Los_Angeles");
        AssertTimed(seed.Google.Created["Appointment: West Coast winter"], "2026-11-02T09:30:00-08:00", "2026-11-02T10:30:00-08:00", "America/Los_Angeles");
        Assert.Equal(new DateTime(2026, 10, 28, 14, 0, 0), (await ReloadAsync(db, summer)).DueAt);
    }

    // ---- pull: Google -> iPro ---------------------------------------------------------------

    [Fact]
    public async Task An_event_moved_in_google_comes_back_at_the_advisers_wall_clock()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAsync(db, Toronto);
        // Events the sync wrote after 601, then moved by the adviser in Google. Google answers with the offset of
        // the event's own zone; an event can also come back in UTC ("Z") -- the instant is what counts.
        var withinSummer = await AddFollowUpAsync(db, seed, "Moved within summer time", new DateTime(2026, 10, 28, 14, 0, 0), "g-1");
        seed.Google.HoldTimed("g-1", "Moved within summer time", "2026-10-29T16:00:00-04:00", "2026-10-29T17:00:00-04:00", "America/Toronto");
        var acrossTheChange = await AddFollowUpAsync(db, seed, "Moved past the change", new DateTime(2026, 10, 30, 9, 0, 0), "g-2");
        seed.Google.HoldTimed("g-2", "Moved past the change", "2026-11-03T10:30:00-05:00", "2026-11-03T11:30:00-05:00", "America/Toronto");
        var allDay = await AddFollowUpAsync(db, seed, "Moved to another day", new DateTime(2026, 10, 26), "g-3");
        seed.Google.HoldAllDay("g-3", "Moved to another day", "2026-10-27", "2026-10-28");
        var inUtc = await AddFollowUpAsync(db, seed, "Answered in UTC", new DateTime(2026, 11, 4, 9, 0, 0), "g-4");
        seed.Google.HoldTimed("g-4", "Answered in UTC", "2026-11-04T15:00:00Z", "2026-11-04T16:00:00Z", "America/Toronto");

        await RunJobAsync(db, seed);

        Assert.Equal(new DateTime(2026, 10, 29, 16, 0, 0), (await ReloadAsync(db, withinSummer)).DueAt);
        Assert.Equal(new DateTime(2026, 11, 3, 10, 30, 0), (await ReloadAsync(db, acrossTheChange)).DueAt);
        Assert.Equal(new DateTime(2026, 10, 27), (await ReloadAsync(db, allDay)).DueAt);
        Assert.Equal(new DateTime(2026, 11, 4, 10, 0, 0), (await ReloadAsync(db, inUtc)).DueAt);
        Assert.Empty(seed.Google.Patches);   // the adviser's move is the truth; nothing is pushed back
    }

    [Fact]
    public async Task An_event_written_before_601_is_put_right_in_google_and_never_moves_the_follow_up()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        // A connection that has been syncing under the old code: it holds a sync token, so Google would list only
        // what changes from now on and the old events would never be seen again.
        var seed = await SeedAsync(db, Toronto, syncToken: "token-from-before-601");

        // What the old code left in Google: DueAt written as if it were UTC, with timeZone "UTC".
        var appointment = await AddFollowUpAsync(db, seed, "Appointment: Old Client", new DateTime(2026, 10, 28, 14, 0, 0), "g-old-1");
        seed.Google.HoldTimed("g-old-1", "Appointment: Old Client", "2026-10-28T14:00:00Z", "2026-10-28T15:00:00Z", "UTC");
        var dateOnly = await AddFollowUpAsync(db, seed, "Old date-only follow-up", new DateTime(2026, 10, 24), "g-old-2");
        seed.Google.HoldTimed("g-old-2", "Old date-only follow-up", "2026-10-24T00:00:00Z", "2026-10-24T01:00:00Z", "UTC");
        var winterHalfHour = await AddFollowUpAsync(db, seed, "Old winter half hour", new DateTime(2026, 11, 5, 10, 0, 0), "g-old-3");
        seed.Google.HoldTimed("g-old-3", "Old winter half hour", "2026-11-05T10:00:00Z", "2026-11-05T10:30:00Z", "UTC");
        // An old event the adviser has since dragged to 3 p.m. in Google: no longer where the old code put it,
        // so it is their move, read at their wall clock.
        var draggedByAdviser = await AddFollowUpAsync(db, seed, "Old event moved by the adviser", new DateTime(2026, 10, 29, 13, 0, 0), "g-old-4");
        seed.Google.HoldTimed("g-old-4", "Old event moved by the adviser", "2026-10-29T19:00:00Z", "2026-10-29T20:00:00Z", "UTC");

        await RunJobAsync(db, seed);

        // The first run after the release reads the calendar in full, whatever token it held.
        Assert.DoesNotContain("syncToken", seed.Google.ListQueries[0]);

        // The stored DueAt is the truth: the follow-ups do not move...
        Assert.Equal(new DateTime(2026, 10, 28, 14, 0, 0), (await ReloadAsync(db, appointment)).DueAt);
        Assert.Equal(new DateTime(2026, 10, 24), (await ReloadAsync(db, dateOnly)).DueAt);
        Assert.Equal(new DateTime(2026, 11, 5, 10, 0, 0), (await ReloadAsync(db, winterHalfHour)).DueAt);
        Assert.Equal(new DateTime(2026, 10, 29, 15, 0, 0), (await ReloadAsync(db, draggedByAdviser)).DueAt);

        // ...and Google's copies are re-written where they belong.
        var held = seed.Google.Events;
        AssertTimed(held["g-old-1"], "2026-10-28T14:00:00-04:00", "2026-10-28T15:00:00-04:00", "America/Toronto");
        Assert.Equal("Appointment: Old Client", held["g-old-1"]["summary"]!.GetValue<string>());
        // A date-only one becomes all-day on its date. PATCH merges, so the old dateTime and timeZone must be
        // gone, not left beside the new date.
        Assert.Equal("""{"date":"2026-10-24"}""", held["g-old-2"]["start"]!.ToJsonString());
        Assert.Equal("""{"date":"2026-10-25"}""", held["g-old-2"]["end"]!.ToJsonString());
        // The length the event had is kept.
        AssertTimed(held["g-old-3"], "2026-11-05T10:00:00-05:00", "2026-11-05T10:30:00-05:00", "America/Toronto");
        Assert.DoesNotContain(seed.Google.Patches, p => p.EventId == "g-old-4");
        Assert.Equal(3, seed.Google.Patches.Count);

        // The next run is incremental again and finds nothing to correct: the re-written events read back exactly.
        await RunJobAsync(db, seed);
        Assert.Contains("syncToken=tok-1", seed.Google.ListQueries[1]);
        Assert.Equal(3, seed.Google.Patches.Count);
        Assert.Equal(new DateTime(2026, 10, 28, 14, 0, 0), (await ReloadAsync(db, appointment)).DueAt);
        Assert.Equal(new DateTime(2026, 10, 24), (await ReloadAsync(db, dateOnly)).DueAt);
        Assert.Equal(new DateTime(2026, 10, 29, 15, 0, 0), (await ReloadAsync(db, draggedByAdviser)).DueAt);
    }

    [Fact]
    public async Task A_google_event_of_the_advisers_own_shows_on_the_day_it_happens_where_they_are()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAsync(db, Toronto);
        // The Calendar page puts these copies on the day of StartAt.Date, the adviser's day. A 9 p.m. dinner is
        // 1 a.m. the next day in UTC, and showed on the wrong day.
        seed.Google.HoldTimed("g-own-1", "Dinner", "2026-10-30T21:00:00-04:00", "2026-10-30T22:00:00-04:00", "America/Toronto");
        seed.Google.HoldAllDay("g-own-2", "Conference", "2026-11-06", "2026-11-07");

        await RunJobAsync(db, seed);

        var dinner = await db.ExternalCalendarEvents.AsNoTracking().SingleAsync(e => e.GoogleEventId == "g-own-1");
        Assert.Equal(new DateTime(2026, 10, 30, 21, 0, 0), dinner.StartAt);
        Assert.Equal(new DateTime(2026, 10, 30, 22, 0, 0), dinner.EndAt);
        var conference = await db.ExternalCalendarEvents.AsNoTracking().SingleAsync(e => e.GoogleEventId == "g-own-2");
        Assert.Equal(new DateTime(2026, 11, 6), conference.StartAt);
    }

    [Fact]
    public void Production_gets_the_resync_column()
    {
        Assert.Contains("LocalTimesResyncedAt", File.ReadAllText(FindRepoFile(Path.Combine("src", "IPRO.DataAccess", "StartupSchemaRepair.cs"))));
    }

    // ---- 608: a client's appointment moved in Google, the client is told ------------------------------

    [Fact]
    public async Task A_clients_appointment_moved_in_google_emails_the_client_once_608()
    {
        // The owner, 2026-10-26, after dragging his test appointment from 2:00 AM to 9:00 PM: "yes, email the
        // client when an appointment moves". The real sync, the real notifier, the fake Google; dates ahead of
        // today so the move is never "into the past".
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAsync(db, Toronto);
        var toronto = AgentLocalTime.Zone(Toronto);
        string Utc(DateTime wall) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), toronto).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

        var first = DateTime.Today.AddDays(10).AddHours(2);    // scheduled for 2:00 AM by mistake
        var moved = DateTime.Today.AddDays(10).AddHours(21);   // dragged in Google to 9:00 PM
        var appointment = await AddFollowUpAsync(db, seed, "Appointment: Cal Client", first, "g-appt");
        db.PortalAppointmentRequests.Add(new PortalAppointmentRequest
        {
            ClientId = seed.ClientId, Notes = "test 601", Status = PortalAppointmentRequestStatus.Scheduled,
            ScheduledAt = first, RespondedAt = DateTime.UtcNow, ClientFollowUpId = appointment
        });
        await db.SaveChangesAsync();
        seed.Google.HoldTimed("g-appt", "Appointment: Cal Client", Utc(moved), Utc(moved.AddHours(1)), "America/Toronto");
        // The adviser's own follow-up moved too: theirs alone, nobody is written to.
        var ownTask = await AddFollowUpAsync(db, seed, "Call the bank", DateTime.Today.AddDays(11).AddHours(9), "g-own-task");
        seed.Google.HoldTimed("g-own-task", "Call the bank", Utc(DateTime.Today.AddDays(11).AddHours(15)), Utc(DateTime.Today.AddDays(11).AddHours(16)), "America/Toronto");
        // An appointment Google still shows where iPro has it: no move, no email.
        var untouched = await AddFollowUpAsync(db, seed, "Appointment: Cal Client (2)", DateTime.Today.AddDays(12).AddHours(10), "g-still");
        db.PortalAppointmentRequests.Add(new PortalAppointmentRequest
        {
            ClientId = seed.ClientId, Status = PortalAppointmentRequestStatus.Scheduled,
            ScheduledAt = DateTime.Today.AddDays(12).AddHours(10), RespondedAt = DateTime.UtcNow, ClientFollowUpId = untouched
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        seed.Google.HoldTimed("g-still", "Appointment: Cal Client (2)", Utc(DateTime.Today.AddDays(12).AddHours(10)), Utc(DateTime.Today.AddDays(12).AddHours(11)), "America/Toronto");

        var email = new PortalBatch598Tests.RecordingEmail();
        var notifier = new IPRO.Web.Infrastructure.AppointmentMoveNotifier(db, email, PortalBatch598Tests.Config(),
            new ErrorLog<IPRO.Web.Infrastructure.AppointmentMoveNotifier>(seed.Errors));
        await RunJobAsync(db, seed, notifier);

        Assert.Equal(moved, (await ReloadAsync(db, appointment)).DueAt);
        var client = await db.Clients.AsNoTracking().SingleAsync(c => c.Id == seed.ClientId);
        var sent = Assert.Single(email.Sent);
        Assert.Equal(client.Email, sent.To);
        Assert.Contains("has moved", sent.Subject);
        Assert.Contains($"moved to <strong>{IPRO.Web.Infrastructure.AppointmentMoveNotifier.When(moved)}</strong>", sent.Body);
        Assert.Contains($"It was {IPRO.Web.Infrastructure.AppointmentMoveNotifier.When(first)}.", sent.Body);
        Assert.Contains("/ClientPortalAccount/Login", sent.Body);

        // The next run finds nothing new in Google: no second letter.
        await RunJobAsync(db, seed, notifier);
        Assert.Single(email.Sent);
    }

    // ---- harness ----------------------------------------------------------------------------

    private sealed record Seed(int AgentId, int ClientId, FakeGoogle Google, IDataProtectionProvider Protection, List<string> Errors);

    private static async Task<Seed> SeedAsync(IPRODbContext db, string agentTimeZone, string? syncToken = null)
    {
        var agent = new AgentUser
        {
            UserName = $"g601-{Guid.NewGuid():N}"[..20],
            Email = $"g601-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Cal", LastName = "Adviser",
            DomainName = $"g601-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario",
            TimeZone = agentTimeZone
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var client = new Client { AgentUserId = agent.Id, FirstName = "Cal", LastName = "Client", Email = $"cal-{Guid.NewGuid():N}"[..12] + "@example.test" };
        db.Add(client);
        await db.SaveChangesAsync();

        var provider = new EphemeralDataProtectionProvider();
        var tokens = provider.CreateProtector("IPRO.Web.GoogleCalendar.Tokens.v1");
        db.Add(new GoogleCalendarConnection
        {
            AgentUserId = agent.Id,
            GoogleAccountEmail = "adviser@example.test",
            EncryptedAccessToken = tokens.Protect("access-token"),
            EncryptedRefreshToken = tokens.Protect("refresh-token"),
            AccessTokenExpiresAt = DateTime.UtcNow.AddHours(1),
            SyncToken = syncToken
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seed(agent.Id, client.Id, new FakeGoogle(), provider, new List<string>());
    }

    private static async Task<int> AddFollowUpAsync(IPRODbContext db, Seed seed, string title, DateTime dueAt, string? googleEventId = null)
    {
        var followUp = new ClientFollowUp { ClientId = seed.ClientId, Title = title, DueAt = dueAt, GoogleEventId = googleEventId };
        db.Add(followUp);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return followUp.Id;
    }

    private static async Task<ClientFollowUp> ReloadAsync(IPRODbContext db, int id) =>
        await db.ClientFollowUps.AsNoTracking().SingleAsync(f => f.Id == id);

    private static async Task RunJobAsync(IPRODbContext db, Seed seed, IAppointmentMoveNotifier? notifier = null)
    {
        var service = new GoogleCalendarService(
            new HandlerFactory(seed.Google),
            Options.Create(new GoogleCalendarSettings { ClientId = "client-id", ClientSecret = "secret" }),
            new ErrorLog<GoogleCalendarService>(seed.Errors));
        var job = new GoogleCalendarSyncJob(db, service, seed.Protection, new ErrorLog<GoogleCalendarSyncJob>(seed.Errors), notifier);
        await job.RunAsync();
        db.ChangeTracker.Clear();
        Assert.True(seed.Errors.Count == 0, "the sync logged errors:\n" + string.Join("\n", seed.Errors));
    }

    private static void AssertTimed(JsonObject evt, string start, string end, string zone)
    {
        Assert.Equal(start, evt["start"]!["dateTime"]!.GetValue<string>());
        Assert.Equal(zone, evt["start"]!["timeZone"]!.GetValue<string>());
        Assert.Null(evt["start"]!["date"]);
        Assert.Equal(end, evt["end"]!["dateTime"]!.GetValue<string>());
        Assert.Equal(zone, evt["end"]!["timeZone"]!.GetValue<string>());
        Assert.Null(evt["end"]!["date"]);
    }

    private static void AssertAllDay(JsonObject evt, string start, string endExclusive)
    {
        Assert.Equal(start, evt["start"]!["date"]?.GetValue<string>());
        Assert.Null(evt["start"]!["dateTime"]);
        Assert.Equal(endExclusive, evt["end"]!["date"]?.GetValue<string>());
        Assert.Null(evt["end"]!["dateTime"]);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "IPRO.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    // Google Calendar's events API, in memory: insert, patch (named fields replace, nested objects merge, a
    // null removes the field -- Google's patch semantics), list (everything it holds, with a fresh sync token)
    // and delete. Each event is kept as the JSON Google would hand back.
    private sealed class FakeGoogle : HttpMessageHandler
    {
        private int _nextId;
        public Dictionary<string, JsonObject> Events { get; } = new();
        public Dictionary<string, JsonObject> Created { get; } = new();
        public List<(string EventId, JsonObject Body)> Patches { get; } = new();
        public List<string> ListQueries { get; } = new();

        public void HoldTimed(string id, string summary, string start, string end, string zone) =>
            Events[id] = new JsonObject
            {
                ["id"] = id, ["status"] = "confirmed", ["summary"] = summary,
                ["start"] = new JsonObject { ["dateTime"] = start, ["timeZone"] = zone },
                ["end"] = new JsonObject { ["dateTime"] = end, ["timeZone"] = zone }
            };

        public void HoldAllDay(string id, string summary, string start, string endExclusive) =>
            Events[id] = new JsonObject
            {
                ["id"] = id, ["status"] = "confirmed", ["summary"] = summary,
                ["start"] = new JsonObject { ["date"] = start },
                ["end"] = new JsonObject { ["date"] = endExclusive }
            };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            const string marker = "/events";
            var at = path.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) throw new InvalidOperationException($"unexpected Google call {request.Method} {uri}");
            var eventId = path.Length > at + marker.Length ? Uri.UnescapeDataString(path[(at + marker.Length + 1)..]) : null;

            if (request.Method == HttpMethod.Post && eventId == null)
            {
                var sent = JsonNode.Parse(body!)!.AsObject();
                Created[sent["summary"]!.GetValue<string>()] = sent.DeepClone().AsObject();
                var id = $"g-created-{++_nextId}";
                var stored = sent.DeepClone().AsObject();
                stored["id"] = id;
                stored["status"] = "confirmed";
                Events[id] = stored;
                return Json(new JsonObject { ["id"] = id });
            }

            if (request.Method == HttpMethod.Patch && eventId != null)
            {
                var sent = JsonNode.Parse(body!)!.AsObject();
                Patches.Add((eventId, sent.DeepClone().AsObject()));
                if (!Events.TryGetValue(eventId, out var stored)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                Merge(stored, sent);
                return Json(stored);
            }

            if (request.Method == HttpMethod.Get && eventId == null)
            {
                ListQueries.Add(uri.Query);
                var items = new JsonArray(Events.Values.Select(e => (JsonNode?)e.DeepClone()).ToArray());
                return Json(new JsonObject { ["items"] = items, ["nextSyncToken"] = $"tok-{ListQueries.Count}" });
            }

            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
            throw new InvalidOperationException($"unexpected Google call {request.Method} {uri}");
        }

        private static void Merge(JsonObject target, JsonObject patch)
        {
            foreach (var (key, value) in patch.ToList())
            {
                if (value is null) { target.Remove(key); continue; }
                if (value is JsonObject child && target[key] is JsonObject existing) { Merge(existing, child); continue; }
                target[key] = value.DeepClone();
            }
        }

        private static HttpResponseMessage Json(JsonNode node) =>
            new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private sealed class HandlerFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public HandlerFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    // The job catches every per-connection failure and only logs it, so a broken sync would otherwise pass as
    // "nothing happened". Errors land here and fail the test with their text.
    private sealed class ErrorLog<T> : ILogger<T>
    {
        private readonly List<string> _sink;
        public ErrorLog(List<string> sink) => _sink = sink;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) _sink.Add($"{formatter(state, exception)} {exception}");
        }
    }
}
