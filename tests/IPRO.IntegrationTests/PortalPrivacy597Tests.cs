using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 597 (2026-10-24). Reading the client portal end to end for the owner's System Book found two
// things a client could see that were the adviser's: the Appointments page's "Upcoming" list showed
// every open follow-up on the client's record by its title (private to-dos, life-event reminders),
// and the Invoices tab listed drafts the adviser had not sent, with a link that opened them.
// Upcoming is now the client's own scheduled appointments; Invoices only what was sent.
public class PortalPrivacy597Tests
{
    [Fact]
    public async Task Upcoming_lists_only_the_clients_own_scheduled_appointments_never_the_advisers_follow_ups()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var clientId = await SeedClientAsync(db, agentId);
        var otherClientId = await SeedClientAsync(db, agentId);

        var due = DateTime.UtcNow.Date.AddDays(3).AddHours(14);
        await SeedAppointmentAsync(db, clientId, due, "talk about my RRSP", completed: false);
        await SeedAppointmentAsync(db, clientId, DateTime.UtcNow.Date.AddDays(-2), "the old one", completed: true);
        await SeedAppointmentAsync(db, otherClientId, DateTime.UtcNow.Date.AddDays(1), "someone else's", completed: false);
        db.ClientFollowUps.Add(new ClientFollowUp { ClientId = clientId, Title = "PRIVATE estate review", Notes = "adviser only", DueAt = DateTime.UtcNow.Date.AddDays(1) });
        db.ClientFollowUps.Add(new ClientFollowUp { ClientId = clientId, Title = "Birthday: send a card", DueAt = DateTime.UtcNow.Date.AddDays(5) });
        db.PortalAppointmentRequests.Add(new PortalAppointmentRequest { ClientId = clientId, Notes = "still pending", Status = PortalAppointmentRequestStatus.Pending });
        db.PortalAppointmentRequests.Add(new PortalAppointmentRequest { ClientId = clientId, Notes = "was declined", Status = PortalAppointmentRequestStatus.Declined });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = AsClient(PortalBatch598Tests.Appointments(db), clientId);
        await controller.Index();

