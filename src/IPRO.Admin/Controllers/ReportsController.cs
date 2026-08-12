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

    public async Task<IActionResult> Revenue(int months = 6)
    {
        var from     = DateTime.UtcNow.AddMonths(-months);
        var invoices = (await _uow.Invoices.FindAsync(i => i.IssuedAt >= from && i.IsPaid))
            .GroupBy(i => new { i.IssuedAt.Year, i.IssuedAt.Month })
            .Select(g => new { Label = $"{g.Key.Year}-{g.Key.Month:D2}", Total = g.Sum(i => i.Total) })
            .OrderBy(g => g.Label).ToList();
        ViewBag.Months     = months;
        ViewBag.Invoices   = invoices;
        ViewBag.GrandTotal = invoices.Sum(i => i.Total);
        return View();
    }

    // The accounting ledger: every invoice ever issued, including those whose agent has since been
    // deleted (invoices are retained through deletion and carry their own bill-to snapshot). This is
    // the answer to "an ex-customer wants copies of their invoices" and to "how much tax did we
    // collect per province this period" -- neither of which should ever require logging into PayPal.
    public async Task<IActionResult> Invoices(DateTime? from = null, DateTime? to = null, string? q = null)
    {
        var (invoices, fromDate, toDate) = await LoadLedgerAsync(from, to, q);

        var agents = (await _uow.AgentUsers.GetAllAsync()).ToDictionary(a => a.Id);
        var billings = (await _uow.Billings.GetAllAsync()).ToDictionary(b => b.Id);
        var packages = (await _uow.BillingRules.GetAllAsync()).ToDictionary(p => p.Id);

        var paid = invoices.Where(i => i.IsPaid).ToList();
        ViewBag.From = fromDate;
        ViewBag.To = toDate;
        ViewBag.Query = q ?? string.Empty;
        ViewBag.Agents = agents;
        ViewBag.Billings = billings;
        ViewBag.Packages = packages;
        ViewBag.PaidTotal = paid.Sum(i => i.Total);
        ViewBag.PaidCount = paid.Count;
        ViewBag.UnpaidCount = invoices.Count - paid.Count;
        // Grouped on the region label the invoice was issued under -- this is the remittance view.
        ViewBag.TaxByRegion = paid
            .Where(i => i.TaxAmount > 0)
            .GroupBy(i => i.TaxRegion)
            .Select(g => new { Region = g.Key, Taxable = g.Sum(i => i.SubTotal), Tax = g.Sum(i => i.TaxAmount) })
            .OrderByDescending(g => g.Tax)
            .Cast<dynamic>()
            .ToList();
        return View(invoices);
    }

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
        return View(invoice);
    }

    public async Task<IActionResult> InvoicesCsv(DateTime? from = null, DateTime? to = null, string? q = null)
    {
        var (invoices, fromDate, toDate) = await LoadLedgerAsync(from, to, q);
        var agents = (await _uow.AgentUsers.GetAllAsync()).ToDictionary(a => a.Id);
        var billings = (await _uow.Billings.GetAllAsync()).ToDictionary(b => b.Id);
        var packages = (await _uow.BillingRules.GetAllAsync()).ToDictionary(p => p.Id);

        static string Csv(string? value)
        {
            value ??= string.Empty;
            return value.Contains(',') || value.Contains('"') || value.Contains('\n')
                ? $"\"{value.Replace("\"", "\"\"")}\""
                : value;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("InvoiceNumber,IssuedAtUtc,BillToName,BillToCompany,BillToEmail,AgentUserId,AgentAccount,Package,SubTotal,TaxRegion,TaxRatePercent,TaxAmount,Total,Currency,Paid,PayPalTransactionId");
        foreach (var i in invoices)
        {
            var package = billings.TryGetValue(i.BillingId, out var b) && packages.TryGetValue(b.BillingRuleId, out var p)
                ? p.PackageName : string.Empty;
            var account = agents.TryGetValue(i.AgentUserId, out var a) ? a.UserName : "(deleted)";
            sb.AppendLine(string.Join(",",
                Csv(i.InvoiceNumber), i.IssuedAt.ToString("yyyy-MM-dd HH:mm"), Csv(i.BillToName), Csv(i.BillToCompany),
                Csv(i.BillToEmail), i.AgentUserId.ToString(), Csv(account), Csv(package),
                i.SubTotal.ToString("0.00"), Csv(i.TaxRegion), (i.TaxRate * 100).ToString("0.###"),
                i.TaxAmount.ToString("0.00"), i.Total.ToString("0.00"), Csv(i.Currency),
                i.IsPaid ? "yes" : "no", Csv(i.PayPalTransactionId)));
        }

        var name = $"ipro-invoices-{fromDate:yyyyMMdd}-{toDate:yyyyMMdd}.csv";
        return File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", name);
    }

    private async Task<(List<Invoice> Invoices, DateTime From, DateTime To)> LoadLedgerAsync(DateTime? from, DateTime? to, string? q)
    {
        var fromDate = (from ?? DateTime.UtcNow.AddMonths(-12)).Date;
        var toDate = (to ?? DateTime.UtcNow).Date;
        var endExclusive = toDate.AddDays(1);

        var invoices = (await _uow.Invoices.FindAsync(i => i.IssuedAt >= fromDate && i.IssuedAt < endExclusive))
            .OrderByDescending(i => i.IssuedAt)
            .ToList();

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
        }

        return (invoices, fromDate, toDate);
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
