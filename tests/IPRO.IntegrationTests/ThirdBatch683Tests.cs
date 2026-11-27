using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.Admin.Controllers;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 683, the whole-code audit of 2026-11-26: five more confirmed Medium findings.
//   A02-01  a setup fee set to $0 in SuperAdmin came back at the next start of either app
//   A02-02  the Refund Queue dropped a refund whose adviser had been deleted
//   DAT-1   one SuperAdmin export quoted its cells but let a formula through
//   ACC-4   a client could respell their sign-in address without the password, or take another client's
//   W02-08  saving a custom form deleted its questions, committed, then put them back one by one
public class ThirdBatch683Tests
{
    // ---------------------------------------------------------------- A02-01

    [Fact]
    public async Task A_packages_setup_fee_and_limits_stay_as_SuperAdmin_set_them()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await PackageEntitlementSeeder.SeedAsync(db);
        var gold = await db.BillingRules.SingleAsync(r => r.PackageName == "IPro Gold");
        Assert.True(gold.SetupFee > 0, "a new package starts with its seeded setup fee");
        var seededClients = gold.MaxClients;

        gold.SetupFee = 0m;        // SuperAdmin -> Packages: no setup fee on Gold
        gold.MaxClients = 0;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await PackageEntitlementSeeder.SeedAsync(db);   // every start of the portal or SuperAdmin
        db.ChangeTracker.Clear();

