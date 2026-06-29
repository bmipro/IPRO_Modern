using IPRO.Business.Interfaces;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Admin.Controllers;

[Authorize]
public class AgentsController : Controller
{
    private readonly IAgentService _agents;
    private readonly IWebsiteService _websites;
    private readonly IUnitOfWork _uow;
    private readonly IPleskHostingService _plesk;

    public AgentsController(IAgentService agents, IWebsiteService websites,
        IUnitOfWork uow, IPleskHostingService plesk)
    { _agents = agents; _websites = websites; _uow = uow; _plesk = plesk; }

    public async Task<IActionResult> Index(string? search, string? status, int page = 1)
    {
        var all = await _agents.GetAllAsync();
        if (!string.IsNullOrWhiteSpace(search))
            all = all.Where(a => a.UserName.Contains(search, StringComparison.OrdinalIgnoreCase)
                              || a.Email.Contains(search, StringComparison.OrdinalIgnoreCase)
                              || a.FirstName.Contains(search, StringComparison.OrdinalIgnoreCase)
                              || a.LastName.Contains(search, StringComparison.OrdinalIgnoreCase));
        if (status == "active")   all = all.Where(a => a.IsActive);
        if (status == "inactive") all = all.Where(a => !a.IsActive);

        ViewBag.Search     = search;
        ViewBag.Status     = status;
        ViewBag.TotalCount = all.Count();
        return View(PaginationHelper.Paginate(all.OrderByDescending(a => a.CreatedAt), page, 20));
    }

    public async Task<IActionResult> Details(int id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();
        ViewBag.Website      = await _websites.GetByAgentIdAsync(id);
        ViewBag.Subscription = await _uow.Billings.FirstOrDefaultAsync(b => b.AgentUserId == id && b.Status == BillingStatus.Active);
        ViewBag.Invoices     = (await _uow.Invoices.FindAsync(i => i.AgentUserId == id)).OrderByDescending(i => i.IssuedAt).Take(10);
        ViewBag.ClientCount  = await _uow.Clients.CountAsync(c => c.AgentUserId == id);
        ViewBag.Logs         = (await _uow.OperateLogs.FindAsync(l => l.AgentUserId == id)).OrderByDescending(l => l.CreatedAt).Take(20);
        return View(agent);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Activate(int id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();
        agent.IsActive = true;
        await _agents.UpdateAsync(agent);
        if (!string.IsNullOrEmpty(agent.DomainName)) await _plesk.UnsuspendDomainAsync(agent.DomainName);
        await LogAsync(id, "Activate", "Agent activated");
        TempData["Success"] = $"Agent {agent.UserName} activated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Deactivate(int id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();
        await _agents.DeactivateAsync(id);
        if (!string.IsNullOrEmpty(agent.DomainName)) await _plesk.SuspendDomainAsync(agent.DomainName);
        await LogAsync(id, "Deactivate", "Agent deactivated");
        TempData["Warning"] = $"Agent {agent.UserName} deactivated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ProvisionHosting(int id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();
        if (string.IsNullOrEmpty(agent.DomainName))
        { TempData["Error"] = "No domain set for this agent."; return RedirectToAction(nameof(Details), new { id }); }
        var tempPwd = EncryptionService.GenerateToken(12);
        var domain  = await _plesk.CreateDomainAsync(agent.DomainName, agent.UserName, tempPwd, agent.Email);
        if (domain != null)
        {
            await _plesk.CreateEmailAsync($"info@{agent.DomainName}", tempPwd, agent.DomainName);
            await LogAsync(id, "ProvisionHosting", $"Hosting provisioned: {agent.DomainName}");
            TempData["Success"] = $"Hosting provisioned for {agent.DomainName}.";
        }
        else TempData["Error"] = "Provisioning failed — check Plesk connection.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PleskLogin(int id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();
        var url = await _plesk.GenerateAutoLoginUrlAsync(agent.UserName);
        if (string.IsNullOrEmpty(url)) { TempData["Error"] = "Could not generate Plesk login."; return RedirectToAction(nameof(Details), new { id }); }
        return Redirect(url);
    }

    private async Task LogAsync(int agentId, string action, string desc)
    {
        await _uow.OperateLogs.AddAsync(new OperateLog { AgentUserId = agentId, Action = action, Module = "Agents", Description = desc, CreatedAt = DateTime.UtcNow });
        await _uow.SaveChangesAsync();
    }
}
