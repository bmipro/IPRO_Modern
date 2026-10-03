using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Communication.Email;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;
using Xunit;

namespace IPRO.IntegrationTests;

// 542 (2026-10-03). The owner's Amazon pilot: an estimate to his own Yahoo address landed in Spam with its
// links disabled. Amazon delivered it and the authentication passed; what the filter saw was an HTML-only
// email that was a heading, one sentence and one "View estimate" button, under a business name in the From
// line and a free-mail Reply-To -- the shape of invoice fraud. The owner: "go, fix it with the next push".
// Every email now carries a plain-text part, and the emails a client gets about their business with the
// adviser read as a letter: greeted by name, what it is about, the adviser's name, phone, email and address.
public class ClientLetters542Tests
{
    private static readonly string Dot = ((char)0xB7).ToString();   // the middle dot between phone and email

    // ---- the plain-text part -------------------------------------------------------------------------

    [Fact]
    public void Html_reads_as_text_a_line_per_paragraph_with_each_link_written_out()
    {
        var html = """
            <html><head><style>p{color:red}</style><title>t</title></head><body>
            <!-- a comment -->
            <div style="x"><h1 style="margin:0">Global &amp; Co</h1></div>
            <div>
              <p>Hi Bob,</p>
              <p>Here is invoice <strong>INV-1</strong> for <strong>$357.00 CAD</strong>.</p>
              <p><a href="https://app.test/invoice/abc?x=1&amp;y=2" style="padding:1px">View invoice</a></p>
              <p>Best regards,<br>Bahman Motamed<br>416-555-1212 &middot; bm@example.test</p>
              <ul><li>One</li><li>Two</li></ul>
              <img src="https://app.test/open.gif" alt="">
              <script>alert(1)</script>
            </div></body></html>
            """;

        Assert.Equal(
            "Global & Co\n\nHi Bob,\n\nHere is invoice INV-1 for $357.00 CAD.\n\n"
            + "View invoice (https://app.test/invoice/abc?x=1&y=2)\n\n"
            + $"Best regards,\nBahman Motamed\n416-555-1212 {Dot} bm@example.test\n\n- One\n- Two",
            EmailPlainText.FromHtml(html));
    }

    [Theory]
    [InlineData("<a href=\"#top\">Back to top</a>", "Back to top")]
    [InlineData("<a href=\"https://x.test/\">https://x.test</a>", "https://x.test/")]
    [InlineData("<a href=\"https://x.test/a\"><img src=\"logo.png\" alt=\"Logo\"></a>", "https://x.test/a")]
    [InlineData("<a href='mailto:ann@x.test?subject=Hi'>Write to Ann</a>", "Write to Ann (ann@x.test)")]
    [InlineData("<a href=\"mailto:ann@x.test\">ann@x.test</a>", "ann@x.test")]
    [InlineData("<p>Thanks, <a href=\"https://x.test/p\"><strong>Bob</strong></a>.</p>", "Thanks, Bob (https://x.test/p).")]
    [InlineData("<b>A &amp; B</b> &lt;script&gt;", "A & B <script>")]
    [InlineData("<p>&nbsp;</p>", "")]
    public void Links_read_as_their_label_and_address_and_typed_text_stays_text(string html, string expected)
    {
        Assert.Equal(expected, EmailPlainText.FromHtml(html));
    }

    [Fact]
    public void The_callers_own_text_is_kept_and_the_html_is_read_only_when_there_is_none()
    {
        Assert.Equal("mine", EmailPlainText.Ensure("mine", "<p>x</p>"));
        Assert.Equal("x", EmailPlainText.Ensure(null, "<p>x</p>"));
        Assert.Equal("x", EmailPlainText.Ensure("  ", "<p>x</p>"));
        Assert.Equal(string.Empty, EmailPlainText.Ensure(null, null));
    }

