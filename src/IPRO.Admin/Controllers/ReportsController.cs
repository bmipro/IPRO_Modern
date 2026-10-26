using System.Globalization;
using IPRO.Admin.Models;
using IPRO.Billing;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Admin.Controllers;

[Authorize(Policy = "AdminAccess")]
public class ReportsController : Controller
{
    private readonly IUnitOfWork _uow;
    public ReportsController(IUnitOfWork uow) => _uow = uow;

    // ONE revenue screen (owner request 2026-08-13): the by-month chart and totals on top, the full
    // permanent ledger beneath -- every invoice ever issued, including those whose agent has since
    // been deleted (invoices are retained through deletion and carry their own bill-to snapshot).
    // Having "Revenue" and "Invoices" as separate nav items meant the button NAMED Revenue was the
    // shallow one, and the detail hid behind a different word.
    // 600 (2026-10-24): and every credit note -- a refund marked Refunded, a Refer a Friend reward paid -- as its own
    // negative row, netted in the totals, the months and the tax by region (the owner: "matters for tax filing").
    // The model stays the invoice list; the screen reads LedgerRows.
    public async Task<IActionResult> Revenue(DateTime? from = null, DateTime? to = null, string? q = null,
        string sort = "date", string dir = "desc")
    {
        var (invoices, creditNotes, fromDate, toDate) = await LoadLedgerAsync(from, to, q);

        var agents = (await _uow.AgentUsers.GetAllAsync()).ToDictionary(a => a.Id);
        var billings = (await _uow.Billings.GetAllAsync()).ToDictionary(b => b.Id);
        var packages = (await _uow.BillingRules.GetAllAsync()).ToDictionary(p => p.Id);

        string PackageNameOf(Invoice i) =>
            billings.TryGetValue(i.BillingId, out var b) && packages.TryGetValue(b.BillingRuleId, out var p)
                ? p.PackageName
                : string.Empty;

        // Sortable Date / Package columns (owner request). Anything else falls back to date.
        sort = sort?.ToLowerInvariant() == "package" ? "package" : "date";
        dir = dir?.ToLowerInvariant() == "asc" ? "asc" : "desc";
        invoices = (sort, dir) switch
        {
            ("package", "asc") => invoices.OrderBy(PackageNameOf).ThenByDescending(i => i.IssuedAt).ToList(),
            ("package", "desc") => invoices.OrderByDescending(PackageNameOf).ThenByDescending(i => i.IssuedAt).ToList(),
            ("date", "asc") => invoices.OrderBy(i => i.IssuedAt).ToList(),
            _ => invoices.OrderByDescending(i => i.IssuedAt).ToList()
        };
        var rows = LedgerRows(invoices, creditNotes, agents, PackageNameOf);
        rows = (sort, dir) switch
        {
            ("package", "asc") => rows.OrderBy(r => r.Package).ThenByDescending(r => r.IssuedAt).ToList(),
            ("package", "desc") => rows.OrderByDescending(r => r.Package).ThenByDescending(r => r.IssuedAt).ToList(),
            ("date", "asc") => rows.OrderBy(r => r.IssuedAt).ToList(),
            _ => rows.OrderByDescending(r => r.IssuedAt).ToList()
        };

        var paid = invoices.Where(i => i.IsPaid).ToList();

        // Chart series: paid revenue by month WITHIN the filtered range, so the chart and the ledger
        // always describe the same set of invoices and can never disagree again.
        // ADMIN-8 (fixed 2026-08-20): the chart used to bucket by UTC month while the rows
        // beneath print agent-LOCAL dates (AgentLocalTime.FromUtc) -- an evening month-boundary
        // invoice appeared in a different month than its own ledger row. Both now read the same
        // clock: the issuing agent's, defaulting like the rows do when the agent is deleted.
        // 600: each month is invoiced, credit notes (negative) and net; a credit note reads its adviser's clock too.
        string MonthOf(DateTime utc, string? timeZone)
        {
            var local = IPRO.DataAccess.AgentLocalTime.FromUtc(utc, timeZone);
            return $"{local.Year}-{local.Month:D2}";
        }
        var invoicedByMonth = paid
            .GroupBy(i => MonthOf(i.IssuedAt, agents.TryGetValue(i.AgentUserId, out var a) ? a.TimeZone : null))
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Total));
        var creditedByMonth = creditNotes
            .GroupBy(c => MonthOf(c.IssuedAt, c.AdviserTimeZone))
            .ToDictionary(g => g.Key, g => -g.Sum(c => c.Gross));
        ViewBag.ChartMonths = invoicedByMonth.Keys.Union(creditedByMonth.Keys)
            .OrderBy(label => label, StringComparer.Ordinal)
            .Select(label =>
            {
                var invoiced = invoicedByMonth.GetValueOrDefault(label);
                var credited = creditedByMonth.GetValueOrDefault(label);
                return new RevenueMonth(label, invoiced, credited, invoiced + credited);
            })
            .ToList();

        ViewBag.From = fromDate;
        ViewBag.To = toDate;
        ViewBag.Query = q ?? string.Empty;
        ViewBag.Sort = sort;
        ViewBag.Dir = dir;
        ViewBag.Agents = agents;
        ViewBag.Billings = billings;
        ViewBag.Packages = packages;
        ViewBag.PaidTotal = paid.Sum(i => i.Total);
        ViewBag.PaidCount = paid.Count;
        ViewBag.UnpaidCount = invoices.Count - paid.Count;
        // 600: credit notes as a negative, and what is left.
        var creditNotesTotal = -creditNotes.Sum(c => c.Gross);
        ViewBag.CreditNotesTotal = creditNotesTotal;
        ViewBag.CreditNoteCount = creditNotes.Count;
        ViewBag.NetTotal = paid.Sum(i => i.Total) + creditNotesTotal;
        ViewBag.LedgerRows = rows;
        // Grouped on the region label the invoice was issued under -- this is the remittance view.
        // 600: a credit note carries its invoice's region, so the tax it gave back comes off the same line.
        ViewBag.TaxByRegion = paid
            .Where(i => i.TaxAmount > 0)
            .Select(i => (Region: i.TaxRegion ?? string.Empty, Taxable: i.SubTotal, Collected: i.TaxAmount, Credited: 0m))
            .Concat(creditNotes
                .Where(c => c.Tax > 0)
                .Select(c => (Region: c.TaxRegion ?? string.Empty, Taxable: -c.Net, Collected: 0m, Credited: -c.Tax)))
            .GroupBy(l => l.Region)
            .Select(g => new RevenueTaxRegion(g.Key, g.Sum(l => l.Taxable), g.Sum(l => l.Collected), g.Sum(l => l.Credited), g.Sum(l => l.Collected + l.Credited)))
            .OrderByDescending(r => r.Collected)
            .ToList();
        return View(invoices);
    }

    // The old separate ledger URL. Kept so bookmarks and muscle memory keep working.
    public IActionResult Invoices(DateTime? from = null, DateTime? to = null, string? q = null)
        => RedirectToActionPermanent(nameof(Revenue), new { from, to, q });

    public async Task<IActionResult> Invoice(int id)
    {
        var invoice = await _uow.Invoices.GetByIdAsync(id);
        if (invoice == null) return NotFound();
        invoice.LineItems = (await _uow.InvoiceLineItems.FindAsync(l => l.InvoiceId == invoice.Id))
            .OrderBy(l => l.SortOrder).ToList();

        var agent = await _uow.AgentUsers.GetByIdAsync(invoice.AgentUserId);
        var billing = await _uow.Billings.GetByIdAsync(invoice.BillingId);
        ViewBag.Agent = agent; // null when the agent has been deleted -- the snapshot carries the bill-to
        ViewBag.Package = billing == null ? null : await _uow.BillingRules.GetByIdAsync(billing.BillingRuleId);
        // 514: the supplier block reads SuperAdmin -> Company Details, the settings as the fallback.
        var configuration = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        ViewBag.Company = await BillingCompanyDetails.LoadAsync(_uow.Context, key => configuration[key]);
        return View(invoice);
    }

    // 600 (2026-10-24): a credit note -- a refund (kind=refund, the SubscriptionChange) or a Refer a Friend reward
    // (kind=referral, the Referral) -- to print or save as PDF, in the invoice print view's layout.
    public async Task<IActionResult> CreditNote(int id, string kind = CreditNotes.RefundKind)
    {
        var note = await CreditNotes.FindAsync(_uow.Context, kind, id);
        if (note == null) return NotFound();
        var configuration = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        ViewBag.Company = await BillingCompanyDetails.LoadAsync(_uow.Context, key => configuration[key]);
        return View(note);
    }

    private static string CreditNoteUrl(CreditNoteEntry note) => $"/Reports/CreditNote/{note.SourceId}?kind={note.Kind}";

    // 600: the columns and their order are as they were; Type and CreditsInvoice are added at the end. A credit note's
    // SubTotal, TaxAmount and Total are negative, so summing a column nets it; its Paid is "yes" (the refund was made)
    // and its PayPalTransactionId is PayPal's refund id.
    public async Task<IActionResult> InvoicesCsv(DateTime? from = null, DateTime? to = null, string? q = null)
    {
        var (invoices, creditNotes, fromDate, toDate) = await LoadLedgerAsync(from, to, q);
        var agents = (await _uow.AgentUsers.GetAllAsync()).ToDictionary(a => a.Id);
        var billings = (await _uow.Billings.GetAllAsync()).ToDictionary(b => b.Id);
        var packages = (await _uow.BillingRules.GetAllAsync()).ToDictionary(p => p.Id);
        string PackageNameOf(Invoice i) =>
            billings.TryGetValue(i.BillingId, out var b) && packages.TryGetValue(b.BillingRuleId, out var p) ? p.PackageName : string.Empty;

        static string Csv(string? value)
        {
            value ??= string.Empty;
            return value.Contains(',') || value.Contains('"') || value.Contains('\n')
                ? $"\"{value.Replace("\"", "\"\"")}\""
                : value;
        }
        static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("InvoiceNumber,IssuedAtUtc,BillToName,BillToCompany,BillToEmail,AgentUserId,AgentAccount,Package,SubTotal,TaxRegion,TaxRatePercent,TaxAmount,Total,Currency,Paid,PayPalTransactionId,Type,CreditsInvoice");
        foreach (var r in LedgerRows(invoices, creditNotes, agents, PackageNameOf).OrderByDescending(r => r.IssuedAt))
        {
            var account = agents.TryGetValue(r.AgentUserId, out var a) ? a.UserName : "(deleted)";
            sb.AppendLine(string.Join(",",
                Csv(r.Number), r.IssuedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), Csv(r.BillToName), Csv(r.BillToCompany),
                Csv(r.BillToEmail), r.AgentUserId.ToString(CultureInfo.InvariantCulture), Csv(account), Csv(r.Package),
                Amount(r.SubTotal), Csv(r.TaxRegion), (r.TaxRate * 100).ToString("0.###", CultureInfo.InvariantCulture),
                Amount(r.Tax), Amount(r.Total), Csv(r.Currency),
                r.Paid ? "yes" : "no", Csv(r.PayPalReference), r.Type, Csv(r.CreditsInvoice)));
        }

        var name = $"ipro-invoices-{fromDate:yyyyMMdd}-{toDate:yyyyMMdd}.csv";
        return File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", name);
    }

    // 600: the invoices and the credit notes as one ledger -- the screen and the CSV read the same rows.
    private static List<RevenueLedgerRow> LedgerRows(IEnumerable<Invoice> invoices, IEnumerable<CreditNoteEntry> creditNotes,
        IReadOnlyDictionary<int, AgentUser> agents, Func<Invoice, string> packageOf) =>
        invoices.Select(i => new RevenueLedgerRow(
                false, i.InvoiceNumber, i.IssuedAt, i.AgentUserId, i.BillToName, i.BillToCompany, i.BillToEmail,
                packageOf(i), i.SubTotal, i.TaxRegion, i.TaxRate, i.TaxAmount, i.Total, i.Currency, i.IsPaid,
                i.PayPalTransactionId, string.Empty, $"/Reports/Invoice/{i.Id}",
                agents.TryGetValue(i.AgentUserId, out var a) ? a.TimeZone : string.Empty))
            .Concat(creditNotes.Select(c => new RevenueLedgerRow(
                true, c.Number, c.IssuedAt, c.AgentUserId, c.BillToName, c.BillToCompany, c.BillToEmail,
                c.Package, -c.Net, c.TaxRegion, c.TaxRate, -c.Tax, -c.Gross, c.Currency, true,
                c.PayPalRefundId, c.CreditsInvoice, CreditNoteUrl(c), c.AdviserTimeZone, c.KindLabel)))
            .ToList();

    private async Task<(List<Invoice> Invoices, List<CreditNoteEntry> CreditNotes, DateTime From, DateTime To)> LoadLedgerAsync(DateTime? from, DateTime? to, string? q)
    {
        var fromDate = (from ?? DateTime.UtcNow.AddMonths(-12)).Date;
        var toDate = (to ?? DateTime.UtcNow).Date;
        var endExclusive = toDate.AddDays(1);

        var invoices = (await _uow.Invoices.FindAsync(i => i.IssuedAt >= fromDate && i.IssuedAt < endExclusive))
            .OrderByDescending(i => i.IssuedAt)
            .ToList();
        // 600: the credit notes issued in the same days.
        var creditNotes = await CreditNotes.ListAsync(_uow.Context, fromDate, endExclusive);
        // 2026-08-26: QA harness charges are not revenue. The daily test packages
        // (IsHiddenTestPackage: 7/8/9) bill through PayPal's SANDBOX, so their invoices are real
        // rows recording money that never moved -- ~$500 of them from the BobyMot lifecycle run
        // alone. The rows themselves are correctly RETAINED when a QA agent is deleted (CRA
        // retention, and this report is what reads them), so the exclusion belongs here, at the
        // one loader both the ledger and the CSV export read.
        //
        // Fails OPEN, deliberately: only a POSITIVE match on IsHiddenTestPackage excludes an
        // invoice. One whose billing or package row is missing -- an older
        // eraseFinancialRecords delete, a data gap -- still COUNTS. The opposite default would
        // silently erase real income from the books, which is the same shape as bob3test3's $335
        // vanishing in 2026-08-12.
        var testPackageIds = (await _uow.BillingRules.FindAsync(p => p.IsHiddenTestPackage))
            .Select(p => p.Id)
            .ToHashSet();
        if (testPackageIds.Count > 0)
        {
            var testBillingIds = (await _uow.Billings.FindAsync(b => testPackageIds.Contains(b.BillingRuleId)))
                .Select(b => b.Id)
                .ToHashSet();
            if (testBillingIds.Count > 0)
            {
                invoices = invoices.Where(i => !testBillingIds.Contains(i.BillingId)).ToList();
                // 600: a refund on a sandbox subscription is no more real than its charge (same positive match).
                creditNotes = creditNotes.Where(c => !(c.BillingId.HasValue && testBillingIds.Contains(c.BillingId.Value))).ToList();
            }
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            invoices = invoices.Where(i =>
                    i.InvoiceNumber.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    i.BillToName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    i.BillToCompany.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    i.BillToEmail.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    i.PayPalTransactionId.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToList();
            // 600: a credit note is found by its own number, the invoice it credits, the bill-to, or either PayPal id.
            creditNotes = creditNotes.Where(c =>
                    c.Number.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    c.CreditsInvoice.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    c.BillToName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    c.BillToCompany.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    c.BillToEmail.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    c.PayPalRefundId.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    c.RefundedAgainst.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return (invoices, creditNotes, fromDate, toDate);
    }

    public async Task<IActionResult> Agents()
    {
        var agents  = (await _uow.AgentUsers.GetAllAsync()).ToList();
        var byMonth = agents.GroupBy(a => new { a.CreatedAt.Year, a.CreatedAt.Month })
                            .Select(g => new { Label = $"{g.Key.Year}-{g.Key.Month:D2}", Count = g.Count() })
                            .OrderBy(g => g.Label).ToList();
        ViewBag.ByMonth       = byMonth;
        ViewBag.TotalActive   = agents.Count(a => a.IsActive);
        ViewBag.TotalInactive = agents.Count(a => !a.IsActive);
        return View();
    }

    public async Task<IActionResult> Subscriptions()
    {
        var billings = (await _uow.Billings.GetAllAsync()).ToList();
        var agents = (await _uow.AgentUsers.GetAllAsync()).ToDictionary(a => a.Id);
        var packages = (await _uow.BillingRules.GetAllAsync()).ToDictionary(p => p.Id);
        var invoices = (await _uow.Invoices.GetAllAsync()).ToList();
        var changes = (await _uow.SubscriptionChanges.GetAllAsync()).ToList();
        ViewBag.Active    = billings.Count(b => b.Status == BillingStatus.Active);
        ViewBag.Cancelled = billings.Count(b => b.Status == BillingStatus.Cancelled);
        ViewBag.Pending   = billings.Count(b => b.Status == BillingStatus.Pending);
        ViewBag.Failed    = billings.Count(b => b.Status == BillingStatus.Failed);
        ViewBag.FailedPayments = invoices.Count(i => !i.IsPaid && i.PayPalTransactionId.StartsWith("PAYPAL_FAILED:"));
        ViewBag.OpenInvoices = invoices.Count(i => !i.IsPaid);
        ViewBag.Agents = agents;
        ViewBag.Packages = packages;
        ViewBag.InvoicesByBilling = invoices
            .GroupBy(i => i.BillingId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.IssuedAt).ToList());
        ViewBag.ChangesByBilling = changes
            .Where(c => c.BillingId.HasValue)
            .GroupBy(c => c.BillingId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.CreatedAt).ToList());
        ViewBag.Billings  = billings.OrderByDescending(b => b.CreatedAt).Take(50);
        return View();
    }
}
