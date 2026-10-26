using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 608 (2026-10-26): "yes, email the client when an appointment moves". The sync's side, end to end against
// a fake Google, is GoogleCalendarTime601Tests.A_clients_appointment_moved_in_google_emails_the_client_once_608.
// These are the notifier's own rules: only a client's live appointment, only a client with an email, never a
// move into the past; and the letter has no hour to give for a bare date.
public class AppointmentMoved608Tests
{
    [Fact]
    public async Task A_moved_appointment_is_written_to_the_client_in_the_scheduled_letters_words()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (_, clientId) = await SeedAsync(db, clientEmail: "pat@example.test");
        var from = DateTime.Today.AddDays(6).AddHours(2);
        var to = DateTime.Today.AddDays(6).AddHours(21);
        var followUpId = await SeedAppointmentAsync(db, clientId, to, PortalAppointmentRequestStatus.Scheduled, notes: "talk about <my> RRSP");

        var email = new PortalBatch598Tests.RecordingEmail();
        await Notifier(db, email).NotifyAsync(followUpId, from, to);

        var sent = Assert.Single(email.Sent);
        Assert.Equal("pat@example.test", sent.To);
        Assert.Equal("Your appointment with Moves Co has moved", sent.Subject);
        Assert.Contains($"Your appointment has moved to <strong>{AppointmentMoveNotifier.When(to)}</strong>. It was {AppointmentMoveNotifier.When(from)}.", sent.Body);
        Assert.Contains("Notes: talk about &lt;my&gt; RRSP", sent.Body);
        Assert.Contains("https://app.example.test/ClientPortalAccount/Login", sent.Body);
    }

    [Fact]
    public async Task Nothing_is_sent_for_an_advisers_own_follow_up_a_finished_or_unscheduled_one_no_email_or_the_past()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (_, clientId) = await SeedAsync(db, clientEmail: "pat@example.test");
        var (_, silentClientId) = await SeedAsync(db, clientEmail: null);
        var ahead = DateTime.Today.AddDays(4).AddHours(10);

        var own = new ClientFollowUp { ClientId = clientId, Title = "Call the bank", DueAt = ahead };
        db.ClientFollowUps.Add(own);
        await db.SaveChangesAsync();
        var finished = await SeedAppointmentAsync(db, clientId, ahead, PortalAppointmentRequestStatus.Scheduled, completed: true);
        var declined = await SeedAppointmentAsync(db, clientId, ahead, PortalAppointmentRequestStatus.Declined);
        var noEmail = await SeedAppointmentAsync(db, silentClientId, ahead, PortalAppointmentRequestStatus.Scheduled);
        var past = DateTime.Today.AddDays(-3).AddHours(10);
        var intoThePast = await SeedAppointmentAsync(db, clientId, past, PortalAppointmentRequestStatus.Scheduled);

        var email = new PortalBatch598Tests.RecordingEmail();
        var notifier = Notifier(db, email);
        await notifier.NotifyAsync(own.Id, ahead.AddHours(-1), ahead);
        await notifier.NotifyAsync(finished, ahead.AddHours(-1), ahead);
        await notifier.NotifyAsync(declined, ahead.AddHours(-1), ahead);
        await notifier.NotifyAsync(noEmail, ahead.AddHours(-1), ahead);
        await notifier.NotifyAsync(intoThePast, past.AddDays(-1), past);

        Assert.Empty(email.Sent);
    }

    [Fact]
    public void A_bare_date_has_no_hour_and_the_web_app_registers_the_notifier()
    {
        // Production words times as the "scheduled" email does, in the server's culture ("2:00 AM" in the owner's
        // screenshots); this machine is en-CA ("2:00 a.m."), so the wording is checked under en-US.
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        try
        {
            Assert.Equal("Thursday, October 29, 2026", AppointmentMoveNotifier.When(new DateTime(2026, 10, 29)));
            Assert.Equal("Thursday, October 29, 2026 at 9:00 PM", AppointmentMoveNotifier.When(new DateTime(2026, 10, 29, 21, 0, 0)));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }

        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var program = File.ReadAllText(Path.Combine(dir!, @"src\IPRO.Web\Program.cs"));
        Assert.Contains("AddScoped<IPRO.Scheduler.IAppointmentMoveNotifier, IPRO.Web.Infrastructure.AppointmentMoveNotifier>()", program);
    }

    // ---------------------------------------------------------------- harness

    private static AppointmentMoveNotifier Notifier(IPRODbContext db, PortalBatch598Tests.RecordingEmail email) =>
        new(db, email, PortalBatch598Tests.Config(), NullLogger<AppointmentMoveNotifier>.Instance);

    private static async Task<(int AgentId, int ClientId)> SeedAsync(IPRODbContext db, string? clientEmail)
    {
        var agent = new AgentUser
        {
            UserName = ($"t608-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Mo", LastName = "Adviser", CompanyName = "Moves Co",
            DomainName = ($"t608-{Guid.NewGuid():N}")[..24],
            TimeZone = "(GMT-05:00) Eastern Time (US & Canada)"
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var client = new Client { AgentUserId = agent.Id, FirstName = "Pat", LastName = "Client", Email = clientEmail ?? string.Empty };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return (agent.Id, client.Id);
    }

    // What PortalRequestsController.Schedule makes: the follow-up, and the request pointing at it.
    private static async Task<int> SeedAppointmentAsync(IPRODbContext db, int clientId, DateTime dueAt, PortalAppointmentRequestStatus status,
        bool completed = false, string? notes = null)
    {
        var followUp = new ClientFollowUp { ClientId = clientId, Title = "Appointment: Pat Client", DueAt = dueAt, IsCompleted = completed };
        db.ClientFollowUps.Add(followUp);
        await db.SaveChangesAsync();
        db.PortalAppointmentRequests.Add(new PortalAppointmentRequest
        {
            ClientId = clientId, Notes = notes, Status = status,
            ScheduledAt = dueAt, RespondedAt = DateTime.UtcNow, ClientFollowUpId = followUp.Id
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return followUp.Id;
    }
}
