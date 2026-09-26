using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// 523 (2026-09-26), slice 3: the reminder schedule the adviser controls -- a set number of days
// before the due date, on it, the day after, then 7, 14 and 30 days overdue, each a switch with
// its own wording -- run by the daily job in the adviser's own day, each stage once per invoice,
// never two reminders within five days, with a settings page and a preview. The stage logic and
// the wording are pinned without a clock; the job, the page and the button run against MySQL.
public class InvoiceReminders523Tests
{
    private static readonly DateTime Due = new(2026, 10, 15);

    private static ClientInvoiceReminderSettings Settings(bool before = false, int beforeDays = 3, bool onDue = false,
        bool first = true, bool d7 = true, bool d14 = true, bool d30 = true) => new()
    {
        AgentUserId = 1, BeforeDueEnabled = before, BeforeDueDays = beforeDays, OnDueEnabled = onDue,
        OverdueFirstEnabled = first, Overdue7Enabled = d7, Overdue14Enabled = d14, Overdue30Enabled = d30
    };

    private static string? Stage(ClientInvoiceReminderSettings s, int daysFromDue, params string[] sent) =>
        ClientInvoiceReminderSchedule.StageDue(s, Due, Due.AddDays(daysFromDue), sent.ToHashSet());

    // ---- which stage is due today -----------------------------------------------------------------

    [Fact]
    public void The_defaults_remind_only_after_the_due_date()
    {
        var d = ClientInvoiceReminderSchedule.Defaults(1);
        Assert.Null(Stage(d, -3));
        Assert.Null(Stage(d, 0));
        Assert.Equal("overdue1", Stage(d, 1));
        Assert.Equal("overdue1", Stage(d, 6));
        Assert.Equal("overdue7", Stage(d, 7));
        Assert.Equal("overdue7", Stage(d, 13));
        Assert.Equal("overdue14", Stage(d, 14));
        Assert.Equal("overdue14", Stage(d, 29));
        Assert.Equal("overdue30", Stage(d, 30));
        Assert.Equal("overdue30", Stage(d, 59));
        Assert.Null(Stage(d, 60));
        Assert.Null(Stage(d, 200));
    }

    [Fact]
    public void A_stage_sent_once_never_repeats_and_a_switched_off_stage_never_fires()
    {
        var d = ClientInvoiceReminderSchedule.Defaults(1);
        Assert.Null(Stage(d, 8, "overdue7"));
        Assert.Equal("overdue7", Stage(d, 8, "overdue1"));           // an earlier stage sent does not block the next
        Assert.Null(Stage(Settings(d7: false), 8));
        Assert.Equal("overdue14", Stage(Settings(d7: false), 14));
        Assert.Null(Stage(Settings(first: false, d7: false, d14: false, d30: false), 20));
    }

    [Fact]
    public void The_before_and_on_due_stages_fire_when_switched_on()
    {
        var s = Settings(before: true, beforeDays: 3, onDue: true);
        Assert.Equal("before", Stage(s, -3));
        Assert.Equal("before", Stage(s, -2));                          // a missed morning still gets it
        Assert.Equal("before", Stage(s, -1));
        Assert.Null(Stage(s, -4));
        Assert.Null(Stage(s, -1, "before"));
        Assert.Equal("due", Stage(s, 0));

        var one = Settings(before: true, beforeDays: 1);
        Assert.Equal("before", Stage(one, -1));
        Assert.Null(Stage(one, -2));

        var seven = Settings(before: true, beforeDays: 7);
        Assert.Equal("before", Stage(seven, -7));
        Assert.Equal("before", Stage(seven, -5));
        Assert.Null(Stage(seven, -4));
    }

    // ---- the words ---------------------------------------------------------------------------------

    [Fact]
    public void The_wording_is_filled_in_and_nothing_typed_becomes_markup()
    {
        var invoice = new ClientInvoice
        {
            DocumentNumber = "INV-7", Total = 1234.5m, Currency = "CAD", DueDate = Due,
            Client = new Client { FirstName = "Ann <b>", LastName = "X" },
            AgentUser = new AgentUser { CompanyName = "Acme & Co" }
        };
        var html = ClientInvoiceReminderSchedule.Fill(
            "Hi {client}, {invoice} for {amount} was due {due}, {days} days ago.\nFrom {company} <script>", invoice, 9);
        Assert.Equal("Hi Ann &lt;b&gt;, INV-7 for $1,234.50 CAD was due October 15, 2026, 9 days ago.<br>From Acme &amp; Co &lt;script&gt;", html);

        Assert.Equal("Invoice INV-7 is due in 3 days", ClientInvoiceReminderSchedule.SubjectFor("before", "INV-7", -3));
        Assert.Equal("Invoice INV-7 is due in 1 day", ClientInvoiceReminderSchedule.SubjectFor("before", "INV-7", -1));
        Assert.Equal("Invoice INV-7 is due today", ClientInvoiceReminderSchedule.SubjectFor("due", "INV-7", 0));
        Assert.Equal("Reminder: Invoice INV-7 is overdue", ClientInvoiceReminderSchedule.SubjectFor("overdue7", "INV-7", 9));

        var stock = ClientInvoiceReminderSchedule.Defaults(1);
        Assert.Equal(ClientInvoiceReminderSchedule.DefaultOverdueMessage, ClientInvoiceReminderSchedule.MessageFor(stock, "overdue14"));
        Assert.Equal(ClientInvoiceReminderSchedule.DefaultBeforeDueMessage, ClientInvoiceReminderSchedule.MessageFor(stock, "before"));
        var own = Settings();
        own.OverdueMessage = "  Please settle {invoice}.  ";
        Assert.Equal("Please settle {invoice}.", ClientInvoiceReminderSchedule.MessageFor(own, "overdue1"));
    }

