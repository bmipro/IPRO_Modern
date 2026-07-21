using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Admin.Controllers;

[Authorize(Policy = "AdminAccess")]
public class AdminDashboardController : Controller
{
    private readonly IAgentService _agents;
    private readonly IUnitOfWork _uow;
    private readonly IPRODbContext _db;

    public AdminDashboardController(IAgentService agents, IUnitOfWork uow, IPRODbContext db)
    { _agents = agents; _uow = uow; _db = db; }

    public async Task<IActionResult> Index()
    {
        var allAgents     = (await _agents.GetAllAsync()).ToList();
        var allBillings   = (await _uow.Billings.GetAllAsync()).ToList();
        var monthInvoices = (await _uow.Invoices.FindAsync(i =>
                                i.IssuedAt >= DateTime.UtcNow.AddDays(-30) && i.IsPaid)).ToList();

        ViewBag.TotalAgents    = allAgents.Count;
        ViewBag.ActiveAgents   = allAgents.Count(a => a.IsActive);
        ViewBag.ActiveSubs     = allBillings.Count(b => b.Status == BillingStatus.Active);
        ViewBag.MonthRevenue   = monthInvoices.Sum(i => i.Total);
        ViewBag.TotalClients   = await _uow.Clients.CountAsync();
        ViewBag.RecentAgents   = allAgents.OrderByDescending(a => a.CreatedAt).Take(8).ToList();
        ViewBag.RecentInvoices = (await _uow.Invoices.FindAsync(i => i.IsPaid))
                                  .OrderByDescending(i => i.IssuedAt).Take(8).ToList();

        if (User.HasClaim("Role", AdminRoles.SuperAdmin))
        {
            var aiSettings = await _db.AiBillingSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1);
            if (aiSettings != null && aiSettings.TotalFundedUsd > 0)
            {
                var aiSpend = await _db.AiUsageDailyLogs.SumAsync(l => (decimal?)l.EstimatedCostUsd) ?? 0m;
                var aiRemainingPercent = ((aiSettings.TotalFundedUsd - aiSpend) / aiSettings.TotalFundedUsd) * 100m;
                ViewBag.AiUsageLow = aiRemainingPercent <= aiSettings.LowBalanceThresholdPercent;
                ViewBag.AiRemainingPercent = aiRemainingPercent;
            }
        }

        return View();
    }
}