    [Fact]
    public async Task Azure_and_SendGrid_send_the_text_part_beside_the_html()
    {
        const string html = "<p>Hello Ann,</p><p><a href=\"https://x.test/i\">View invoice</a></p>";
        const string text = "Hello Ann,\n\nView invoice (https://x.test/i)";

        EmailMessage? azure = null;
        var azureService = BuildAzure(m => { azure = m; return Task.FromResult("op-1"); });
        Assert.True((await azureService.SendDetailedAsync("ann@example.test", "Ann", "s", html)).Success);
        Assert.Equal(text, azure!.Content.PlainText);
        await azureService.SendDetailedAsync("ann@example.test", "Ann", "s", html, "written by hand");
        Assert.Equal("written by hand", azure!.Content.PlainText);

        var sendGrid = new CapturingSendGridClient();
        Assert.True((await BuildSendGrid(sendGrid).SendDetailedAsync("ann@example.test", "Ann", "s", html)).Success);
        Assert.Equal(text, sendGrid.LastMessage!.Contents.Single(c => c.Type == "text/plain").Value);
        Assert.Equal(html, sendGrid.LastMessage.Contents.Single(c => c.Type == "text/html").Value);
    }

    // ---- the letter ------------------------------------------------------------------------------------

    [Fact]
    public void A_letter_greets_the_client_and_signs_off_with_the_advisers_name_phone_email_and_address()
    {
        var agent = Adviser();
        var html = ClientLetter.Html(agent, "Bob", new[] { "Here is <strong>it</strong>." }, "View invoice", "https://app.test/invoice/t",
            ClientLetter.QuestionsLine(agent));

        Assert.Equal(
            "Global Business Solution\n\nHi Bob,\n\nHere is it.\n\nView invoice (https://app.test/invoice/t)\n\n"
            + "If you have any questions, just reply to this email or call 416-555-1212.\n\n" + SignOffText + "\n\n"
            + "Sent with iPro (https://www.iproadvisers.com/)",
            EmailPlainText.FromHtml(html));
    }

