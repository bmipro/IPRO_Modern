using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using IPRO.Admin.Controllers;
using IPRO.Admin.Models;
using IPRO.Billing;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using EBilling = IPRO.Entities.Billing;

namespace IPRO.IntegrationTests;

// 600 (2026-10-24). The owner: "Refunds don't reduce Revenue and get no credit note -> Add credit notes and net them in
// Revenue and the CSV (matters for tax filing)." A refund marked Refunded in SuperAdmin -> Refunds (a cancellation's
// unused prepaid value, or money PayPal captured after a subscription had ended) is now a credit note: numbered from
// the one CN-YYYY-NNNN sequence the Refer a Friend credit notes use, emailed to the adviser, and netted -- with the
// referral ones -- in Revenue's totals, its months, its tax by region, its ledger and its CSV. Waived refunds get none.
public class CreditNotes600Tests
{
    private static readonly DateTime Oct24 = new(2026, 10, 24, 15, 0, 0, DateTimeKind.Utc);

    // ---- the number ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Refund_and_referral_credit_notes_share_one_sequence_that_never_collides_and_restarts_each_year()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var older = await PendingRefundAsync(db, "SALE-OLD");
        var first = await PendingRefundAsync(db, "SALE-A");
        var second = await PendingRefundAsync(db, "SALE-B");
        var nextYear = await PendingRefundAsync(db, "SALE-C");
        // Numbers already issued -- a referral's CN-2026-0003 and a refund's CN-2026-0007 -- and no counter row yet: the
        // first number taken must come after the highest in EITHER table (seeding from the referrals alone gives 0004,
        // and the counter would hand out 0007 a second time three numbers later).
        await EarnedReferralAsync(db, older.Adviser, "SALE-OLD", creditNote: "CN-2026-0003", paidAt: Oct24.AddDays(-20));
        await db.SubscriptionChanges.Where(c => c.Id == older.Refund.Id).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.RefundStatus, RefundStatus.Refunded).SetProperty(c => c.CreditNoteNumber, "CN-2026-0007")
            .SetProperty(c => c.CreditNoteIssuedAt, Oct24.AddDays(-10)));

        var refunded = await CreditNotes.MarkRefundedAsync(db, first.Refund.Id, "REFUND-A", Oct24);
        Assert.NotNull(refunded);
        Assert.Equal("CN-2026-0008", refunded!.CreditNoteNumber);

        var reward = await EarnedReferralAsync(db, first.Adviser, "SALE-A");
        var paid = await ReferralProgram.MarkPaidAsync(db, reward.Id, "REFUND-R", Oct24.AddMinutes(5));
        Assert.Equal("CN-2026-0009", paid!.CreditNoteNumber);

        var again = await CreditNotes.MarkRefundedAsync(db, second.Refund.Id, "REFUND-B", Oct24.AddMinutes(10));
        Assert.Equal("CN-2026-0010", again!.CreditNoteNumber);

        var january = await CreditNotes.MarkRefundedAsync(db, nextYear.Refund.Id, "REFUND-C", new DateTime(2027, 1, 4, 14, 0, 0, DateTimeKind.Utc));
        Assert.Equal("CN-2027-0001", january!.CreditNoteNumber);

        var all = (await db.SubscriptionChanges.AsNoTracking().Select(c => c.CreditNoteNumber).ToListAsync())
            .Concat(await db.Referrals.AsNoTracking().Select(r => r.CreditNoteNumber).ToListAsync())
            .Where(n => n != "").ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public async Task Marking_a_refund_refunded_issues_its_credit_note_once_and_waiving_issues_none()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var refund = await PendingRefundAsync(db, "SALE-600");
        var waived = await PendingRefundAsync(db, "SALE-601");
        var email = new RecordingEmail();
        var controller = Admin(new RefundsController(db, new RecordingAudit(), email, Config(), NullLogger<RefundsController>.Instance));

        await controller.MarkWaived(waived.Refund.Id, "Goodwill: the adviser kept the site to the end of the year");
        var waivedRow = await db.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.Id == waived.Refund.Id);
        Assert.Equal((RefundStatus.Waived, "", (DateTime?)null), (waivedRow.RefundStatus, waivedRow.CreditNoteNumber, waivedRow.CreditNoteIssuedAt));
        Assert.Empty(email.Sent);
        Assert.False(await db.NumberSequences.AnyAsync());   // not even a number taken

        await controller.MarkRefunded(refund.Refund.Id, "  REFUND-77 ");
        var row = await db.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.Id == refund.Refund.Id);
        Assert.Equal(RefundStatus.Refunded, row.RefundStatus);
        Assert.Equal($"CN-{DateTime.UtcNow:yyyy}-0001", row.CreditNoteNumber);
        Assert.NotNull(row.CreditNoteIssuedAt);
        Assert.Equal((refund.Invoice.InvoiceNumber, 0.13m, "ON HST", "REFUND-77"), (row.CreditNoteInvoiceNumber, row.CreditNoteTaxRate, row.CreditNoteTaxRegion, row.PayPalRefundId));
        Assert.Contains("REFUND-77", row.RefundResolutionNote);

        // A second click (or the back button) finds nothing waiting: no second number, no second email.
        var second = await Admin(new RefundsController(db, new RecordingAudit(), email, Config(), NullLogger<RefundsController>.Instance)).MarkRefunded(refund.Refund.Id, "REFUND-78");
        Assert.IsType<NotFoundResult>(second);
        Assert.Null(await CreditNotes.MarkRefundedAsync(db, refund.Refund.Id, "REFUND-79", DateTime.UtcNow));
        var after = await db.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.Id == refund.Refund.Id);
        Assert.Equal((row.CreditNoteNumber, "REFUND-77"), (after.CreditNoteNumber, after.PayPalRefundId));
        Assert.Equal(1L, (await db.NumberSequences.AsNoTracking().SingleAsync()).LastValue);
        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task Two_admins_marking_the_same_refund_at_once_issue_one_credit_note()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        int id;
        await using (var setup = testDb.CreateContext())
            id = (await PendingRefundAsync(setup, "SALE-RACE")).Refund.Id;

        await using var one = testDb.CreateContext();
        await using var two = testDb.CreateContext();
        var results = await Task.WhenAll(
            CreditNotes.MarkRefundedAsync(one, id, "REFUND-ONE", Oct24),
            CreditNotes.MarkRefundedAsync(two, id, "REFUND-TWO", Oct24));

        var winner = Assert.Single(results, r => r != null)!;
        await using var check = testDb.CreateContext();
        var row = await check.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.Id == id);
        Assert.Equal(("CN-2026-0001", winner.PayPalRefundId), (row.CreditNoteNumber, row.PayPalRefundId));
        Assert.Equal(1L, (await check.NumberSequences.AsNoTracking().SingleAsync()).LastValue);
    }

    // ---- the email -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_adviser_is_emailed_the_credit_note_with_its_amounts_tax_invoice_refund_and_supplier()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await BillingCompanyDetails.SaveAsync(db, new BillingCompanyProfile
        {
            Name = "iPro Advisers Inc.", AddressLine1 = "100 King St W", City = "Toronto", Province = "ON", PostalCode = "M5X 1A9",
            Country = "Canada", TaxRegistrationNumber = "123456789 RT0001", Email = "billing@iproadvisers.com", Website = "www.iproadvisers.com",
        }, Oct24);
        var refund = await PendingRefundAsync(db, "SALE-600");
        var email = new RecordingEmail();
        var audit = new RecordingAudit();
        var controller = Admin(new RefundsController(db, audit, email, Config(), NullLogger<RefundsController>.Instance));

        await controller.MarkRefunded(refund.Refund.Id, "REFUND-77");

        var row = await db.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.Id == refund.Refund.Id);
        Assert.NotNull(row.CreditNoteEmailSentAt);
        var mail = Assert.Single(email.Sent);
        Assert.Equal(refund.Adviser.Email, mail.To);
        Assert.Equal($"IPRO credit note {row.CreditNoteNumber} for invoice {refund.Invoice.InvoiceNumber}", mail.Subject);
        foreach (var expected in new[] { row.CreditNoteNumber, refund.Invoice.InvoiceNumber, "-$300.00 CAD", "ON HST 13%", "-$39.00 CAD", "-$339.00 CAD",
                     "$339.00 CAD was refunded", "REFUND-77", "SALE-600", "iPro Advisers Inc.", "100 King St W", "123456789 RT0001", "www.iProAdvisers.com" })
        {
            Assert.Contains(expected, mail.Html);
            Assert.Contains(expected, mail.Text);
        }
        Assert.Contains("The adviser was emailed", (string)controller.TempData["Success"]!);
        Assert.Contains(audit.Entries, e => e.Action == "RefundMarkedRefunded" && e.Details.Contains(row.CreditNoteNumber) && e.Details.Contains("REFUND-77"));
    }

    [Theory]
    [InlineData(false)]   // the email service says no
    [InlineData(true)]    // the email service throws
    public async Task A_failed_email_keeps_the_refund_and_its_credit_note_and_tells_the_admin(bool throws)
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var refund = await PendingRefundAsync(db, "SALE-600");
        var email = new RecordingEmail { Fail = true, Throw = throws };
        var controller = Admin(new RefundsController(db, new RecordingAudit(), email, Config(), NullLogger<RefundsController>.Instance));

        var result = await controller.MarkRefunded(refund.Refund.Id, "REFUND-77");

        Assert.IsType<RedirectToActionResult>(result);
        var row = await db.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.Id == refund.Refund.Id);
        Assert.Equal(RefundStatus.Refunded, row.RefundStatus);
        Assert.StartsWith("CN-", row.CreditNoteNumber);
        Assert.Null(row.CreditNoteEmailSentAt);
        var message = (string)controller.TempData["Success"]!;
        Assert.Contains(row.CreditNoteNumber, message);
        Assert.Contains("did NOT go out", message);
    }

    // ---- which invoice it credits --------------------------------------------------------------------------

    [Fact]
    public async Task A_real_cancellation_refund_credits_the_invoice_that_paid_the_cycle()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await AnnualSubscriberAsync(db, daysIn: 135);
        Assert.True(await NewService(db).CancelSubscriptionAsync(seed.Adviser.Id));
        db.ChangeTracker.Clear();
        var change = await db.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.ChangeType == SubscriptionChangeType.Cancel && c.AgentUserId == seed.Adviser.Id);
        Assert.Equal((RefundStatus.Pending, 339m), (change.RefundStatus, change.RefundGrossAmount));   // DOCS/22's worked example

        await Admin(new RefundsController(db, new RecordingAudit(), new RecordingEmail(), Config(), NullLogger<RefundsController>.Instance)).MarkRefunded(change.Id, "REFUND-REAL");

        var row = await db.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.Id == change.Id);
        Assert.Equal((seed.Invoice.InvoiceNumber, 0.13m, "ON HST"), (row.CreditNoteInvoiceNumber, row.CreditNoteTaxRate, row.CreditNoteTaxRegion));
        var note = await CreditNotes.FindAsync(db, CreditNotes.RefundKind, change.Id);
        Assert.Equal((300m, 39m, 339m), (note!.Net, note.Tax, note.Gross));
    }

    [Fact]
    public async Task Money_captured_after_the_end_gets_a_credit_note_for_the_invoice_that_recorded_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var refund = await PendingRefundAsync(db, "SALE-LATE", capturedAfterEnd: true);

        var row = await CreditNotes.MarkRefundedAsync(db, refund.Refund.Id, "REFUND-LATE", Oct24);

        Assert.Equal(refund.Invoice.InvoiceNumber, row!.CreditNoteInvoiceNumber);
        var note = await CreditNotes.FindAsync(db, CreditNotes.RefundKind, refund.Refund.Id);
        Assert.Contains("after the subscription had ended", note!.Description);
        Assert.Equal(("Ada Lovelace", "Lovelace Planning"), (note.BillToName, note.BillToCompany));   // the invoice's frozen bill-to
    }

    // ---- Revenue and the CSV -------------------------------------------------------------------------------

    [Fact]
    public async Task Revenue_shows_invoiced_credit_notes_as_a_negative_and_net_in_the_totals_months_tax_and_ledger()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (refund, refundNumber, rewardNumber) = await RefundAndRewardIssuedAsync(db);

        var view = Assert.IsType<ViewResult>(await Reports(db).Revenue());

        Assert.Equal(678m, (decimal)view.ViewData["PaidTotal"]!);            // invoiced, as before
        Assert.Equal(-395.50m, (decimal)view.ViewData["CreditNotesTotal"]!); // $339 refund + $56.50 reward
        Assert.Equal(282.50m, (decimal)view.ViewData["NetTotal"]!);
        Assert.Equal(2, (int)view.ViewData["CreditNoteCount"]!);

        var months = Assert.IsType<List<RevenueMonth>>(view.ViewData["ChartMonths"]);
        Assert.Equal((678m, -395.50m, 282.50m), (months.Sum(m => m.Invoiced), months.Sum(m => m.CreditNotes), months.Sum(m => m.Net)));
        var thisMonth = months.Single(m => m.CreditNotes != 0m);
        Assert.Equal((0m, -395.50m, -395.50m), (thisMonth.Invoiced, thisMonth.CreditNotes, thisMonth.Net));

        var tax = Assert.Single(Assert.IsType<List<RevenueTaxRegion>>(view.ViewData["TaxByRegion"]));
        Assert.Equal(("ON HST", 250m, 78m, -45.50m, 32.50m), (tax.Region, tax.Taxable, tax.Collected, tax.Credited, tax.NetTax));

        var rows = Assert.IsType<List<RevenueLedgerRow>>(view.ViewData["LedgerRows"]);
        var refundRow = rows.Single(r => r.Number == refundNumber);
        Assert.True(refundRow.IsCreditNote);
        Assert.Equal((-300m, -39m, -339m, refund.Invoice.InvoiceNumber), (refundRow.SubTotal, refundRow.Tax, refundRow.Total, refundRow.CreditsInvoice));
        Assert.Equal($"/Reports/CreditNote/{refund.Refund.Id}?kind=refund", refundRow.ViewUrl);
        var rewardRow = rows.Single(r => r.Number == rewardNumber);
        Assert.Equal((-50m, -6.50m, -56.50m, refund.Invoice.InvoiceNumber), (rewardRow.SubTotal, rewardRow.Tax, rewardRow.Total, rewardRow.CreditsInvoice));
        Assert.False(rows.Single(r => r.Number == refund.Invoice.InvoiceNumber).IsCreditNote);
        // The model stays the invoice list (RevenueExcludesTestPackagesTests reads it).
        Assert.Single(Assert.IsAssignableFrom<IEnumerable<Invoice>>(view.Model));

        var html = await RenderAdminAsync("~/Views/Reports/Revenue.cshtml", view.ViewData);
        Assert.Contains(refundNumber, html);
        Assert.Contains("Credit note", html);
        Assert.Contains("-$339.00", html);
        Assert.Contains("-$395.50", html);
        Assert.Contains("$282.50", html);
    }

    [Fact]
    public async Task The_csv_keeps_its_columns_and_adds_credit_notes_as_negative_rows_so_a_tax_filing_nets()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (refund, refundNumber, rewardNumber) = await RefundAndRewardIssuedAsync(db);

        var file = Assert.IsType<FileContentResult>(await Reports(db).InvoicesCsv());
        var lines = Encoding.UTF8.GetString(file.FileContents).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();

        Assert.Equal("InvoiceNumber,IssuedAtUtc,BillToName,BillToCompany,BillToEmail,AgentUserId,AgentAccount,Package,SubTotal,TaxRegion,TaxRatePercent,TaxAmount,Total,Currency,Paid,PayPalTransactionId,Type,CreditsInvoice", lines[0]);
        var rows = lines.Skip(1).Select(Cells).ToList();
        Assert.All(rows, r => Assert.Equal(18, r.Count));
        var invoice = rows.Single(r => r[0] == refund.Invoice.InvoiceNumber);
        Assert.Equal(("600.00", "78.00", "678.00", "Invoice", ""), (invoice[8], invoice[11], invoice[12], invoice[16], invoice[17]));
        var credit = rows.Single(r => r[0] == refundNumber);
        Assert.Equal(("-300.00", "ON HST", "13", "-39.00", "-339.00", "CAD"), (credit[8], credit[9], credit[10], credit[11], credit[12], credit[13]));
        Assert.Equal(("REFUND-77", "Credit note", refund.Invoice.InvoiceNumber), (credit[15], credit[16], credit[17]));
        var reward = rows.Single(r => r[0] == rewardNumber);
        Assert.Equal(("-50.00", "-6.50", "-56.50", "Credit note", refund.Invoice.InvoiceNumber), (reward[8], reward[11], reward[12], reward[16], reward[17]));

        // Summing the Total column nets: what was invoiced less what was credited.
        Assert.Equal(282.50m, rows.Sum(r => decimal.Parse(r[12], System.Globalization.CultureInfo.InvariantCulture)));
    }

    // A CSV line's cells, quotes undone (an invoice's PayPal reference holds a comma).
    private static List<string> Cells(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(c);
        }
        cells.Add(cell.ToString());
        return cells;
    }

    // ---- the printable credit note --------------------------------------------------------------------------

    [Fact]
    public async Task SuperAdmin_prints_either_kind_of_credit_note_on_the_invoice_layout()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (refund, refundNumber, rewardNumber) = await RefundAndRewardIssuedAsync(db);
        var rewardId = await db.Referrals.AsNoTracking().Select(r => r.Id).SingleAsync();

        var refundView = Assert.IsType<ViewResult>(await Reports(db).CreditNote(refund.Refund.Id, CreditNotes.RefundKind));
        var refundNote = Assert.IsType<CreditNoteEntry>(refundView.Model);
        Assert.Equal((refundNumber, 300m, 39m, 339m, refund.Invoice.InvoiceNumber, "REFUND-77"),
            (refundNote.Number, refundNote.Net, refundNote.Tax, refundNote.Gross, refundNote.CreditsInvoice, refundNote.PayPalRefundId));
        var html = await RenderAdminAsync("~/Views/Reports/CreditNote.cshtml", refundView.ViewData);
        foreach (var expected in new[] { ">Credit note<", refundNumber, refund.Invoice.InvoiceNumber, "-$300.00", "ON HST 13%", "-$39.00", "-$339.00", "REFUND-77", "Print / Save PDF" })
            Assert.Contains(expected, html);

        var rewardView = Assert.IsType<ViewResult>(await Reports(db).CreditNote(rewardId, CreditNotes.ReferralKind));
        var rewardNote = Assert.IsType<CreditNoteEntry>(rewardView.Model);
        Assert.Equal((rewardNumber, 56.50m, "Refer a Friend reward: Bob Moore joined iPro with your gift"), (rewardNote.Number, rewardNote.Gross, rewardNote.Description));
        Assert.Contains("-$56.50", await RenderAdminAsync("~/Views/Reports/CreditNote.cshtml", rewardView.ViewData));

        Assert.IsType<NotFoundResult>(await Reports(db).CreditNote(refund.Refund.Id + 1000, CreditNotes.RefundKind));
    }

    [Fact]
    public async Task The_refund_queue_shows_each_credit_note_and_says_marking_issues_one()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (refund, refundNumber, _) = await RefundAndRewardIssuedAsync(db);
        await PendingRefundAsync(db, "SALE-WAITING");

        var view = Assert.IsType<ViewResult>(await Admin(new RefundsController(db, new RecordingAudit(), new RecordingEmail(), Config(), NullLogger<RefundsController>.Instance)).Index());
        var html = await RenderAdminAsync("~/Views/Refunds/Index.cshtml", view.ViewData);

        Assert.Contains(refundNumber, html);
        Assert.Contains($"/Reports/CreditNote/{refund.Refund.Id}?kind=refund", html);
        Assert.Contains("issues a credit note and emails the adviser", html);
    }

    // ---- the schema and the wiring ---------------------------------------------------------------------------

    [Fact]
    public async Task The_start_up_step_adds_the_columns_and_both_apps_run_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        foreach (var column in new[] { "CreditNoteNumber", "CreditNoteIssuedAt", "CreditNoteInvoiceNumber", "CreditNoteTaxRate", "CreditNoteTaxRegion", "PayPalRefundId", "CreditNoteEmailSentAt" })
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE `SubscriptionChanges` DROP COLUMN `" + column + "`");   // seven fixed names
        await StartupSchemaRepair.EnsureRefundCreditNoteSchemaAsync(db);
        await StartupSchemaRepair.EnsureRefundCreditNoteSchemaAsync(db);   // idempotent

        var refund = await PendingRefundAsync(db, "SALE-SCHEMA");
        await db.SubscriptionChanges.Where(c => c.Id == refund.Refund.Id).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.CreditNoteNumber, "CN-2026-0042").SetProperty(c => c.CreditNoteIssuedAt, Oct24)
            .SetProperty(c => c.CreditNoteInvoiceNumber, "IPRO-2026-000123").SetProperty(c => c.CreditNoteTaxRate, 0.14975m)
            .SetProperty(c => c.CreditNoteTaxRegion, "QC GST+QST").SetProperty(c => c.PayPalRefundId, "R-1").SetProperty(c => c.CreditNoteEmailSentAt, Oct24));
        var row = await db.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.Id == refund.Refund.Id);
        Assert.Equal(("CN-2026-0042", 0.14975m, "QC GST+QST", "R-1"), (row.CreditNoteNumber, row.CreditNoteTaxRate, row.CreditNoteTaxRegion, row.PayPalRefundId));

        foreach (var program in new[] { @"src\IPRO.Web\Program.cs", @"src\IPRO.Admin\Program.cs" })
            Assert.Contains("await StartupGuard.RunStepAsync(\"StartupSchemaRepair.EnsureRefundCreditNoteSchemaAsync\", () => StartupSchemaRepair.EnsureRefundCreditNoteSchemaAsync(db), db, app.Logger);", Read(program));
    }

    // ---- plumbing ---------------------------------------------------------------------------------------------

    private static int _seedNumber;

    private sealed record Seeded(AgentUser Adviser, EBilling Billing, Invoice Invoice, SubscriptionChange Refund);

    // An adviser who paid $678 ($600 + 13% ON HST) on an annual plan and cancelled, with $339 ($300 + $39) owed back
    // against the sale -- DOCS/22's worked example -- waiting in the queue.
    private static async Task<Seeded> PendingRefundAsync(IPRODbContext db, string sale, bool capturedAfterEnd = false)
    {
        var n = System.Threading.Interlocked.Increment(ref _seedNumber);
        var rule = new BillingRule { PackageName = $"IPro Gold {n}", MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var adviser = new AgentUser
        {
            UserName = $"cn-{Guid.NewGuid():N}"[..20], DomainName = $"cn-{Guid.NewGuid():N}"[..24], Email = $"ada{n}@example.test",
            FirstName = "Ada", LastName = "Lovelace", CompanyName = "Lovelace Planning", Country = "Canada", Province = "Ontario", PackageId = rule.Id,
        };
        db.Add(adviser);
        await db.SaveChangesAsync();
        var start = DateTime.UtcNow.AddDays(-135);
        var billing = new EBilling { AgentUserId = adviser.Id, BillingRuleId = rule.Id, Amount = 600m, Currency = "CAD", Status = BillingStatus.Cancelled, Period = BillingPeriod.Annually, StartDate = start };
        db.Add(billing);
        await db.SaveChangesAsync();
        var invoice = new Invoice
        {
            BillingId = billing.Id, AgentUserId = adviser.Id, InvoiceNumber = $"IPRO-2026-{n:000000}", SubTotal = 600m, TaxRate = 0.13m, TaxAmount = 78m,
            TaxRegion = "ON HST", Total = 678m, Currency = "CAD", PayPalTransactionId = $"I-SUB{n},{sale}", IssuedAt = start, IsPaid = true,
            BillToName = "Ada Lovelace", BillToCompany = "Lovelace Planning", BillToEmail = $"ada{n}@example.test", BillToAddress = "1 Main St\nToronto, ON M5V 1A1\nCanada",
        };
        db.Add(invoice);
        var refund = new SubscriptionChange
        {
            AgentUserId = adviser.Id, CurrentBillingRuleId = rule.Id, RequestedBillingRuleId = rule.Id, BillingId = billing.Id,
            ChangeType = SubscriptionChangeType.Cancel, Status = SubscriptionChangeStatus.Applied, Period = BillingPeriod.Annually,
            EffectiveDate = DateTime.UtcNow, AppliedAt = capturedAfterEnd ? null : DateTime.UtcNow, Currency = "CAD",
            RefundNetAmount = 300m, RefundTaxAmount = 39m, RefundGrossAmount = 339m, RefundStatus = RefundStatus.Pending,
            RefundPayPalTransactionId = sale, RefundWindowEndsAt = DateTime.UtcNow.AddDays(45),
            RefundResolutionNote = capturedAfterEnd
                ? $"PayPal captured 678.00 CAD (txn {sale}) against Cancelled subscription I-SUB{n} -- the agent received nothing for it. Refund at PayPal, then verify the subscription is stopped there. [captured-after-end net]"
                : "Annual clawback: 5 month(s) used; refund 300.00 + tax 39.00 (rate 0.13 as invoiced). [self-cancel]",
        };
        db.Add(refund);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seeded(adviser, billing, invoice, refund);
    }

    // A Refer a Friend reward earned by the adviser, planned against their own sale (or already paid, with its number).
    private static async Task<Referral> EarnedReferralAsync(IPRODbContext db, AgentUser referrer, string sale, string creditNote = "", DateTime? paidAt = null)
    {
        var referral = new Referral
        {
            AgentUserId = referrer.Id, FriendAgentUserId = 900000 + System.Threading.Interlocked.Increment(ref _seedNumber), Code = "ADA-7K3Q",
            FriendName = "Bob Moore", RewardAmount = 50m, Stage = creditNote.Length > 0 ? ReferralStages.Paid : ReferralStages.Earned,
            SignedUpAt = Oct24.AddDays(-70), EarnedAt = Oct24.AddDays(-25), RewardNet = 50m, RewardTax = 6.50m, RewardGross = 56.50m,
            RewardTaxRate = 0.13m, RewardTaxRegion = "ON HST", RefundPlan = $"{sale}=56.50", RefundWindowEndsAt = Oct24.AddDays(40),
            CreditNoteNumber = creditNote, PaidAt = paidAt, RefundTransactionId = creditNote.Length > 0 ? "REFUND-OLD" : "",
        };
        db.Referrals.Add(referral);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return referral;
    }

    // One $678 invoice, its $339 refund marked Refunded, and a $56.50 Refer a Friend reward paid -- both credit notes
    // issued now, so the Revenue screen's default twelve months shows all three.
    private static async Task<(Seeded Refund, string RefundNumber, string RewardNumber)> RefundAndRewardIssuedAsync(IPRODbContext db)
    {
        var refund = await PendingRefundAsync(db, "SALE-600");
        var marked = await CreditNotes.MarkRefundedAsync(db, refund.Refund.Id, "REFUND-77", DateTime.UtcNow);
        var reward = await EarnedReferralAsync(db, refund.Adviser, "SALE-600");
        var paid = await ReferralProgram.MarkPaidAsync(db, reward.Id, "REFUND-88", DateTime.UtcNow);
        db.ChangeTracker.Clear();
        return (refund, marked!.CreditNoteNumber, paid!.CreditNoteNumber);
    }

    private sealed record Subscriber(AgentUser Adviser, Invoice Invoice);

    // PrepaidValueTests' annual Gold subscriber: $600 + 13% paid at the start, the cycle `daysIn` days old.
    private static async Task<Subscriber> AnnualSubscriberAsync(IPRODbContext db, int daysIn)
    {
        if (!await db.ProvinceTaxRates.AnyAsync(t => t.ProvinceCode == "ON"))
            db.Add(new ProvinceTaxRate { ProvinceCode = "ON", ProvinceName = "Ontario", Rate = 0.13m, TaxLabel = "ON 13% HST", IsActive = true });
        var rule = new BillingRule { PackageName = $"CN-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var adviser = new AgentUser
        {
            UserName = $"cn-{Guid.NewGuid():N}"[..20], Email = "pv@example.test", FirstName = "Prepaid", LastName = "Value",
            DomainName = $"cn-{Guid.NewGuid():N}"[..24], Country = "Canada", Province = "Ontario", PackageId = rule.Id,
        };
        db.Add(adviser);
        await db.SaveChangesAsync();
        var start = DateTime.UtcNow.AddDays(-daysIn);
        var billing = new EBilling
        {
            AgentUserId = adviser.Id, BillingRuleId = rule.Id, PayPalSubscriptionId = string.Empty, PayPalPlanId = string.Empty, Amount = 600m,
            Currency = "CAD", Status = BillingStatus.Active, Period = BillingPeriod.Annually, StartDate = start, NextBillingDate = start.AddYears(1),
        };
        db.Add(billing);
        await db.SaveChangesAsync();
        var invoice = new Invoice
        {
            BillingId = billing.Id, AgentUserId = adviser.Id, InvoiceNumber = $"PV-{billing.Id}", SubTotal = 600m, TaxRate = 0.13m, TaxAmount = 78m,
            TaxRegion = "ON HST", Total = 678m, PayPalTransactionId = "TXN-ANNUAL-1", IssuedAt = start, IsPaid = true,
        };
        db.Add(invoice);
        await db.SaveChangesAsync();
        return new Subscriber(adviser, invoice);
    }

    private static PayPalBillingService NewService(IPRODbContext db) => new(
        new UnitOfWork(db), db, new EmptyHttp(), new RecordingEmail(), Options.Create(new PayPalSettings()), Config(), NullLogger<PayPalBillingService>.Instance);

    private static ReportsController Reports(IPRODbContext db)
    {
        var controller = new ReportsController(new UnitOfWork(db));
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<IConfiguration>(Config()).BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Name, "owner") }, "test")),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, new NullTempData());
        return controller;
    }

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static T Admin<T>(T controller) where T : Controller
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Name, "owner") }, "test")),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, new NullTempData());
        return controller;
    }

    // The admin app's own compiled view, rendered with what the action handed it (no layout).
    private static async Task<string> RenderAdminAsync(string viewPath, ViewDataDictionary viewData)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions
        {
            ApplicationName = typeof(ReportsController).Assembly.GetName().Name,
            ContentRootPath = Path.Combine(RepoRoot(), "src", "IPRO.Admin"),
            EnvironmentName = "Production",
        });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(ReportsController).Assembly);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var http = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "owner"), new Claim("Role", "SuperAdmin") }, "test")),
        };
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "render"));
        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var engine = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Mvc.Razor.IRazorViewEngine>();
        var found = engine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        Assert.True(found.Success, $"{viewPath} was not found among the admin app's compiled views");
        await using var writer = new StringWriter();
        await found.View!.RenderAsync(new Microsoft.AspNetCore.Mvc.Rendering.ViewContext(
            actionContext, found.View, viewData, new TempDataDictionary(http, new NullTempData()), writer, new HtmlHelperOptions()));
        return writer.ToString();
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative)).Replace("\r\n", "\n");

    private sealed class EmptyHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class RecordingEmail : IEmailService
    {
        public bool Fail { get; init; }
        public bool Throw { get; init; }
        public List<(string To, string Subject, string Html, string Text)> Sent { get; } = new();

        public Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        {
            if (Throw) throw new HttpRequestException("the email provider is down");
            if (Fail) return Task.FromResult(false);
            Sent.Add((toEmail, subject, htmlBody, textBody ?? string.Empty));
            return Task.FromResult(true);
        }

        public async Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            await SendAsync(toEmail, toName, subject, htmlBody, textBody) ? EmailSendResult.Sent() : EmailSendResult.Failed("not sent");

        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
    }

    private sealed class RecordingAudit : IPRO.Business.Interfaces.IAdminAuditLogService
    {
        public List<(string Action, string Details)> Entries { get; } = new();
        public Task LogAsync(int adminUserId, string adminUsername, string action, string details)
        {
            Entries.Add((action, details));
            return Task.CompletedTask;
        }
    }

    private sealed class NullTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