    // ---- the pages, the job, the schema and the eraser carry it --------------------------------------

    [Fact]
    public void The_schedule_is_wired_end_to_end()
    {
        var controller = Read(@"src\IPRO.Web\Controllers\ClientInvoicesController.cs");
        Assert.Contains("public async Task<IActionResult> Reminders(", controller);
        Assert.Contains("ClientInvoiceReminderSchedule.SaveAsync(", controller);
        Assert.Contains("ClientInvoiceReminderSchedule.LoadAsync(", controller);

        var job = Read(@"src\IPRO.Scheduler\OverdueInvoiceReminderJob.cs");
        Assert.Contains("ClientInvoiceReminderSchedule.StageDue(", job);
        Assert.Contains("ClientInvoiceReminderSends.Add(", job);
        Assert.DoesNotContain("AddDays(-7)", job);                                       // the weekly-for-ever resend is gone

        Assert.Contains("/portal/ClientInvoices/Reminders", Read(@"src\IPRO.Web\Views\ClientInvoices\Index.cshtml"));
        Assert.Contains("reminder-previews", Read(@"src\IPRO.Web\Views\ClientInvoices\Reminders.cshtml"));

        var web = Read(@"src\IPRO.Web\Program.cs");
        Assert.Contains("EnsureClientInvoiceReminderSchemaAsync", web);
        Assert.Contains("\"overdue-invoice-reminders\", job => job.RunAsync(), \"0 13 * * *\"", web);   // the morning, not midnight UTC
        Assert.Contains("EnsureClientInvoiceReminderSchemaAsync", Read(@"src\IPRO.Admin\Program.cs"));
        Assert.Contains("CREATE TABLE IF NOT EXISTS `ClientInvoiceReminderSettings`", Read(@"src\IPRO.DataAccess\StartupSchemaRepair.cs"));
        Assert.Contains("CREATE TABLE IF NOT EXISTS `ClientInvoiceReminderSends`", Read(@"src\IPRO.DataAccess\StartupSchemaRepair.cs"));

        var eraser = Read(@"src\IPRO.DataAccess\AgentDataEraser.cs");
        Assert.Contains("(\"ClientInvoiceReminderSettings\"", eraser);
        Assert.Contains("(\"ClientInvoiceReminderSends\"", eraser);
    }

    // ---- against the database --------------------------------------------------------------------