    [Fact]
    public void What_a_person_typed_never_becomes_markup()
    {
        var agent = Adviser();
        agent.CompanyName = "<b>Acme</b> & Co";
        agent.Designation = "<i>CFP</i>";
        agent.Phone = "<x>";
        var html = ClientLetter.Html(agent, "<script>alert(1)</script>", Array.Empty<string>(), "Go", "https://app.test/g", ClientLetter.QuestionsLine(agent));

        Assert.DoesNotContain("<b>Acme", html);
        Assert.DoesNotContain("<i>CFP", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<x>", html);
        Assert.Contains("&lt;b&gt;Acme&lt;/b&gt; &amp; Co", html);
        Assert.Contains("Hi &lt;script&gt;", html);
    }

    [Fact]
    public void A_letter_leaves_out_what_is_not_on_file()
    {
        var agent = new AgentUser { Email = "solo@example.test", BusinessType = "Mortgage" };   // no name, business, phone or address
        var html = ClientLetter.Html(agent, "  ", new[] { "Body.", "" }, closing: ClientLetter.QuestionsLine(agent));

        Assert.Equal(
            "Hello,\n\nBody.\n\nIf you have any questions, just reply to this email.\n\nBest regards,\nsolo@example.test\n\n"
            + "Sent with iPro (https://www.ipromortgages.com/)",
            EmailPlainText.FromHtml(html));

        var footerBelow = EmailPlainText.FromHtml(ClientLetter.Html(Adviser(), "Bob", new[] { "Body." }, withAddress: false, sentWith: false));
        Assert.DoesNotContain("Yonge", footerBelow);
        Assert.DoesNotContain("Sent with", footerBelow);
        Assert.EndsWith($"416-555-1212 {Dot} bm@example.test", footerBelow);
    }

    // ---- the emails --------------------------------------------------------------------------------------

    [Fact]
    public void An_invoice_email_says_what_it_is_for_and_when_it_is_due()
    {
        var invoice = Invoice(ClientInvoiceDocumentType.Invoice, ClientInvoiceStatus.Draft, "INV-1014", 357m,
            "Bookkeeping, September", "Year-end review\nwith the notes", "Tax filing", "Payroll", "Advice");

        Assert.Equal(
            "Global Business Solution\n\nHi Bob,\n\n"
            + "Here is invoice INV-1014 for $357.00 CAD, dated October 2, 2026, due October 16, 2026.\n\n"
            + "For: Bookkeeping, September; Year-end review; Tax filing; and 2 more.\n\n"
            + "View invoice (https://app.test/invoice/t)\n\n"
            + "If you have any questions, just reply to this email or call 416-555-1212.\n\n" + SignOffText + "\n\n"
            + "Sent with iPro (https://www.iproadvisers.com/)",
            EmailPlainText.FromHtml(IPRO.Scheduler.ClientInvoiceEmail.Html(invoice, "https://app.test/invoice/t")));
    }

    [Fact]
    public void An_estimate_asks_for_approval_and_a_paid_invoice_goes_as_a_copy()
    {
        var estimate = EmailPlainText.FromHtml(IPRO.Scheduler.ClientInvoiceEmail.Html(
            Invoice(ClientInvoiceDocumentType.Estimate, ClientInvoiceStatus.Draft, "EST-1003", 1009m, "test"), "https://app.test/invoice/e"));
        Assert.Contains("Here is estimate EST-1003 for $1,009.00 CAD, dated October 2, 2026.\n\nFor: test.\n\nYou can review the estimate and approve it online.\n\nView estimate (https://app.test/invoice/e)", estimate);
        Assert.DoesNotContain("due", estimate);

        var paid = EmailPlainText.FromHtml(IPRO.Scheduler.ClientInvoiceEmail.Html(
            Invoice(ClientInvoiceDocumentType.Invoice, ClientInvoiceStatus.Paid, "INV-1", 100m, new string('x', 70)), "https://app.test/invoice/p"));
        Assert.Contains("Here is a copy of invoice INV-1 for $100.00 CAD, dated October 2, 2026. It has been paid in full. Thank you.", paid);
        Assert.Contains($"For: {new string('x', 57)}....", paid);    // clipped to 60 characters with its dots
        Assert.DoesNotContain("due", paid);
    }

    [Fact]
    public void A_reminder_is_a_letter_and_an_advisers_own_greeting_is_not_said_twice()
    {
        var invoice = Invoice(ClientInvoiceDocumentType.Invoice, ClientInvoiceStatus.Sent, "INV-9", 100m);
        invoice.Client!.FirstName = "Ann";

        var (_, html) = IPRO.Scheduler.ClientInvoiceReminderEmail.Build(invoice, "https://app.test/invoice/t", ClientInvoiceReminderSchedule.Defaults(1), new DateTime(2026, 10, 30));
        var text = EmailPlainText.FromHtml(html);
        Assert.StartsWith("Global Business Solution\n\nHi Ann,\n\nThis is a reminder that invoice INV-9 for ", text);
        Assert.Contains("is now overdue.\n\nView Invoice (https://app.test/invoice/t)\n\nIf you have already paid, thank you. If you have any questions, just reply to this email or call 416-555-1212.", text);
        Assert.EndsWith(SignOffText + "\n\nSent with iPro (https://www.iproadvisers.com/)", text);

        var own = new ClientInvoiceReminderSettings { AgentUserId = 1, OverdueMessage = "Hi {client}, please settle {invoice}." };
        var (_, ownHtml) = IPRO.Scheduler.ClientInvoiceReminderEmail.Build(invoice, "https://app.test/invoice/t", own, new DateTime(2026, 10, 30));
        var ownText = EmailPlainText.FromHtml(ownHtml);
        Assert.StartsWith("Global Business Solution\n\nHi Ann, please settle INV-9.\n\n", ownText);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(ownText, "Hi Ann"));
    }