        var upcoming = Assert.IsType<List<PortalUpcomingAppointment>>(controller.ViewData["UpcomingAppointments"]);
        var only = Assert.Single(upcoming);
        Assert.Equal(due, only.When);
        Assert.Equal("talk about my RRSP", only.ClientNotes);
    }

    [Fact]
    public async Task An_appointment_the_adviser_marked_done_leaves_the_upcoming_list()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var clientId = await SeedClientAsync(db, agentId);
        var followUpId = await SeedAppointmentAsync(db, clientId, DateTime.UtcNow.Date.AddDays(2), null, completed: false);

        var controller = AsClient(PortalBatch598Tests.Appointments(db), clientId);
        await controller.Index();
        Assert.Single(Assert.IsType<List<PortalUpcomingAppointment>>(controller.ViewData["UpcomingAppointments"]));

        var followUp = await db.ClientFollowUps.FindAsync(followUpId);
        followUp!.IsCompleted = true;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        controller = AsClient(PortalBatch598Tests.Appointments(db), clientId);
        await controller.Index();
        Assert.Empty(Assert.IsType<List<PortalUpcomingAppointment>>(controller.ViewData["UpcomingAppointments"]));
    }

    [Fact]
    public void The_upcoming_line_has_no_room_for_a_follow_up_title_and_the_page_never_reads_one()
    {
        // The model carries the time and the client's own words only.
        var names = typeof(PortalUpcomingAppointment).GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "ClientNotes", "When" }, names);

        var view = File.ReadAllText(Path.Combine(RepoRoot(), "src", "IPRO.Web", "Views", "ClientPortalAppointments", "Index.cshtml"));
        Assert.DoesNotContain("ClientFollowUp", view);
        Assert.DoesNotContain(".Title", view);
    }

    [Fact]
    public async Task Invoices_lists_only_documents_the_adviser_sent()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var clientId = await SeedClientAsync(db, agentId);
        var otherClientId = await SeedClientAsync(db, agentId);

        var sent = DateTime.UtcNow.AddDays(-3);
        await SeedInvoiceAsync(db, agentId, clientId, "SHOW-SENT", ClientInvoiceStatus.Sent, sent);
        await SeedInvoiceAsync(db, agentId, clientId, "SHOW-PAID", ClientInvoiceStatus.Paid, sent);
        await SeedInvoiceAsync(db, agentId, clientId, "SHOW-APPROVED", ClientInvoiceStatus.Approved, sent, ClientInvoiceDocumentType.Estimate);
        await SeedInvoiceAsync(db, agentId, clientId, "SHOW-VOID-AFTER-SENDING", ClientInvoiceStatus.Void, sent);
        await SeedInvoiceAsync(db, agentId, clientId, "HIDE-DRAFT", ClientInvoiceStatus.Draft, null);
        await SeedInvoiceAsync(db, agentId, clientId, "HIDE-DRAFT-ESTIMATE", ClientInvoiceStatus.Draft, null, ClientInvoiceDocumentType.Estimate);
        // Converting an approved estimate makes it a draft invoice again with SentAt cleared.
        await SeedInvoiceAsync(db, agentId, clientId, "HIDE-CONVERTED", ClientInvoiceStatus.Draft, null);
        await SeedInvoiceAsync(db, agentId, clientId, "HIDE-VOID-NEVER-SENT", ClientInvoiceStatus.Void, null);
        await SeedInvoiceAsync(db, agentId, otherClientId, "HIDE-OTHER-CLIENT", ClientInvoiceStatus.Sent, sent);
        db.ChangeTracker.Clear();

        var controller = AsClient(new IPRO.Web.Controllers.ClientPortalController(db), clientId);
        var result = Assert.IsType<ViewResult>(await controller.Invoices());
        var listed = Assert.IsType<List<ClientInvoice>>(result.Model).Select(i => i.DocumentNumber).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "SHOW-APPROVED", "SHOW-PAID", "SHOW-SENT", "SHOW-VOID-AFTER-SENDING" }, listed);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static T AsClient<T>(T controller, int clientId) where T : Controller
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, clientId.ToString()) }, "ClientPortal"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());
        return controller;
    }

    private static async Task<int> SeedAgentAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T597-{Guid.NewGuid():N}")[..20], MonthlyPrice = 90m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t597-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Portal", LastName = "Adviser", CompanyName = "Privacy Co",
            DomainName = ($"t597-{Guid.NewGuid():N}")[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<int> SeedClientAsync(IPRODbContext db, int agentId)
    {
        var client = new Client { AgentUserId = agentId, FirstName = "Pat", LastName = "Client", Email = $"client-{Guid.NewGuid():N}@example.test" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    // What PortalRequestsController.Schedule makes: a follow-up titled for the adviser, and the request
    // marked Scheduled and pointing at it.
    private static async Task<int> SeedAppointmentAsync(IPRODbContext db, int clientId, DateTime due, string? clientNotes, bool completed)
    {
        var followUp = new ClientFollowUp { ClientId = clientId, Title = "Appointment: Pat Client", Notes = "adviser's own note", DueAt = due, IsCompleted = completed };
        db.ClientFollowUps.Add(followUp);
        await db.SaveChangesAsync();
        db.PortalAppointmentRequests.Add(new PortalAppointmentRequest
        {
            ClientId = clientId, Notes = clientNotes, Status = PortalAppointmentRequestStatus.Scheduled,
            ScheduledAt = due, RespondedAt = DateTime.UtcNow, ClientFollowUpId = followUp.Id
        });
        await db.SaveChangesAsync();
        return followUp.Id;
    }

    private static async Task SeedInvoiceAsync(IPRODbContext db, int agentId, int clientId, string number, ClientInvoiceStatus status, DateTime? sentAt,
        ClientInvoiceDocumentType type = ClientInvoiceDocumentType.Invoice)
    {
        db.ClientInvoices.Add(new ClientInvoice
        {
            AgentUserId = agentId, ClientId = clientId, DocumentType = type, Status = status,
            DocumentNumber = number, Total = 100m, Currency = "CAD",
            ViewToken = Guid.NewGuid().ToString("N"), SentAt = sentAt
        });
        await db.SaveChangesAsync();
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
