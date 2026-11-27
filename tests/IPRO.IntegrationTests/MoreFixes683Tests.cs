using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 683, the whole-code audit of 2026-11-26: eight more confirmed Medium findings.
//   W01-04  sign-up started on Alberta, so an untouched form charged 5% tax where 13% was due
//   W01-05  Submit Registration could be pressed twice: "An account already exists" for the account just made
//   W03-03  an appointment request with a long note could be sent and then never scheduled
//   D01-05  a picture used by an article copied into a newsletter could be deleted
//   W04-01  the invoice export offered to a locked-out adviser was refused by its own plan check
//   W05-07  a form link in a campaign used a custom domain that was only typed
//   W02-04  an over-long contact field answered an error page; one long cell failed a whole import
//   W02-07  restoring a deleted contact skipped the contact limit and the one-address rule
public class MoreFixes683Tests
{
    // ---------------------------------------------------------------- W01-04, W01-05

    [Fact]
    public void Sign_up_asks_for_the_province_and_takes_one_press()
    {
        Assert.Equal(string.Empty, new AgentRegistrationViewModel().Province);

        var view = Read(@"src\IPRO.Web\Views\Account\Register.cshtml").Replace("\r\n", "\n");
        Assert.Contains("<select asp-for=\"Province\" class=\"medium\" required>\n            <option value=\"\">Choose your province</option>\n            <option>Alberta</option>", view);
        // The server asks too (it always did; the default had made it pass).
        Assert.Contains("if (string.IsNullOrWhiteSpace(model.Province)) ModelState.AddModelError(\"\", \"Province is required.\");", Read(@"src\IPRO.Web\Controllers\AccountController.cs"));

        Assert.Contains("<button class=\"submit-button\" type=\"submit\" data-pressed=\"Creating your account...\">", view);
        Assert.Contains("  button.form.addEventListener('submit', function () {\n    button.disabled = true;\n    button.textContent = button.getAttribute('data-pressed');\n  });", view);
        Assert.Contains("window.addEventListener('pageshow', function () { button.disabled = false; button.innerHTML = words; });", view);
    }

    // ---------------------------------------------------------------- W03-03

    [Fact]
    public async Task An_appointment_request_with_a_long_note_can_be_scheduled()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var client = new Client { AgentUserId = agentId, FirstName = new string('F', 80), LastName = new string('L', 80), Email = $"{Guid.NewGuid():N}@example.test" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        // As a request could be stored before the limit: 1,800 characters in a column of 2,000.
        var request = new PortalAppointmentRequest { ClientId = client.Id, Notes = new string('n', 1800), PreferredDate = new DateTime(2026, 12, 1) };
        db.PortalAppointmentRequests.Add(request);
        await db.SaveChangesAsync();

        var controller = Prepared(new PortalRequestsController(db, new RecordingEmail(), new Plan(), new ConfigurationBuilder().Build()), agentId);
        Assert.IsType<RedirectToActionResult>(await controller.Schedule(request.Id, new DateTime(2026, 12, 1, 15, 0, 0)));   // an error page before, every time

        db.ChangeTracker.Clear();
        var followUp = await db.ClientFollowUps.AsNoTracking().SingleAsync(f => f.ClientId == client.Id);
        Assert.Equal(PortalAppointmentRequest.MaxNotesLength, followUp.Notes.Length);
        Assert.Equal(160, followUp.Title.Length);
        Assert.StartsWith("Appointment: FFFF", followUp.Title);
        Assert.Equal(PortalAppointmentRequestStatus.Scheduled, (await db.PortalAppointmentRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).Status);

        // And a new request is cut as it is made, with the box saying how much it takes.
        Assert.Equal(1000, PortalAppointmentRequest.FitNotes(new string('n', 5000))!.Length);
        Assert.Equal("See you then", PortalAppointmentRequest.FitNotes("  See you then  "));
        Assert.Null(PortalAppointmentRequest.FitNotes(null));
        Assert.Contains("Notes = PortalAppointmentRequest.FitNotes(notes),", Read(@"src\IPRO.Web\Controllers\ClientPortalAppointmentsController.cs"));
        Assert.Contains("name=\"notes\" class=\"form-control\" maxlength=\"@IPRO.Entities.PortalAppointmentRequest.MaxNotesLength\"", Read(@"src\IPRO.Web\Views\ClientPortalAppointments\Index.cshtml"));
        Assert.Equal(PortalAppointmentRequest.MaxNotesLength, db.Model.FindEntityType(typeof(ClientFollowUp))!.FindProperty(nameof(ClientFollowUp.Notes))!.GetMaxLength());
    }