    [Fact]
    public async Task The_send_puts_the_invoices_items_and_the_advisers_sign_off_in_the_email()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var rule = new BillingRule { PackageName = ($"T542-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = Adviser();
        agent.UserName = ($"t542-{Guid.NewGuid():N}")[..20];
        agent.Email = $"{Guid.NewGuid():N}@example.test";
        agent.DomainName = ($"t542-{Guid.NewGuid():N}")[..24];
        agent.PackageId = rule.Id;
        db.Add(agent);
        await db.SaveChangesAsync();
        var client = new Client { AgentUserId = agent.Id, FirstName = "Bob", LastName = "Moore", Email = $"{Guid.NewGuid():N}@example.test" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var invoice = new ClientInvoice
        {
            AgentUserId = agent.Id, ClientId = client.Id, DocumentType = ClientInvoiceDocumentType.Invoice, Status = ClientInvoiceStatus.Draft,
            DocumentNumber = ($"B-{Guid.NewGuid():N}")[..20], Total = 250m, Currency = "CAD", ViewToken = Guid.NewGuid().ToString("N"),
            IssueDate = new DateTime(2026, 10, 2), DueDate = new DateTime(2026, 10, 16),
            LineItems =
            {
                new ClientInvoiceLineItem { Description = "Year-end review", Quantity = 1, UnitPrice = 50m, Amount = 50m, SortOrder = 2 },
                new ClientInvoiceLineItem { Description = "Bookkeeping", Quantity = 1, UnitPrice = 200m, Amount = 200m, SortOrder = 1 }
            }
        };
        db.ClientInvoices.Add(invoice);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var email = new RecordingEmail();
        await NewInvoicesController(db, email, agent.Id).Send(invoice.Id);

        var sent = Assert.Single(email.Sent);
        var text = EmailPlainText.FromHtml(sent.Html);
        Assert.Contains("Hi Bob,", text);
        Assert.Contains("For: Bookkeeping; Year-end review.", text);
        Assert.Contains($"Best regards,\nBahman Motamed\nGlobal Business Solution\n416-555-1212 {Dot} {agent.Email}\n", text);
    }

    [Fact]
    public void Every_client_email_is_a_letter_and_every_provider_sends_a_text_part()
    {
        Assert.Contains("IPRO.Scheduler.ClientInvoiceEmail.Html(", Read(@"src\IPRO.Web\Controllers\ClientInvoicesController.cs"));
        Assert.Contains("ClientLetter.Html(", Read(@"src\IPRO.Scheduler\ClientInvoiceEmail.cs"));
        Assert.Contains("ClientLetter.Html(", Read(@"src\IPRO.Scheduler\ClientInvoiceReminderEmail.cs"));
        Assert.Contains("ClientLetter.Html(", Read(@"src\IPRO.Web\Controllers\ClientsController.cs"));           // the portal invitation
        var testimonials = Read(@"src\IPRO.Web\Controllers\TestimonialsController.cs");
        Assert.Contains("ClientLetter.Html(", testimonials);
        Assert.Contains("SenderFooter.AppendHtml(", testimonials);                                               // 533's footer stays
        Assert.Equal(2, Count(Read(@"src\IPRO.Web\Controllers\PortalRequestsController.cs"), "ClientLetter.Html("));  // scheduled, declined

        Assert.Equal(1, Count(Read(@"src\IPRO.Email\SesEmailService.cs"), "EmailPlainText.Ensure("));
        Assert.Equal(1, Count(Read(@"src\IPRO.Email\AzureEmailService.cs"), "EmailPlainText.Ensure("));
        Assert.Equal(2, Count(Read(@"src\IPRO.Email\SendGridEmailService.cs"), "EmailPlainText.Ensure("));

        // Where replies go is said where the address is set.
        Assert.Contains("your clients' replies come to it", Read(@"src\IPRO.Web\Views\Account\Profile.cshtml"));
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static readonly string SignOffText =
        $"Best regards,\nBahman Motamed\nGlobal Business Solution\n416-555-1212 {Dot} bm@example.test\n3230 Yonge Street Suite 2005, Toronto Ontario M4N 3P6, Canada";

    private static AgentUser Adviser() => new()
    {
        FirstName = "Bahman", LastName = "Motamed", CompanyName = "Global Business Solution",
        Phone = "416-555-1212", Email = "bm@example.test", BusinessType = "Generic",
        CompanyAddress = "3230 Yonge Street Suite 2005", City = "Toronto", Province = "Ontario", PostalCode = "M4N 3P6", Country = "Canada"
    };

    private static ClientInvoice Invoice(ClientInvoiceDocumentType type, ClientInvoiceStatus status, string number, decimal total, params string[] items)
    {
        var invoice = new ClientInvoice
        {
            DocumentType = type, Status = status, DocumentNumber = number, Total = total, Currency = "CAD",
            IssueDate = new DateTime(2026, 10, 2), DueDate = new DateTime(2026, 10, 16),
            Client = new Client { FirstName = "Bob", LastName = "Moore" },
            AgentUser = Adviser()
        };
        for (var i = 0; i < items.Length; i++)
            invoice.LineItems.Add(new ClientInvoiceLineItem { Description = items[i], SortOrder = i + 1 });
        return invoice;
    }

    private static int Count(string source, string needle)
    {
        var count = 0;
        for (var at = source.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = source.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }

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

    private static AzureEmailService BuildAzure(Func<EmailMessage, Task<string>> sendCore)
    {
        var settings = Options.Create(new EmailSettings
        {
            Provider = "Azure",
            AzureCommunicationConnectionString = "endpoint=https://x.canada.communication.azure.com/;accesskey=abc",
            FromEmail = "support@iproadvisers.com",
            FromName = "IPRO Advisers"
        });
        var service = new AzureEmailService(settings, NullLogger<AzureEmailService>.Instance, new EmailSendGate(settings));
        service.ClientFactory = _ => new StubEmailClient(sendCore);
        return service;
    }

    private static SendGridEmailService BuildSendGrid(ISendGridClient client)
    {
        var settings = Options.Create(new EmailSettings
        {
            Provider = "SendGrid",
            SendGridApiKey = "SG.test-key",
            FromEmail = "support@iproadvisers.com",
            FromName = "IPRO Advisers"
        });
        var service = new SendGridEmailService(settings, NullLogger<SendGridEmailService>.Instance);
        service.ClientFactory = _ => client;
        return service;
    }

    private sealed class RecordingEmail : IEmailService
    {
        public List<(string To, string Subject, string Html, string? Text)> Sent { get; } = new();
        public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;
        public Task<IPRO.Email.EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        {
            Sent.Add((toEmail, subject, htmlBody, textBody));
            return Task.FromResult(IPRO.Email.EmailSendResult.Sent($"acs-{Sent.Count}"));
        }
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
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

    private sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class StubEmailClient : EmailClient
    {
        private readonly Func<EmailMessage, Task<string>> _sendCore;
        public StubEmailClient(Func<EmailMessage, Task<string>> sendCore) => _sendCore = sendCore;

        public override async Task<EmailSendOperation> SendAsync(WaitUntil wait, EmailMessage message, CancellationToken cancellationToken = default)
        {
            var id = await _sendCore(message);
            return new StubOperation(id);
        }

        private sealed class StubOperation : EmailSendOperation
        {
            private readonly string _id;
            public StubOperation(string id) => _id = id;
            public override string Id => _id;
        }
    }

    private sealed class CapturingSendGridClient : ISendGridClient
    {
        public SendGridMessage? LastMessage;

        public string UrlPath { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string MediaType { get; set; } = string.Empty;

        public System.Net.Http.Headers.AuthenticationHeaderValue AddAuthorization(KeyValuePair<string, string> header) => new("Bearer", "test");

        public Task<SendGrid.Response> MakeRequest(HttpRequestMessage request, CancellationToken cancellationToken = default) => Task.FromResult(Accepted());

        public Task<SendGrid.Response> RequestAsync(BaseClient.Method method, string? requestBody = null,
            string? queryParams = null, string? urlPath = null, CancellationToken cancellationToken = default) => Task.FromResult(Accepted());

        public Task<SendGrid.Response> SendEmailAsync(SendGridMessage msg, CancellationToken cancellationToken = default)
        {
            LastMessage = msg;
            return Task.FromResult(Accepted());
        }

        private static SendGrid.Response Accepted()
        {
            var carrier = new HttpResponseMessage(HttpStatusCode.Accepted);
            carrier.Headers.TryAddWithoutValidation("X-Message-Id", "stub-message-id");
            return new SendGrid.Response(HttpStatusCode.Accepted, new StringContent(string.Empty), carrier.Headers);
        }
    }
}
