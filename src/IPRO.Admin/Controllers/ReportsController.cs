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