    [Fact]
    public async Task The_settings_save_and_load_round_trip()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);

        var fresh = await ClientInvoiceReminderSchedule.LoadAsync(db, agentId);
        Assert.False(fresh.BeforeDueEnabled);
        Assert.Equal(3, fresh.BeforeDueDays);
        Assert.True(fresh.OverdueFirstEnabled && fresh.Overdue7Enabled && fresh.Overdue14Enabled && fresh.Overdue30Enabled);

        await ClientInvoiceReminderSchedule.SaveAsync(db, new ClientInvoiceReminderSettings
        {
            AgentUserId = agentId, BeforeDueEnabled = true, BeforeDueDays = 5, OnDueEnabled = true,
            Overdue14Enabled = false, OverdueMessage = " Please settle {invoice} now. "
        });
        var saved = await ClientInvoiceReminderSchedule.LoadAsync(db, agentId);
        Assert.True(saved.BeforeDueEnabled);
        Assert.Equal(5, saved.BeforeDueDays);
        Assert.True(saved.OnDueEnabled);
        Assert.False(saved.Overdue14Enabled);
        Assert.True(saved.Overdue30Enabled);
        Assert.Equal("Please settle {invoice} now.", saved.OverdueMessage);

        await ClientInvoiceReminderSchedule.SaveAsync(db, new ClientInvoiceReminderSettings { AgentUserId = agentId, BeforeDueDays = 40, OverdueMessage = "" });
        var again = await ClientInvoiceReminderSchedule.LoadAsync(db, agentId);
        Assert.False(again.BeforeDueEnabled);
        Assert.Equal(30, again.BeforeDueDays);                       // clamped
        Assert.Equal(string.Empty, again.OverdueMessage);
        Assert.Equal(1, await db.ClientInvoiceReminderSettings.CountAsync(s => s.AgentUserId == agentId));
    }

    [Fact]
    public async Task The_job_runs_each_advisers_schedule_in_their_own_day_once_per_stage()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var today = AgentLocalTime.FromUtc(DateTime.UtcNow, AgentLocalTime.DefaultTimeZone).Date;

        var stock = await SeedAgentAsync(db);
        var stockIds = new Dictionary<int, int>();
        foreach (var days in new[] { -3, 0, 3, 8, 45, 70 })
            stockIds[days] = await SeedInvoiceAsync(db, stock, today.AddDays(-days));

        var eager = await SeedAgentAsync(db);
        await ClientInvoiceReminderSchedule.SaveAsync(db, new ClientInvoiceReminderSettings { AgentUserId = eager, BeforeDueEnabled = true, BeforeDueDays = 3, OnDueEnabled = true });
        var eagerBefore = await SeedInvoiceAsync(db, eager, today.AddDays(3));
        var eagerDue = await SeedInvoiceAsync(db, eager, today);
        db.ChangeTracker.Clear();

        var email = new RecordingEmail();
        await NewReminderJob(db, email).RunAsync();
        db.ChangeTracker.Clear();

        var sends = await db.ClientInvoiceReminderSends.AsNoTracking().ToListAsync();
        Assert.Equal("overdue1", sends.Single(s => s.ClientInvoiceId == stockIds[3]).Stage);
        Assert.Equal("overdue7", sends.Single(s => s.ClientInvoiceId == stockIds[8]).Stage);
        Assert.Equal("overdue30", sends.Single(s => s.ClientInvoiceId == stockIds[45]).Stage);
        Assert.DoesNotContain(sends, s => s.ClientInvoiceId == stockIds[-3] || s.ClientInvoiceId == stockIds[0] || s.ClientInvoiceId == stockIds[70]);
        Assert.Equal("before", sends.Single(s => s.ClientInvoiceId == eagerBefore).Stage);
        Assert.Equal("due", sends.Single(s => s.ClientInvoiceId == eagerDue).Stage);
        Assert.Equal(5, sends.Count);
        Assert.Equal(5, email.Sent.Count);
        Assert.Contains(email.Sent, m => m.Subject.EndsWith(" is due in 3 days"));
        Assert.Contains(email.Sent, m => m.Subject.EndsWith(" is due today"));
        Assert.Equal(3, email.Sent.Count(m => m.Subject.StartsWith("Reminder: Invoice ") && m.Subject.EndsWith(" is overdue")));
        Assert.Equal(5, await db.ClientInvoiceEmails.CountAsync(e => e.Kind == ClientInvoiceEmailKind.Reminder));
        Assert.All(await db.ClientInvoices.Where(i => sends.Select(s => s.ClientInvoiceId).Contains(i.Id)).ToListAsync(), i => Assert.NotNull(i.LastReminderSentAt));

        // The same morning again (or Hangfire's retry): nothing goes twice.
        await NewReminderJob(db, email).RunAsync();
        Assert.Equal(5, email.Sent.Count);
        Assert.Equal(5, await db.ClientInvoiceReminderSends.CountAsync());
    }

    [Fact]
    public async Task A_recent_reminder_holds_the_schedule_for_five_days()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var today = AgentLocalTime.FromUtc(DateTime.UtcNow, AgentLocalTime.DefaultTimeZone).Date;
        var agentId = await SeedAgentAsync(db);
        var held = await SeedInvoiceAsync(db, agentId, today.AddDays(-8), lastReminderUtc: DateTime.UtcNow.AddDays(-2));   // the button, two days ago
        var free = await SeedInvoiceAsync(db, agentId, today.AddDays(-8), lastReminderUtc: DateTime.UtcNow.AddDays(-6));
        db.ChangeTracker.Clear();

        var email = new RecordingEmail();
        await NewReminderJob(db, email).RunAsync();

        var sends = await db.ClientInvoiceReminderSends.AsNoTracking().ToListAsync();
        var only = Assert.Single(sends);
        Assert.Equal(free, only.ClientInvoiceId);
        Assert.Single(email.Sent);
        Assert.DoesNotContain(sends, s => s.ClientInvoiceId == held);
    }

    [Fact]
    public async Task The_settings_page_saves_what_the_adviser_chose()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var controller = NewInvoicesController(db, new RecordingEmail(), agentId);

        var page = Assert.IsType<ViewResult>(await controller.Reminders());
        var form = Assert.IsType<IPRO.Web.Models.ClientInvoiceReminderForm>(page.Model);
        Assert.True(form.Overdue7Enabled);
        Assert.False(form.OnDueEnabled);

        form.OnDueEnabled = true;
        form.Overdue30Enabled = false;
        form.BeforeDueMessage = "Coming up: {invoice}.";
        var redirect = Assert.IsType<RedirectToActionResult>(await controller.Reminders(form));
        Assert.Equal("Reminders", redirect.ActionName);

        var saved = await ClientInvoiceReminderSchedule.LoadAsync(db, agentId);
        Assert.True(saved.OnDueEnabled);
        Assert.False(saved.Overdue30Enabled);
        Assert.Equal("Coming up: {invoice}.", saved.BeforeDueMessage);

        // A bad form comes straight back, nothing saved.
        controller.ModelState.AddModelError("BeforeDueDays", "Days before the due date must be between 1 and 30.");
        form.OnDueEnabled = false;
        Assert.IsType<ViewResult>(await controller.Reminders(form));
        Assert.True((await ClientInvoiceReminderSchedule.LoadAsync(db, agentId)).OnDueEnabled);
    }

    [Fact]
    public async Task The_button_uses_the_advisers_own_overdue_wording()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var today = AgentLocalTime.FromUtc(DateTime.UtcNow, AgentLocalTime.DefaultTimeZone).Date;
        var agentId = await SeedAgentAsync(db);
        await ClientInvoiceReminderSchedule.SaveAsync(db, new ClientInvoiceReminderSettings { AgentUserId = agentId, OverdueMessage = "Please settle {invoice} now, {client}." });
        var invoiceId = await SeedInvoiceAsync(db, agentId, today.AddDays(-5));

        var email = new RecordingEmail();
        var controller = NewInvoicesController(db, email, agentId);
        Assert.IsType<RedirectToActionResult>(await controller.Remind(invoiceId, "aging"));

        var sent = Assert.Single(email.Sent);
        Assert.Contains("Please settle G-", sent.Html);
        Assert.Contains(" now, Cli.", sent.Html);
        Assert.EndsWith(" is overdue", sent.Subject);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static IPRO.Scheduler.OverdueInvoiceReminderJob NewReminderJob(IPRODbContext db, IEmailService email) =>
        new(db, new GrantAll(), email, new ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IPRO.Scheduler.OverdueInvoiceReminderJob>.Instance);

    private static IPRO.Web.Controllers.ClientInvoicesController NewInvoicesController(IPRODbContext db, IEmailService email, int agentId)
    {
        var controller = new IPRO.Web.Controllers.ClientInvoicesController(
            db, new GrantAll(), new IPRO.Business.Services.ClientInvoiceService(new IPRO.DataAccess.Repositories.UnitOfWork(db)), email,
            new ConfigurationBuilder().Build());
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());
        return controller;
    }

    private static async Task<int> SeedAgentAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T523-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t523c-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Remind",
            LastName = "Agent",
            CompanyName = "Remind Co",
            DomainName = ($"t523c-{Guid.NewGuid():N}")[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<int> SeedInvoiceAsync(IPRODbContext db, int agentId, DateTime dueDate, DateTime? lastReminderUtc = null)
    {
        var client = new Client
        {
            AgentUserId = agentId, FirstName = "Cli", LastName = "Ent",
            Email = $"{Guid.NewGuid():N}@example.test"
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var invoice = new ClientInvoice
        {
            AgentUserId = agentId, ClientId = client.Id,
            DocumentType = ClientInvoiceDocumentType.Invoice, Status = ClientInvoiceStatus.Sent,
            DocumentNumber = ($"G-{Guid.NewGuid():N}")[..20], Total = 250m, Currency = "CAD",
            ViewToken = Guid.NewGuid().ToString("N"),
            IssueDate = dueDate.AddDays(-14),
            DueDate = dueDate,
            SentAt = DateTime.UtcNow.AddDays(-15),
            LastReminderSentAt = lastReminderUtc
        };
        db.ClientInvoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice.Id;
    }

    private sealed class GrantAll : IPRO.Business.Interfaces.IPackageEntitlementService
    {
        public Task<IPRO.Business.Interfaces.PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) =>
            Task.FromResult(new IPRO.Business.Interfaces.PackageFeatureAccess { FeatureCode = featureCode, IsIncluded = true });
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => Task.FromResult(true);
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode) =>
            Task.FromResult(agentIds.Distinct().ToDictionary(a => a, _ => true));
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(false);
    }

    private sealed class RecordingEmail : IEmailService
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = new();
        public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;
        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        {
            Sent.Add((toEmail, subject, htmlBody));
            return Task.FromResult(EmailSendResult.Sent($"acs-{Sent.Count}"));
        }
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) =>
            Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
    }

    private sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
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
