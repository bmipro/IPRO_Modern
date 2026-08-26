using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// 2026-08-26: the QA daily harness (hidden IsHiddenTestPackage packages, billed in the PayPal
// SANDBOX) put ~$500 of invoices into the Revenue Report — money that was never real. The rows
// themselves are correctly RETAINED by the eraser (CRA six-year retention, and the report is what
// reads them), so the fix belongs in the report, not in a delete: sandbox charges against hidden
// test packages are not revenue and must never be counted, this run or any future one.
public class RevenueExcludesTestPackagesTests
{
    [Fact]
    public async Task Revenue_counts_real_package_invoices_and_ignores_hidden_test_package_ones()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var real = await SeedInvoiceAsync(db, hidden: false, total: 101.70m, number: "REAL-0001");
        var qa = await SeedInvoiceAsync(db, hidden: true, total: 45.20m, number: "QA-0001");

        var controller = new IPRO.Admin.Controllers.ReportsController(new UnitOfWork(db));
        var view = Assert.IsType<ViewResult>(await controller.Revenue());
        var listed = Assert.IsAssignableFrom<IEnumerable<Invoice>>(view.Model).ToList();

        // Pre-fix both appeared and the totals summed both — the QA harness's sandbox charges
        // showed up as revenue the business never earned.
        Assert.Contains(listed, i => i.InvoiceNumber == real.Number);
        Assert.DoesNotContain(listed, i => i.InvoiceNumber == qa.Number);
        Assert.Equal(101.70m, (decimal)view.ViewData["PaidTotal"]!);
        Assert.Equal(1, (int)view.ViewData["PaidCount"]!);
    }

    [Fact]
    public async Task The_csv_export_agrees_with_the_screen()
    {
        // Both read LoadLedgerAsync; an export that disagreed with the screen would be its own
        // defect (the chart-vs-rows lesson from ADMIN-8).
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var real = await SeedInvoiceAsync(db, hidden: false, total: 101.70m, number: "REAL-0002");
        var qa = await SeedInvoiceAsync(db, hidden: true, total: 45.20m, number: "QA-0002");

        var controller = new IPRO.Admin.Controllers.ReportsController(new UnitOfWork(db));
        var file = Assert.IsType<FileContentResult>(await controller.InvoicesCsv());
        var csv = System.Text.Encoding.UTF8.GetString(file.FileContents);

        Assert.Contains(real.Number, csv);
        Assert.DoesNotContain(qa.Number, csv);
    }

    [Fact]
    public async Task An_invoice_whose_billing_row_is_gone_is_still_counted()
    {
        // Fail OPEN for real money: an invoice we cannot trace to a package (billing row purged by
        // an older eraseFinancialRecords delete, or data gap) must still count as revenue. Only a
        // POSITIVE match on IsHiddenTestPackage may exclude — the opposite default would silently
        // erase real income from the books, which is the exact class of bug this report already
        // survived once (bob3test3's $335).
        // WITH the ledger guard, exactly like production: it drops the CASCADE from Billings into
        // Invoices, so removing the billing row leaves the invoice standing. Without the guard the
        // delete below CASCADES and destroys the invoice outright -- which is precisely the
        // 2026-08-14 invoice-loss the guard exists to prevent, and which this test hit on its
        // first run as a live demonstration.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: true);
        await using var db = testDb.CreateContext();
        var orphan = await SeedInvoiceAsync(db, hidden: false, total: 67.80m, number: "ORPHAN-1");
        await db.Billings.Where(b => b.Id == orphan.BillingId).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
        Assert.True(await db.Invoices.AsNoTracking().AnyAsync(i => i.InvoiceNumber == "ORPHAN-1"),
            "precondition: the ledger guard must keep the invoice when its billing row goes");

        var controller = new IPRO.Admin.Controllers.ReportsController(new UnitOfWork(db));
        var view = Assert.IsType<ViewResult>(await controller.Revenue());
        var listed = Assert.IsAssignableFrom<IEnumerable<Invoice>>(view.Model).ToList();

        Assert.Contains(listed, i => i.InvoiceNumber == "ORPHAN-1");
        Assert.Equal(67.80m, (decimal)view.ViewData["PaidTotal"]!);
    }

    // ------------------------------------------------------------------------------ plumbing --

    private sealed record Seeded(string Number, int BillingId);

    private static async Task<Seeded> SeedInvoiceAsync(IPRODbContext db, bool hidden, decimal total, string number)
    {
        var rule = new BillingRule
        {
            PackageName = $"{(hidden ? "QA" : "IPro")}-{Guid.NewGuid():N}"[..20],
            MonthlyPrice = 40m,
            AnnualPrice = 400m,
            IsHiddenTestPackage = hidden
        };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"rev-{Guid.NewGuid():N}"[..20],
            Email = $"rev-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Rev", LastName = "Report",
            DomainName = $"rev-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario",
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var billing = new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id,
            BillingRuleId = rule.Id,
            Amount = 40m,
            Status = BillingStatus.Active,
            Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddDays(-10)
        };
        db.Add(billing);
        await db.SaveChangesAsync();
        db.Add(new Invoice
        {
            BillingId = billing.Id,
            AgentUserId = agent.Id,
            InvoiceNumber = number,
            SubTotal = Math.Round(total / 1.13m, 2),
            TaxRate = 0.13m,
            TaxAmount = total - Math.Round(total / 1.13m, 2),
            TaxRegion = "ON 13% HST",
            Total = total,
            PayPalTransactionId = $"TXN-{number}",
            IssuedAt = DateTime.UtcNow.AddDays(-1),
            IsPaid = true
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seeded(number, billing.Id);
    }
}
