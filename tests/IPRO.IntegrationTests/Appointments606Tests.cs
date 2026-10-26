using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 606 (2026-10-26), found in the owner's 601 Google Calendar test. Portal Requests filled the Schedule box
// with the client's preferred day at 12:00 AM; he meant 2:00 PM, typed 2, and the appointment went to the client
// for 2:00 AM. The box now starts at 9:00 AM, says the time in words, and asks before a time from 9:00 PM to
// 7:00 AM. Then he dragged it in Google to 9:00 PM: Upcoming followed, but My Requests kept "Scheduled ... at
// 2:00 AM", because the request's ScheduledAt is the time first given. Both pages now read the follow-up.
public class Appointments606Tests
{
    [Fact]
    public async Task My_Requests_shows_the_appointments_time_now_not_the_time_first_given()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, clientId) = await SeedAsync(db);

        var first = DateTime.Today.AddDays(3).AddHours(2);    // 2:00 AM, as scheduled
        var moved = DateTime.Today.AddDays(3).AddHours(21);   // 9:00 PM, dragged in Google
        var (requestId, followUpId) = await SeedAppointmentAsync(db, clientId, first);
        var followUp = await db.ClientFollowUps.SingleAsync(f => f.Id == followUpId);
        followUp.DueAt = moved;   // what GoogleCalendarSyncJob.ApplyGoogleTime writes for the adviser's move
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = PortalBatch598Tests.AsClient(PortalBatch598Tests.Appointments(db), clientId, agentId);
        await controller.Index();

        var times = Assert.IsType<Dictionary<int, DateTime>>(controller.ViewData["AppointmentTimes"]);
        Assert.Equal(moved, times[requestId]);
        var request = await db.PortalAppointmentRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
        Assert.Equal(first, request.ScheduledAt);                          // the stored first time is left as it was
        Assert.Equal(moved, PortalAppointmentTimes.For(request, times));   // the badge reads the time now
    }

    [Fact]
    public async Task Without_its_follow_up_a_request_shows_the_time_first_given_and_only_its_own_clients_follow_up_counts()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (_, clientId) = await SeedAsync(db);
        var (_, otherClientId) = await SeedAsync(db);

        var when = DateTime.Today.AddDays(5).AddHours(10);
        var (goneId, goneFollowUpId) = await SeedAppointmentAsync(db, clientId, when);
        db.ClientFollowUps.Remove(await db.ClientFollowUps.SingleAsync(f => f.Id == goneFollowUpId));
        var someoneElses = new ClientFollowUp { ClientId = otherClientId, Title = "Appointment: someone else", DueAt = when.AddHours(5) };
        db.ClientFollowUps.Add(someoneElses);
        await db.SaveChangesAsync();
        var crossed = new PortalAppointmentRequest
        {
            ClientId = clientId, Status = PortalAppointmentRequestStatus.Scheduled, ScheduledAt = when, ClientFollowUpId = someoneElses.Id
        };
        db.PortalAppointmentRequests.Add(crossed);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var requests = await db.PortalAppointmentRequests.AsNoTracking().Where(r => r.ClientId == clientId).ToListAsync();
        var times = await PortalAppointmentTimes.CurrentAsync(db, requests);

        Assert.Empty(times);
        Assert.All(requests, r => Assert.Equal(when, PortalAppointmentTimes.For(r, times)));
        Assert.Contains(requests, r => r.Id == goneId);
    }

    [Fact]
    public void Both_pages_say_the_time_now_and_the_schedule_box_starts_at_nine_and_asks_about_night_hours()
    {
        var root = RepoRoot();
        var adviserView = File.ReadAllText(Path.Combine(root, @"src\IPRO.Web\Views\PortalRequests\Index.cshtml"));
        var clientView = File.ReadAllText(Path.Combine(root, @"src\IPRO.Web\Views\ClientPortalAppointments\Index.cshtml"));
        var adviserController = File.ReadAllText(Path.Combine(root, @"src\IPRO.Web\Controllers\PortalRequestsController.cs"));

        // The box starts at 9:00 AM on the preferred day, no longer at midnight.
        Assert.Contains("request.PreferredDate?.Date.AddHours(9).ToString(\"yyyy-MM-ddTHH:mm\")", adviserView);
        Assert.DoesNotContain("request.PreferredDate?.ToString(\"yyyy-MM-ddTHH:mm\")", adviserView);
        // The time in words under the box, and a question before 9:00 PM to 7:00 AM goes to the client.
        Assert.Contains("data-schedule-when", adviserView);
        Assert.Contains("return minutes < 7 * 60 || minutes > 21 * 60;", adviserView);
        Assert.Contains("style=\"width:0;min-width:100%;\" data-schedule-when", adviserView);   // wraps, never widens the row
        Assert.Contains("the client is emailed this time", adviserView);
        Assert.Contains("nonce=\"@Context.GetCspNonce()\"", adviserView);

        // "Scheduled ... at" reads the appointment's time now on both pages.
        Assert.Contains("PortalAppointmentTimes.CurrentAsync(_db, requests)", adviserController);
        Assert.Contains("PortalAppointmentTimes.For(request, appointmentTimes)", adviserView);
        Assert.Contains("PortalAppointmentTimes.For(r, appointmentTimes)", clientView);
        Assert.DoesNotContain("r.ScheduledAt.Value.ToString", clientView);
        Assert.DoesNotContain("request.ScheduledAt.Value.ToString", adviserView);
    }

    // ---------------------------------------------------------------- harness

    private static async Task<(int AgentId, int ClientId)> SeedAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T606-{Guid.NewGuid():N}")[..20], MonthlyPrice = 90m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t606-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Ada", LastName = "Adviser", CompanyName = "Appointments Co",
            DomainName = ($"t606-{Guid.NewGuid():N}")[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var client = new Client { AgentUserId = agent.Id, FirstName = "Pat", LastName = "Client", Email = $"client-{Guid.NewGuid():N}@example.test" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return (agent.Id, client.Id);
    }

    // What PortalRequestsController.Schedule makes: the follow-up, and the request pointing at it.
    private static async Task<(int RequestId, int FollowUpId)> SeedAppointmentAsync(IPRODbContext db, int clientId, DateTime when)
    {
        var followUp = new ClientFollowUp { ClientId = clientId, Title = "Appointment: Pat Client", DueAt = when };
        db.ClientFollowUps.Add(followUp);
        await db.SaveChangesAsync();
        var request = new PortalAppointmentRequest
        {
            ClientId = clientId, Notes = "test 601", Status = PortalAppointmentRequestStatus.Scheduled,
            ScheduledAt = when, RespondedAt = DateTime.UtcNow, ClientFollowUpId = followUp.Id
        };
        db.PortalAppointmentRequests.Add(request);
        await db.SaveChangesAsync();
        return (request.Id, followUp.Id);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }
}
