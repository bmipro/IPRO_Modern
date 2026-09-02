using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 453 (2026-09-02). The owner, testing support-ticket submission: a support reply arrives as
// an email the agent will naturally answer -- into a mailbox nobody reads as a ticket; and open
// tickets live only on their own page, where they can wait unnoticed. Three things: the reply
// email says where to continue, the dashboard shows open tickets first, and the existing
// notification of the support inbox on every new ticket and agent reply is pinned so it stays.
public class SupportTicketFollowUpTests
{
    [Fact]
    public async Task The_reply_email_tells_the_agent_not_to_reply_and_where_to_continue()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var ticketId = await SeedTicketAsync(db, agentId, "Cannot log in", SupportTicketStatus.Open, unreadForAdmin: true, lastMessageAgo: TimeSpan.FromHours(1));

        var email = new RecordingEmail();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.example.test" })
            .Build();
        var controller = new IPRO.Admin.Controllers.SupportTicketsController(
            db, email, Microsoft.Extensions.Logging.Abstractions.NullLogger<IPRO.Admin.Controllers.SupportTicketsController>.Instance,
            new NoAudit(), config);
        WireAdmin(controller);

        await controller.Reply(ticketId, "Please clear your browser cache and try again.");

        var sent = Assert.Single(email.Sent);
        Assert.Contains("do not reply", sent.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://app.example.test/Account/Login", sent.Body);
        Assert.Contains($"https://app.example.test/portal/Support/Details/{ticketId}", sent.Body);
        Assert.Contains("My Tickets", sent.Body);
        // The reply itself is still there.
        Assert.Contains("clear your browser cache", sent.Body);
    }

    [Fact]
    public async Task The_dashboard_surfaces_open_tickets_with_the_unanswered_ones_first()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var answered = await SeedTicketAsync(db, agentId, "Answered", SupportTicketStatus.InProgress, unreadForAdmin: false, lastMessageAgo: TimeSpan.FromMinutes(10));
        var waiting = await SeedTicketAsync(db, agentId, "Waiting on us", SupportTicketStatus.Open, unreadForAdmin: true, lastMessageAgo: TimeSpan.FromHours(2));
        await SeedTicketAsync(db, agentId, "Resolved", SupportTicketStatus.Resolved, unreadForAdmin: true, lastMessageAgo: TimeSpan.FromHours(3));
        await SeedTicketAsync(db, agentId, "Closed", SupportTicketStatus.Closed, unreadForAdmin: false, lastMessageAgo: TimeSpan.FromHours(4));
        db.ChangeTracker.Clear();

        var uow = new UnitOfWork(db);
        var controller = new IPRO.Admin.Controllers.AdminDashboardController(
            new IPRO.Business.Services.AgentService(uow, new PasswordHasher<AgentUser>()), uow, db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
            }
        };
        await controller.Index();

        var open = Assert.IsType<List<SupportTicket>>(controller.ViewData["OpenTickets"]);
        Assert.Equal(new[] { waiting, answered }, open.Select(t => t.Id).ToArray());
        Assert.All(open, t => Assert.NotNull(t.AgentUser));
        Assert.Equal(2, controller.ViewData["OpenTicketCount"]);
        Assert.Equal(1, controller.ViewData["AwaitingReplyCount"]);
    }

    [Fact]
    public void The_dashboard_view_shows_the_support_panel_above_everything_else()
    {
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\AdminDashboard\Index.cshtml"));
        var panel = view.IndexOf("Support requests", StringComparison.Ordinal);
        var stats = view.IndexOf("Total Agents", StringComparison.Ordinal);
        Assert.True(panel >= 0, "the dashboard must have a Support requests panel");
        Assert.True(panel < stats, "the Support requests panel must come before the statistics cards");
        Assert.Contains("/SupportTickets/Details/", view);
        Assert.Contains("awaiting reply", view);
        Assert.Contains("No open support requests", view);
    }

    [Fact]
    public async Task A_new_ticket_still_notifies_the_support_inbox()
    {
        // Already true before 453 and the answer to "should we create ticket@...": every new ticket
        // and every agent reply is mailed to Support:NotificationEmail. Pinned so it cannot be
        // lost in a later change to the controller.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);

        var email = new RecordingEmail();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Support:NotificationEmail"] = "support@example.test" })
            .Build();
        var controller = new IPRO.Web.Controllers.SupportController(
            db, email, config, Microsoft.Extensions.Logging.Abstractions.NullLogger<IPRO.Web.Controllers.SupportController>.Instance);
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());

        await controller.Create("Need help with my website", "The banner image will not upload.");

        var sent = Assert.Single(email.Sent);
        Assert.Equal("support@example.test", sent.To);
        Assert.Contains("[Ticket #", sent.Subject);
        Assert.Contains("banner image", sent.Body);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static void WireAdmin(Controller controller)
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "1"),
                new Claim(ClaimTypes.Name, "support-tester"),
                new Claim("FullName", "Support Tester"),
                new Claim("Role", AdminRoles.SuperAdmin)
            }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());
    }

    private static async Task<int> SeedAgentAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T453-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t453-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Ticket",
            LastName = "Agent",
            CompanyName = "Ticket Co",
            DomainName = ($"t453-{Guid.NewGuid():N}")[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<int> SeedTicketAsync(IPRODbContext db, int agentId, string subject, SupportTicketStatus status, bool unreadForAdmin, TimeSpan lastMessageAgo)
    {
        var at = DateTime.UtcNow - lastMessageAgo;
        var ticket = new SupportTicket
        {
            AgentUserId = agentId, Subject = subject, Status = status,
            HasUnreadForAdmin = unreadForAdmin, CreatedAt = at, UpdatedAt = at, LastMessageAt = at
        };
        ticket.Messages.Add(new SupportTicketMessage { IsFromAdmin = false, AuthorName = "Ticket Agent", Body = "Help please", CreatedAt = at });
        db.SupportTickets.Add(ticket);
        await db.SaveChangesAsync();
        return ticket.Id;
    }

    private sealed class RecordingEmail : IEmailService
    {
        public List<(string To, string Subject, string Body)> Sent { get; } = new();
        public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;
        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        { Sent.Add((toEmail, subject, htmlBody)); return Task.FromResult(EmailSendResult.Sent("msg-1")); }
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
    }

    private sealed class NoAudit : IPRO.Business.Interfaces.IAdminAuditLogService
    {
        public Task LogAsync(int adminUserId, string adminUsername, string action, string details) => Task.CompletedTask;
    }

    private sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
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
