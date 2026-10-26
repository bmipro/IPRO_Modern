using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 598 (the owner's decisions of 2026-10-24, from reading the application for the System Book):
//   - the client portal is read-only while the adviser's plan has no client portal (downgrade, lapse);
//   - a client's message, upload or appointment request emails the adviser, at most once an hour;
//   - a client's upload counts against the adviser's storage;
//   - wording: Reset Password says the password is shown on screen; the failed-payment email says
//     "suspended" only once PayPal has suspended; the template-retired email no longer signs as 247Advisers;
//   - the advisers' help library leaves out the staff-only parts of a guide.
public class PortalBatch598Tests
{
    // ---------------------------------------------------------------- read-only

    [Fact]
    public async Task Without_the_client_portal_in_the_plan_a_client_can_read_but_not_send_upload_delete_or_request()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, clientId) = await SeedAsync(db);
        db.PortalDocuments.Add(new PortalDocument { ClientId = clientId, UploadedByClient = true, FileName = "mine.txt", BlobUrl = "https://x/portal-documents/mine.txt", ContentType = "text/plain", FileSizeBytes = 5 });
        await db.SaveChangesAsync();
        var docId = await db.PortalDocuments.Where(d => d.ClientId == clientId).Select(d => d.Id).SingleAsync();
        db.ChangeTracker.Clear();
        var email = new RecordingEmail();
        var plan = new Plan(portal: false);

        var messages = AsClient(new IPRO.Web.Controllers.ClientPortalMessagesController(db, plan, email, Config(), NullLogger<IPRO.Web.Controllers.ClientPortalMessagesController>.Instance), clientId, agentId);
        await messages.Index();
        Assert.Equal(true, messages.ViewData["PortalReadOnly"]);
        await messages.Send("hello?");
        Assert.Equal(IPRO.Web.Infrastructure.ClientPortalActivity.ReadOnlyMessage, messages.TempData["Error"]);

        var documents = AsClient(new IPRO.Web.Controllers.ClientPortalDocumentsController(db, new Blobs(), plan, email, Config(), NullLogger<IPRO.Web.Controllers.ClientPortalDocumentsController>.Instance), clientId, agentId);
        await documents.Upload(TextFile("note.txt", "a note"));
        Assert.Equal(IPRO.Web.Infrastructure.ClientPortalActivity.ReadOnlyMessage, documents.TempData["Error"]);
        await documents.Delete(docId);

        var appointments = AsClient(Appointments(db, plan, email), clientId, agentId);
        await appointments.RequestAppointment("can we meet?", null);
        Assert.Equal(IPRO.Web.Infrastructure.ClientPortalActivity.ReadOnlyMessage, appointments.TempData["Error"]);

        Assert.Equal(0, await db.PortalMessages.CountAsync(m => m.ClientId == clientId));
        Assert.Equal(1, await db.PortalDocuments.CountAsync(d => d.ClientId == clientId));   // nothing uploaded, nothing deleted
        Assert.Equal(0, await db.PortalAppointmentRequests.CountAsync(r => r.ClientId == clientId));
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task With_the_client_portal_in_the_plan_the_portal_is_writable()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, clientId) = await SeedAsync(db);
        var messages = AsClient(new IPRO.Web.Controllers.ClientPortalMessagesController(db, new Plan(portal: true), new RecordingEmail(), Config(), NullLogger<IPRO.Web.Controllers.ClientPortalMessagesController>.Instance), clientId, agentId);
        await messages.Index();
        Assert.Equal(false, messages.ViewData["PortalReadOnly"]);
        await messages.Send("hello");
        Assert.Equal(1, await db.PortalMessages.CountAsync(m => m.ClientId == clientId && m.IsFromClient));
    }

    // ---------------------------------------------------------------- the adviser is told

    [Fact]
    public async Task A_clients_message_emails_the_adviser_once_an_hour_without_the_message_itself()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, clientId) = await SeedAsync(db);
        var email = new RecordingEmail();
        var messages = AsClient(new IPRO.Web.Controllers.ClientPortalMessagesController(db, new Plan(portal: true), email, Config(), NullLogger<IPRO.Web.Controllers.ClientPortalMessagesController>.Instance), clientId, agentId);

        await messages.Send("my private question about the estate");
        var sent = Assert.Single(email.Sent);
        Assert.Equal("adviser598@example.test", sent.To);
        Assert.Equal("New in your client portal: Pat Client", sent.Subject);
        Assert.Contains("sent you a message", sent.Body);
        Assert.Contains("https://app.example.test/portal/PortalMessages", sent.Body);
        Assert.DoesNotContain("estate", sent.Body);

        // A second message, an upload and a request within the hour: still one email.
        await messages.Send("and another");
        var documents = AsClient(new IPRO.Web.Controllers.ClientPortalDocumentsController(db, new Blobs(), new Plan(portal: true), email, Config(), NullLogger<IPRO.Web.Controllers.ClientPortalDocumentsController>.Instance), clientId, agentId);
        await documents.Upload(TextFile("note.txt", "a note"));
        var appointments = AsClient(Appointments(db, new Plan(portal: true), email), clientId, agentId);
        await appointments.RequestAppointment("can we meet?", null);
        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task After_a_quiet_hour_the_next_upload_or_request_emails_again()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, clientId) = await SeedAsync(db);
        db.PortalMessages.Add(new PortalMessage { ClientId = clientId, IsFromClient = true, AuthorName = "Pat", Body = "old", CreatedAt = DateTime.UtcNow.AddHours(-2) });
        await db.SaveChangesAsync();
        var email = new RecordingEmail();

        var documents = AsClient(new IPRO.Web.Controllers.ClientPortalDocumentsController(db, new Blobs(), new Plan(portal: true), email, Config(), NullLogger<IPRO.Web.Controllers.ClientPortalDocumentsController>.Instance), clientId, agentId);
        await documents.Upload(TextFile("note.txt", "a note"));
        var sent = Assert.Single(email.Sent);
        Assert.Contains("uploaded a document", sent.Body);
        Assert.Contains($"/portal/Clients/Details/{clientId}", sent.Body);
    }

    // ---------------------------------------------------------------- storage

    [Fact]
    public async Task A_clients_upload_that_would_overfill_the_advisers_storage_is_refused_in_the_clients_words()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, clientId) = await SeedAsync(db);
        // 1 MB plan, 1 MB already used by the adviser's own documents.
        db.AgentDocuments.Add(new AgentDocument { AgentUserId = agentId, FileName = "big.pdf", BlobUrl = "https://x/agent-documents/big.pdf", FileSizeBytes = 1024 * 1024 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var documents = AsClient(new IPRO.Web.Controllers.ClientPortalDocumentsController(db, new Blobs(), new Plan(portal: true, storageMb: 1), new RecordingEmail(), Config(), NullLogger<IPRO.Web.Controllers.ClientPortalDocumentsController>.Instance), clientId, agentId);
        await documents.Upload(TextFile("note.txt", "one byte too many"));

        Assert.StartsWith("Your advisor's document storage is full", documents.TempData["Error"] as string);
        Assert.Equal(0, await db.PortalDocuments.CountAsync(d => d.ClientId == clientId));
    }

    // ---------------------------------------------------------------- wording

    [Fact]
    public void Reset_password_says_the_password_is_shown_on_screen_not_emailed()
    {
        foreach (var view in new[] { "Details.cshtml", "Index.cshtml" })
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "IPRO.Admin", "Views", "Agents", view));
            Assert.DoesNotContain("The temporary password is emailed to the agent.", text);
            Assert.Contains("The temporary password is shown here on screen, for you to pass on to the agent; it is not emailed.", text);
        }
    }

    [Fact]
    public void The_template_retired_email_no_longer_signs_as_247Advisers()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "IPRO.Admin", "Controllers", "WebsiteTemplatesController.cs"));
        Assert.DoesNotContain("247Advisers", text);
    }

    [Fact]
    public void The_failed_payment_email_says_suspended_only_once_PayPal_has_suspended()
    {
        var build = typeof(IPRO.Billing.PayPalBillingService).GetMethod("BuildBillingIssueEmailHtml", BindingFlags.NonPublic | BindingFlags.Static)!;
        var retrying = (string)build.Invoke(null, new object[] { "Pat", "IPro Gold", "67.80 CAD", BillingStatus.Failed, false })!;
        var suspended = (string)build.Invoke(null, new object[] { "Pat", "IPro Gold", "67.80 CAD", BillingStatus.Failed, true })!;

        Assert.DoesNotContain("suspended", retrying);
        Assert.Contains("PayPal will try again automatically", retrying);
        Assert.Contains("Fix Billing", retrying);
        Assert.Contains("Your subscription is suspended at PayPal", suspended);
    }

    // ---------------------------------------------------------------- staff-only help

    [Fact]
    public void The_help_library_leaves_out_staff_only_parts()
    {
        var md = "# Guide\n\n## For advisers\n\nKeep this.\n\n<!-- staff-only -->\n## Staff notes\n\naz webapp config ssl list\n<!-- /staff-only -->\n\n## Also for advisers\n\nKeep this too.\n";
        var html = IPRO.Web.Infrastructure.HelpDocsService.RenderArticle(md);
        Assert.Contains("Keep this.", html);
        Assert.Contains("Keep this too.", html);
        Assert.DoesNotContain("Staff notes", html);
        Assert.DoesNotContain("az webapp", html);
    }

    [Fact]
    public void The_domains_guide_advisers_read_has_no_staff_notes_but_keeps_its_steps()
    {
        var html = IPRO.Web.Infrastructure.HelpDocsService.GetArticleHtml("domains-leads")!;
        Assert.DoesNotContain("az webapp", html);
        Assert.DoesNotContain("What to Tell a New Agent", html);
        Assert.DoesNotContain("411trades", html);
        Assert.DoesNotContain("SSL Automation", html);
        Assert.Contains("id=\"connect-your-domain-step-by-step\"", html);
        Assert.Contains("id=\"add-a-custom-domain\"", html);
        Assert.Contains("id=\"review-website-leads\"", html);

        // The repository keeps every word of it.
        var file = File.ReadAllText(Path.Combine(RepoRoot(), "DOCS", "05_DOMAINS_AND_LEADS.md"));
        Assert.Contains("## SSL Automation", file);
        Assert.Contains("<!-- staff-only -->", file);
    }

    // ---------------------------------------------------------------- harness

    internal static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.example.test" })
        .Build();

    internal static IPRO.Web.Controllers.ClientPortalAppointmentsController Appointments(IPRODbContext db, IPackageEntitlementService? plan = null, IEmailService? email = null) =>
        new(db, plan ?? new Plan(portal: true), email ?? new RecordingEmail(), Config(), NullLogger<IPRO.Web.Controllers.ClientPortalAppointmentsController>.Instance);

    internal static T AsClient<T>(T controller, int clientId, int agentId) where T : Controller
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, clientId.ToString()),
                new Claim("AgentUserId", agentId.ToString())
            }, "ClientPortal"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());
        return controller;
    }

    private static async Task<(int AgentId, int ClientId)> SeedAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T598-{Guid.NewGuid():N}")[..20], MonthlyPrice = 90m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t598-{Guid.NewGuid():N}")[..20],
            Email = "adviser598@example.test",
            FirstName = "Ada", LastName = "Adviser", CompanyName = "Portal Co",
            DomainName = ($"t598-{Guid.NewGuid():N}")[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var client = new Client { AgentUserId = agent.Id, FirstName = "Pat", LastName = "Client", Email = $"client-{Guid.NewGuid():N}@example.test" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return (agent.Id, client.Id);
    }

    private static IFormFile TextFile(string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    // The adviser's plan: client portal in or out, and a storage limit.
    internal sealed class Plan : IPackageEntitlementService
    {
        private readonly bool _portal;
        private readonly int? _storageMb;
        public Plan(bool portal, int? storageMb = 500) { _portal = portal; _storageMb = storageMb; }

        public Task<PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) => Task.FromResult(new PackageFeatureAccess
        {
            FeatureCode = featureCode,
            IsIncluded = featureCode != PackageFeatureCodes.ClientPortal || _portal,
            LimitValue = featureCode == PackageFeatureCodes.FileUploadCapacity ? _storageMb : null
        });
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => Task.FromResult(featureCode != PackageFeatureCodes.ClientPortal || _portal);
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode) => Task.FromResult(agentIds.ToDictionary(a => a, _ => _portal));
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(false);
    }

    internal sealed class RecordingEmail : IEmailService
    {
        public List<(string To, string Subject, string Body)> Sent { get; } = new();
        public Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        { Sent.Add((toEmail, subject, htmlBody)); return Task.FromResult(true); }
        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        { Sent.Add((toEmail, subject, htmlBody)); return Task.FromResult(EmailSendResult.Sent()); }
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
    }

    private sealed class Blobs : IBlobStorageService
    {
        public Task<bool> DeleteAsync(string blobUrl) => Task.FromResult(true);
        public Task<string> UploadAsync(Stream f, string n, string c, string t, bool p) => Task.FromResult($"https://x/{c}/{n}");
        public Task<Stream?> DownloadAsync(string blobUrl) => Task.FromResult<Stream?>(null);
        public Task<List<string>> ListAsync(string containerName) => Task.FromResult(new List<string>());
        public string GetPublicUrl(string containerName, string fileName) => $"https://x/{containerName}/{fileName}";
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => Task.CompletedTask;
    }

    private sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
