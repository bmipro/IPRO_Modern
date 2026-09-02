using System;
using System.Collections.Generic;
using System.IO;
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

// TODO 454 (2026-09-02). After 452 closed it for invoices, four client-facing one-off senders
// still discarded the provider's answer and told the agent "sent" regardless: the portal invite,
// the appointment scheduled / declined emails, and the testimonial request. Now each captures
// the EmailSendResult; a failure is said out loud with its reason, and the portal invite remembers
// whether its email went out so the profile can say so later, not only in the moment.
public class ClientFacingSendResultTests
{
    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.example.test" })
        .Build();

    // ---- the portal invite -------------------------------------------------------------------

    [Fact]
    public async Task A_sent_invite_is_stamped_on_the_client()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var clientId = await SeedClientAsync(db, agentId);

        var email = new RecordingEmail { Next = _ => EmailSendResult.Sent("acs-inv-ok") };
        var controller = NewClients(db, email, agentId);
        await controller.InvitePortal(clientId);

        db.ChangeTracker.Clear();
        var client = await db.Clients.AsNoTracking().SingleAsync(c => c.Id == clientId);
        Assert.NotNull(client.PortalInviteEmailedAt);
        Assert.True(string.IsNullOrEmpty(client.PortalInviteEmailError));
        Assert.Contains(client.Email, (string)controller.TempData["Success"]!);
    }

    [Fact]
    public async Task A_failed_invite_keeps_the_link_says_why_and_is_remembered()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var clientId = await SeedClientAsync(db, agentId);

        var email = new RecordingEmail { Next = _ => EmailSendResult.Failed("Mailbox does not exist") };
        var controller = NewClients(db, email, agentId);
        await controller.InvitePortal(clientId);

        db.ChangeTracker.Clear();
        var client = await db.Clients.AsNoTracking().SingleAsync(c => c.Id == clientId);
        // The token stays: the activation link on the profile is the manual fallback.
        Assert.False(string.IsNullOrWhiteSpace(client.PortalInviteToken));
        Assert.Null(client.PortalInviteEmailedAt);
        Assert.Contains("Mailbox does not exist", client.PortalInviteEmailError);
        Assert.Null(controller.TempData["Success"]);
        var error = (string)controller.TempData["Error"]!;
        Assert.Contains("Mailbox does not exist", error);
        Assert.Contains(client.Email, error);
    }

    [Fact]
    public void The_profile_card_shows_whether_the_invite_email_went_out()
    {
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Clients\Details.cshtml"));
        var invited = view.Substring(view.IndexOf("pending activation", StringComparison.Ordinal));
        invited = invited.Substring(0, invited.IndexOf("Invite to Portal", StringComparison.Ordinal));
        Assert.Contains("PortalInviteEmailedAt", invited);
        Assert.Contains("PortalInviteEmailError", invited);
    }

    // ---- the appointment emails --------------------------------------------------------------

    [Fact]
    public async Task A_scheduled_appointment_whose_email_failed_is_still_scheduled_and_the_agent_is_told()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var clientId = await SeedClientAsync(db, agentId);
        var request = new PortalAppointmentRequest { ClientId = clientId, PreferredDate = DateTime.UtcNow.AddDays(3) };
        db.PortalAppointmentRequests.Add(request);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var email = new RecordingEmail { Next = _ => EmailSendResult.FailedTransient("Timed out") };
        var controller = new IPRO.Web.Controllers.PortalRequestsController(db, email, new GrantAll(), Config());
        Wire(controller, agentId);
        await controller.Schedule(request.Id, new DateTime(2026, 9, 9, 12, 0, 0));

        db.ChangeTracker.Clear();
        Assert.Equal(PortalAppointmentRequestStatus.Scheduled, (await db.PortalAppointmentRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).Status);
        Assert.Null(controller.TempData["Success"]);
        var error = (string)controller.TempData["Error"]!;
        Assert.Contains("scheduled", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Timed out", error);
    }

    // ---- the testimonial request -------------------------------------------------------------

    [Fact]
    public async Task A_testimonial_request_that_could_not_be_sent_does_not_claim_success()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var clientId = await SeedClientAsync(db, agentId);

        var email = new RecordingEmail { Next = _ => EmailSendResult.Failed("Address rejected") };
        var controller = new IPRO.Web.Controllers.TestimonialsController(db, new GrantAll(), email, Config(), new NoConsent());
        Wire(controller, agentId);
        await controller.RequestFromClient(clientId);

        Assert.Single(email.Sent);
        Assert.Null(controller.TempData["Success"]);
        Assert.Contains("Address rejected", (string)controller.TempData["Error"]!);
    }

    [Fact]
    public void Production_gets_the_two_invite_columns_at_startup()
    {
        var repair = File.ReadAllText(FindRepoFile(@"src\IPRO.DataAccess\StartupSchemaRepair.cs"));
        Assert.Contains("ALTER TABLE `Clients` ADD COLUMN `PortalInviteEmailedAt`", repair);
        Assert.Contains("ALTER TABLE `Clients` ADD COLUMN `PortalInviteEmailError`", repair);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static IPRO.Web.Controllers.ClientsController NewClients(IPRODbContext db, IEmailService email, int agentId)
    {
        var controller = new IPRO.Web.Controllers.ClientsController(
            null!, null!, null!, db, new GrantAll(), email, null!, null!,
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), Config());
        Wire(controller, agentId);
        return controller;
    }

    private static void Wire(Controller controller, int agentId)
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());
    }

    private static async Task<int> SeedAgentAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T454-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t454-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Send", LastName = "Agent", CompanyName = "Send Co",
            DomainName = ($"t454-{Guid.NewGuid():N}")[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<int> SeedClientAsync(IPRODbContext db, int agentId)
    {
        var client = new Client { AgentUserId = agentId, FirstName = "Cli", LastName = "Ent", Email = $"cli-{Guid.NewGuid():N}@example.test" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return client.Id;
    }

    private sealed class RecordingEmail : IEmailService
    {
        public Func<string, EmailSendResult> Next { get; set; } = _ => EmailSendResult.Sent();
        public List<(string To, string Subject, string Body)> Sent { get; } = new();
        public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;
        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        { Sent.Add((toEmail, subject, htmlBody)); return Task.FromResult(Next(toEmail)); }
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
    }

    private sealed class GrantAll : IPRO.Business.Interfaces.IPackageEntitlementService
    {
        public Task<IPRO.Business.Interfaces.PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) =>
            Task.FromResult(new IPRO.Business.Interfaces.PackageFeatureAccess { FeatureCode = featureCode, IsIncluded = true });
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => Task.FromResult(true);
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode) => throw new NotSupportedException();
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(false);
    }

    private sealed class NoConsent : IPRO.Business.Services.IEmailConsentService
    {
        public bool IsSuppressed(Client client, IPRO.Business.Services.EmailChannel channel, bool designSurvivesOptOut = false) => false;
        public Task<IPRO.Business.Services.SuppressionResult> SuppressAllAsync(Client client, string source) => throw new NotSupportedException();
        public Task ResubscribeAsync(Client client) => throw new NotSupportedException();
        public Task<int> CancelSuppressedDripEnrollmentsAsync(int batchLimit = 500) => Task.FromResult(0);
        public Task<string> GetOrCreateTokenAsync(Client client) => Task.FromResult("tok");
        public string BuildPreferencesUrl(string token) => $"https://example.test/prefs/{token}";
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