        var after = await db.BillingRules.AsNoTracking().SingleAsync(r => r.PackageName == "IPro Gold");
        Assert.Equal(0m, after.SetupFee);               // was written back to the seeded fee
        Assert.Equal(0, after.MaxClients);
        Assert.True(seededClients != 0 || after.MaxClients == 0);
    }

    // ---------------------------------------------------------------- A02-02

    [Fact]
    public async Task The_refund_queue_keeps_a_refund_whose_adviser_was_deleted()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: true);   // the ledger outlives its adviser, as in production
        await using var db = testDb.CreateContext();
        var rule = new BillingRule { PackageName = ($"T683c-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        AgentUser Adviser(string first) => new()
        {
            UserName = ($"t683c-{Guid.NewGuid():N}")[..20], Email = $"{Guid.NewGuid():N}@example.test", FirstName = first, LastName = "Reid",
            DomainName = ($"t683c-{Guid.NewGuid():N}")[..24], Province = "Ontario", Country = "Canada", PackageId = rule.Id, IsActive = true
        };
        var staying = Adviser("Staying");
        var leaving = Adviser("Leaving");
        db.AddRange(staying, leaving);
        await db.SaveChangesAsync();
        SubscriptionChange Owed(AgentUser adviser, decimal gross) => new()
        {
            AgentUserId = adviser.Id, CurrentBillingRuleId = rule.Id, RequestedBillingRuleId = rule.Id, ChangeType = SubscriptionChangeType.Cancel,
            Status = SubscriptionChangeStatus.Applied, EffectiveDate = DateTime.UtcNow, RefundStatus = RefundStatus.Pending,
            RefundNetAmount = gross, RefundGrossAmount = gross, Currency = "CAD"
        };
        db.AddRange(Owed(staying, 45.20m), Owed(leaving, 113.00m));
        await db.SaveChangesAsync();
        var leavingId = leaving.Id;

        // The adviser's account is deleted; the money record stays (no foreign key holds it).
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET FOREIGN_KEY_CHECKS = 0");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM `AgentUsers` WHERE `Id` = {0}", leavingId);
        await db.Database.ExecuteSqlRawAsync("SET FOREIGN_KEY_CHECKS = 1");
        await db.Database.CloseConnectionAsync();
        db.ChangeTracker.Clear();

        var controller = Admin(new RefundsController(db, new NoAudit(), new NoEmail(), new ConfigurationBuilder().Build(), NullLogger<RefundsController>.Instance));
        var rows = Assert.IsAssignableFrom<IEnumerable<SubscriptionChange>>(Assert.IsType<ViewResult>(await controller.Index()).Model).ToList();

        Assert.Equal(2, rows.Count);                                             // the deleted adviser's refund had vanished
        var orphan = Assert.Single(rows, r => r.AgentUserId == leavingId);
        Assert.Null(orphan.AgentUser);                                           // the page shows "#id" for it
        Assert.Equal(113.00m, orphan.RefundGrossAmount);
        Assert.Equal("Staying", Assert.Single(rows, r => r.AgentUserId == staying.Id).AgentUser.FirstName);
    }

    // ---------------------------------------------------------------- DAT-1

    [Fact]
    public void Both_SuperAdmin_exports_turn_a_formula_into_text()
    {
        Assert.Equal("'=HYPERLINK(1)", ReferralsController.Cell("=HYPERLINK(1)"));
        Assert.Equal("'@SUM(A1)", ReferralsController.Cell("@SUM(A1)"));
        Assert.Equal("\"Doe, Jane\"", ReferralsController.Cell("Doe, Jane"));
        Assert.Equal("-12.50", ReferralsController.Cell("-12.50"));              // a number stays a number
        Assert.Equal("", ReferralsController.Cell(null));

        var reports = Read(@"src\IPRO.Admin\Controllers\ReportsController.cs");
        Assert.Contains("static string Csv(string? value) => IPRO.Utility.CsvCell.Escape(value);", reports);
        Assert.DoesNotContain("? $\"\\\"{value.Replace(\"\\\"\", \"\\\"\\\"\")}\\\"\"", reports);
        Assert.Contains("internal static string Cell(string? value) => IPRO.Utility.CsvCell.Escape(value);", Read(@"src\IPRO.Admin\Controllers\ReferralsController.cs"));
    }

    // ---------------------------------------------------------------- ACC-4

    [Fact]
    public async Task A_client_cannot_respell_their_sign_in_address_without_the_password_or_take_another_clients()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var rule = new BillingRule { PackageName = ($"T683c-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var adviser = new AgentUser
        {
            UserName = ($"t683c-{Guid.NewGuid():N}")[..20], Email = $"{Guid.NewGuid():N}@example.test", FirstName = "Morgan", LastName = "Reid",
            DomainName = ($"t683c-{Guid.NewGuid():N}")[..24], Province = "Ontario", Country = "Canada", PackageId = rule.Id, IsActive = true
        };
        db.Add(adviser);
        await db.SaveChangesAsync();
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<Client>();
        var john = new Client { AgentUserId = adviser.Id, FirstName = "John", LastName = "Smith", Email = "john.smith@gmail.com" };
        john.PortalPasswordHash = hasher.HashPassword(john, "Correct-horse-683");
        var mary = new Client { AgentUserId = adviser.Id, FirstName = "Mary", LastName = "Jones", Email = "mary.jones@example.test" };
        db.Clients.AddRange(john, mary);
        await db.SaveChangesAsync();

        PortalProfileViewModel Form(string email, string? password = null) => new() { FirstName = "John", LastName = "Smith", Email = email, CurrentPassword = password };

        // The same Gmail mailbox spelled another way IS another sign-in: the password is asked.
        var respell = Profile(db, john.Id);
        Assert.IsType<ViewResult>(await respell.Index(Form("johnsmith@gmail.com")));
        Assert.Equal("Enter your current password to change your email.", respell.ModelState[nameof(PortalProfileViewModel.CurrentPassword)]!.Errors.Single().ErrorMessage);
        Assert.Equal("john.smith@gmail.com", (await db.Clients.AsNoTracking().SingleAsync(c => c.Id == john.Id)).Email);

        // Another client's address, with the right password: refused.
        var taken = Profile(db, john.Id);
        Assert.IsType<ViewResult>(await taken.Index(Form("Mary.Jones@example.test", "Correct-horse-683")));
        Assert.StartsWith("That email address cannot be used here.", taken.ModelState[nameof(PortalProfileViewModel.Email)]!.Errors.Single().ErrorMessage);
        Assert.Equal("john.smith@gmail.com", (await db.Clients.AsNoTracking().SingleAsync(c => c.Id == john.Id)).Email);

        // A free address with the right password still changes.
        var fine = Profile(db, john.Id);
        Assert.IsType<RedirectToActionResult>(await fine.Index(Form("john@new.example", "Correct-horse-683")));
        Assert.Equal("john@new.example", (await db.Clients.AsNoTracking().SingleAsync(c => c.Id == john.Id)).Email);

        // Nothing but the name changed: no password is asked.
        var nameOnly = Profile(db, mary.Id);
        Assert.IsType<RedirectToActionResult>(await nameOnly.Index(new PortalProfileViewModel { FirstName = "Marie", LastName = "Jones", Email = "mary.jones@example.test" }));
        Assert.Equal("Marie", (await db.Clients.AsNoTracking().SingleAsync(c => c.Id == mary.Id)).FirstName);
    }

    // ---------------------------------------------------------------- W02-08

    [Fact]
    public void A_form_and_its_questions_are_saved_in_one_transaction()
    {
        var forms = Read(@"src\IPRO.Web\Controllers\FormsController.cs").Replace("\r\n", "\n");
        Assert.Equal(2, forms.Split("await using (var saving = await _db.Database.BeginTransactionAsync())").Length - 1);
        Assert.Equal(2, forms.Split("            await SaveFieldsAsync(form.Id, model);\n            await saving.CommitAsync();").Length - 1);
        // The delete of the old questions is inside it, not committed on its own first.
        var edit = forms[forms.IndexOf("the old questions go and the new ones arrive in one transaction", StringComparison.Ordinal)..];
        Assert.True(edit.IndexOf("_db.WebsiteFormFields.RemoveRange(existingFields);", StringComparison.Ordinal) < edit.IndexOf("await saving.CommitAsync();", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- harness

    private static ClientPortalProfileController Profile(IPRODbContext db, int clientId)
    {
        var controller = new ClientPortalProfileController(db,
            new EmailConsentService(db, new ConfigurationBuilder().Build(), NullLogger<EmailConsentService>.Instance, Array.Empty<IUnsubscribeNotifier>()),
            new Microsoft.AspNetCore.Identity.PasswordHasher<Client>(), new NoEmail());
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, clientId.ToString(CultureInfo.InvariantCulture)) }, "ClientPortal"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, new NoTempData());
        return controller;
    }

    private static T Admin<T>(T controller) where T : Controller
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Name, "owner") }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, new NoTempData());
        return controller;
    }

    private sealed class NoAudit : IAdminAuditLogService
    {
        public Task LogAsync(int adminUserId, string adminUsername, string action, string details) => Task.CompletedTask;
    }

    private sealed class NoEmail : IEmailService
    {
        public Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            Task.FromResult(true);
        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            Task.FromResult(EmailSendResult.Sent());
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