    // ---------------------------------------------------------------- D01-05

    [Fact]
    public async Task A_picture_an_article_copied_into_a_newsletter_uses_is_kept()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var cover = $"https://storage.example.test/article-media/{Guid.NewGuid():N}.jpg";
        var inline = $"https://storage.example.test/article-media/{Guid.NewGuid():N}.png";
        var unused = $"https://storage.example.test/article-media/{Guid.NewGuid():N}.jpg";
        var newsletter = new NewsLetter { AgentUserId = agentId, Subject = "Spring", HtmlBody = "<p>Hello</p>" };
        db.NewsLetters.Add(newsletter);
        await db.SaveChangesAsync();
        db.NewsLetterArticles.Add(new NewsLetterArticle { NewsLetterId = newsletter.Id, Title = "From the library", ImageUrl = cover, Content = $"<p><img src=\"{inline}\"/></p>" });
        await db.SaveChangesAsync();

        Assert.True(await BlobReferences.IsReferencedAsync(db, cover));     // was "not used": the article's cover could be deleted
        Assert.True(await BlobReferences.IsReferencedAsync(db, inline));
        Assert.False(await BlobReferences.IsReferencedAsync(db, unused));
        Assert.Contains(("NewsLetterArticles", "ImageUrl"), BlobReferences.UrlColumns);
        Assert.Contains(("NewsLetterArticles", "Content"), BlobReferences.ContentColumns);
    }

    // ---------------------------------------------------------------- W04-01

    [Fact]
    public async Task An_adviser_whose_plan_has_no_invoicing_can_still_download_their_invoices()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var client = new Client { AgentUserId = agentId, FirstName = "Ava", LastName = "Chen", Email = $"{Guid.NewGuid():N}@example.test" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        db.ClientInvoices.Add(new ClientInvoice
        {
            AgentUserId = agentId, ClientId = client.Id, DocumentType = ClientInvoiceDocumentType.Invoice, Status = ClientInvoiceStatus.Sent, DocumentNumber = "INV-900",
            ViewToken = Guid.NewGuid().ToString("N"), IssueDate = new DateTime(2026, 11, 20), SubTotal = 100m, Total = 113m, TaxAmount = 13m, Currency = "CAD"
        });
        await db.SaveChangesAsync();

        // A lapsed subscription, or a plan without invoicing: the feature is not included.
        var lockedOut = new Plan { Included = false };
        var controller = Prepared(new ClientInvoicesController(db, lockedOut, new ClientInvoiceService(new UnitOfWork(db)), new RecordingEmail(), new ConfigurationBuilder().Build()), agentId);

        var file = Assert.IsType<FileContentResult>(await controller.Export());                 // was sent back to Billing with no file
        Assert.Contains("INV-900", Encoding.UTF8.GetString(file.FileContents));
        Assert.IsType<RedirectToActionResult>(await controller.ExportQuickBooks());               // the accounting formats stay with the plan
        Assert.IsType<RedirectToActionResult>(await controller.Index());
    }

    // ---------------------------------------------------------------- W05-07

    [Fact]
    public async Task A_form_link_uses_a_custom_domain_only_while_it_is_serving()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await WebsiteTemplateSeeder.SeedAsync(db);
        var agentId = await SeedAgentAsync(db);
        var free = (await db.AgentUsers.AsNoTracking().SingleAsync(a => a.Id == agentId)).DomainName;
        var template = await db.WebsiteTemplates.FirstAsync(t => t.TemplateKey == WebsiteTemplateSeeder.DefaultTemplateKey);
        var website = new AgentWebsite { AgentUserId = agentId, TemplateId = template.Id, CustomDomain = "www.typed-683.example", CustomDomainDisplay = "www.Typed-683.example", IsPublished = true };
        db.AgentWebsites.Add(website);
        await db.SaveChangesAsync();

        // Only typed: nothing proves it is theirs or that it answers.
        Assert.Equal(free, await AgentSiteAddress.ServingHostAsync(db, agentId, free));

        var domain = new AgentDomain
        {
            AgentUserId = agentId, AgentWebsiteId = website.Id, DomainName = "www.typed-683.example", RootDomain = "typed-683.example", WwwDomain = "www.typed-683.example",
            DnsStatus = AgentDomainStatus.DnsReady, AzureBindingStatus = AgentDomainStatus.BindingPending, SslStatus = AgentDomainStatus.BindingPending,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, OwnershipVerifiedAt = DateTime.UtcNow
        };
        db.Add(domain);
        await db.SaveChangesAsync();
        Assert.Equal(free, await AgentSiteAddress.ServingHostAsync(db, agentId, free));          // proven, still being set up

        domain.AzureBindingStatus = AgentDomainStatus.Bound;
        domain.SslStatus = AgentDomainStatus.Bound;
        await db.SaveChangesAsync();
        Assert.Equal("www.Typed-683.example", await AgentSiteAddress.ServingHostAsync(db, agentId, free));
        Assert.Equal("www.Typed-683.example", await AgentSiteAddress.HostAsync(db, agentId, free));   // what clients are shown: unchanged

        // A kept own website is what clients are shown, never where a form of iPro's is.
        db.AgentOwnWebsites.Add(new AgentOwnWebsite { AgentUserId = agentId, WebsiteUrl = "https://www.their-own-683.example" });
        await db.SaveChangesAsync();
        Assert.Equal("www.their-own-683.example", await AgentSiteAddress.HostAsync(db, agentId, free));
        Assert.Equal("www.Typed-683.example", await AgentSiteAddress.ServingHostAsync(db, agentId, free));

        var campaigns = Read(@"src\IPRO.Web\Controllers\CampaignsController.cs");
        Assert.Contains("(await AgentSiteAddress.ServingHostAsync(_db, AgentId, website.AgentUser.DomainName)).ToLowerInvariant();", campaigns);
        Assert.DoesNotContain("website!.CustomDomain : website?.AgentUser.DomainName", campaigns);
    }

    // ---------------------------------------------------------------- W02-04

    [Fact]
    public void A_contacts_limits_are_its_columns()
    {
        using var db = new IPRODbContext(new DbContextOptionsBuilder<IPRODbContext>().UseMySql("Server=none;Database=none", new MySqlServerVersion(new Version(8, 0, 44))).Options);
        var client = db.Model.FindEntityType(typeof(Client))!;
        foreach (var (field, _, max, _) in ClientFieldLimits.Fields)
            Assert.True(client.FindProperty(field)!.GetMaxLength() == max, $"{field}: the list says {max}, the column {client.FindProperty(field)!.GetMaxLength()}");
        // Every limited text column of a contact that a person types is in the list.
        var typed = client.GetProperties().Where(p => p.ClrType == typeof(string) && p.GetMaxLength() is > 0 and <= 200).Select(p => p.Name).ToList();
        var listed = ClientFieldLimits.Fields.Select(f => f.Field).ToHashSet();
        // (What the system writes itself -- tokens, hashes, an opt-out's source -- is not typed by anyone.)
        var systemWritten = new[] { "Token", "Hash", "Source", "Password", "Google" };
        Assert.Empty(typed.Where(name => !listed.Contains(name) && !systemWritten.Any(word => name.Contains(word, StringComparison.Ordinal))));

        var long43 = "416-555-0100 ext 234 / 647-555-0111 (cell)";
        Assert.Equal(new[] { "Home number 1 can be at most 40 characters. This one has 42." },
            ClientFieldLimits.Problems(new Client { FirstName = "Ava", LastName = "Chen", Email = "ava@example.test", Phone = long43 }).ToArray());
        Assert.Empty(ClientFieldLimits.Problems(new Client { FirstName = "Ava", LastName = "Chen", Email = "ava@example.test", Phone = "416-555-0100" }));

        Assert.True(ClientFieldLimits.LooksLikeEmail("ava.chen+news@example.co.uk"));
        Assert.False(ClientFieldLimits.LooksLikeEmail("ava.chen"));
        Assert.False(ClientFieldLimits.LooksLikeEmail("ava@"));
        Assert.False(ClientFieldLimits.LooksLikeEmail("@example.test"));
        Assert.False(ClientFieldLimits.LooksLikeEmail("ava chen@example.test"));
        Assert.False(ClientFieldLimits.LooksLikeEmail("ava@@example.test"));

        // The forms say the limits, and an import leaves an over-long row out instead of failing.
        foreach (var view in new[] { Read(@"src\IPRO.Web\Views\Clients\Create.cshtml"), Read(@"src\IPRO.Web\Views\Clients\Edit.cshtml") })
        {
            Assert.Contains("<input asp-for=\"Phone\" class=\"form-control\"", view);
            foreach (var (field, _, max, _) in ClientFieldLimits.Fields.Where(f => f.Field != nameof(Client.Province)))
                Assert.Matches("<input asp-for=\"" + field + "\"[^>]* maxlength=\"" + max + "\"", view);
        }
        var controller = Read(@"src\IPRO.Web\Controllers\ClientsController.cs");
        Assert.Contains("if (ClientFieldLimits.Problems(client).FirstOrDefault() is { } problem)", controller);
        Assert.Contains("left out because a value is too long for its field", controller);
    }

    [Fact]
    public async Task An_over_long_value_is_said_on_the_form_instead_of_an_error_page()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);

        var controller = Clients(db, agentId, new Plan());
        var tooLong = await controller.Create(new Client { FirstName = "Ava", LastName = "Chen", Email = "ava@example.test", Phone = "416-555-0100 ext 234 / 647-555-0111 (cell)" }, Array.Empty<int>());
        Assert.IsType<ViewResult>(tooLong);
        Assert.Contains(controller.ModelState.Values.SelectMany(v => v.Errors), e => e.ErrorMessage == "Home number 1 can be at most 40 characters. This one has 42.");
        Assert.False(await db.Clients.AnyAsync(c => c.AgentUserId == agentId));

        var notAnAddress = Clients(db, agentId, new Plan());
        Assert.IsType<ViewResult>(await notAnAddress.Create(new Client { FirstName = "Ava", LastName = "Chen", Email = "ava.chen" }, Array.Empty<int>()));
        Assert.Contains(notAnAddress.ModelState.Values.SelectMany(v => v.Errors), e => e.ErrorMessage.StartsWith("Enter a valid email address", StringComparison.Ordinal));

        var fine = Clients(db, agentId, new Plan());
        Assert.IsType<RedirectToActionResult>(await fine.Create(new Client { FirstName = "Ava", LastName = "Chen", Email = "ava@example.test", Phone = "416-555-0100" }, Array.Empty<int>()));
        Assert.Equal(1, await db.Clients.CountAsync(c => c.AgentUserId == agentId));
    }

    // ---------------------------------------------------------------- W02-07

    [Fact]
    public async Task Restoring_a_contact_meets_the_contact_limit_and_the_one_address_rule()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var john = new Client { AgentUserId = agentId, FirstName = "John", LastName = "Reid", Email = "john.reid@example.test" };
        var mary = new Client { AgentUserId = agentId, FirstName = "Mary", LastName = "Reid", Email = "mary.reid@example.test" };
        db.Clients.AddRange(john, mary);
        await db.SaveChangesAsync();
        var johnsItem = await ClientRecycleBin.DeleteToBinAsync(db, john.Id);
        var marysItem = await ClientRecycleBin.DeleteToBinAsync(db, mary.Id);
        db.ChangeTracker.Clear();

        // John signs up again on the website while he is in the bin: restoring him made two rows with one address.
        db.Clients.Add(new Client { AgentUserId = agentId, FirstName = "John", LastName = "Reid", Email = "john.reid@example.test" });
        await db.SaveChangesAsync();
        var sameAddress = Clients(db, agentId, new Plan());
        var refused = Assert.IsType<RedirectToActionResult>(await sameAddress.Restore(johnsItem.Id));
        Assert.Equal(nameof(ClientsController.RecycleBin), refused.ActionName);
        Assert.Contains("was not restored: another contact already has the address john.reid@example.test", (string)sameAddress.TempData["Error"]!);
        Assert.Equal(1, await db.Clients.CountAsync(c => c.AgentUserId == agentId));
        Assert.True(await db.ClientRecycleBinItems.AnyAsync(i => i.Id == johnsItem.Id));

        // The plan's contact limit is reached: a restore is one more contact.
        var full = Clients(db, agentId, new Plan { ContactLimit = 1 });
        var overLimit = Assert.IsType<RedirectToActionResult>(await full.Restore(marysItem.Id));
        Assert.Equal(nameof(ClientsController.RecycleBin), overLimit.ActionName);
        Assert.StartsWith("Mary Reid was not restored.", (string)full.TempData["Error"]!);
        Assert.Equal(1, await db.Clients.CountAsync(c => c.AgentUserId == agentId));

        // With room and no one else at the address, she comes back as before.
        var room = Clients(db, agentId, new Plan());
        var restored = Assert.IsType<RedirectToActionResult>(await room.Restore(marysItem.Id));
        Assert.Equal(nameof(ClientsController.Details), restored.ActionName);
        Assert.Equal(2, await db.Clients.CountAsync(c => c.AgentUserId == agentId));

        // Someone else's bin is still not found.
        var other = await SeedAgentAsync(db);
        Assert.IsType<NotFoundResult>(await Clients(db, other, new Plan()).Restore(johnsItem.Id));
    }

    // ---------------------------------------------------------------- harness

    private static async Task<int> SeedAgentAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T683n-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t683n-{Guid.NewGuid():N}")[..20], Email = $"{Guid.NewGuid():N}@example.test", FirstName = "Morgan", LastName = "Reid",
            CompanyName = "Reid Advisory", DomainName = ($"t683n-{Guid.NewGuid():N}")[..20] + ".247advisers.test", CompanyAddress = "123 Main Street", City = "Toronto",
            Province = "Ontario", PostalCode = "M5V 1A1", Country = "Canada", TimeZone = "America/Toronto", PackageId = rule.Id, IsActive = true
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static T Prepared<T>(T controller, int agentId) where T : Controller
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString(CultureInfo.InvariantCulture)) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, new NoTempData());
        return controller;
    }

    private static ClientsController Clients(IPRODbContext db, int agentId, Plan plan)
    {
        var uow = new UnitOfWork(db);
        return Prepared(new ClientsController(new ClientService(uow, db), null!, uow, db, plan, null!, null!, null!, new EphemeralDataProtectionProvider(),
            new ConfigurationBuilder().Build(),
            new EmailConsentService(db, new ConfigurationBuilder().Build(), NullLogger<EmailConsentService>.Instance, Array.Empty<IUnsubscribeNotifier>())), agentId);
    }

    private sealed class Plan : IPackageEntitlementService
    {
        public bool Included { get; init; } = true;
        public int? ContactLimit { get; init; }
        public Task<PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) => Task.FromResult(new PackageFeatureAccess
        {
            FeatureCode = featureCode, IsIncluded = Included, CurrentPackageName = "Test plan",
            LimitValue = featureCode == PackageFeatureCodes.Contacts ? ContactLimit : null
        });
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => Task.FromResult(Included);
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode) =>
            Task.FromResult(agentIds.Distinct().ToDictionary(a => a, _ => Included));
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(!Included);
    }

    private sealed class RecordingEmail : IEmailService
    {
        public List<string> To { get; } = new();
        public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;
        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        {
            To.Add(toEmail);
            return Task.FromResult(EmailSendResult.Sent($"test-{To.Count}"));
        }
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
    }

    private sealed class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
