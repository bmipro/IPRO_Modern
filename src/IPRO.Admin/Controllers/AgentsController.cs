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

    public async Task<IActionResult> Edit(int id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();
        return View(agent);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, AgentUser model)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();

        NormalizeAgent(model);
        ValidateAgentEdit(model);
        await ValidateUniqueAgentFieldsAsync(id, model);
        if (!ModelState.IsValid) return View(model);

        agent.UserName = model.UserName;
        agent.Email = model.Email;
        agent.FirstName = model.FirstName;
        agent.LastName = model.LastName;
        agent.Designation = model.Designation;
        agent.CompanyName = model.CompanyName;
        agent.CompanyAddress = model.CompanyAddress;
        agent.City = model.City;
        agent.Province = model.Province;
        agent.PostalCode = model.PostalCode;
        agent.Country = model.Country;
        agent.TimeZone = model.TimeZone;
        agent.Phone = model.Phone;
        agent.BusinessFax = model.BusinessFax;
        agent.CellPhone = model.CellPhone;
        agent.BusinessType = model.BusinessType;
        agent.DomainName = model.DomainName;
        agent.PackageId = model.PackageId;
        agent.PromotionCode = model.PromotionCode;
        agent.IsActive = model.IsActive;
        agent.MustChangePassword = model.MustChangePassword;

        await _agents.UpdateAsync(agent);
        await LogAsync(id, "Edit", "Agent profile updated");
        TempData["Success"] = $"Agent {agent.UserName} updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();
        var userName = agent.UserName;

        await DeleteAgentOwnedDataAsync(id);
        _uow.AgentUsers.Remove(agent);
        await _uow.SaveChangesAsync();

        TempData["Warning"] = $"Agent {userName} deleted.";
        return RedirectToAction(nameof(Index));
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

    private async Task DeleteAgentOwnedDataAsync(int agentId)
    {
        RemoveEach(await _uow.OperateLogs.FindAsync(x => x.AgentUserId == agentId), _uow.OperateLogs);
        RemoveEach(await _uow.Invoices.FindAsync(x => x.AgentUserId == agentId), _uow.Invoices);
        RemoveEach(await _uow.Billings.FindAsync(x => x.AgentUserId == agentId), _uow.Billings);
        RemoveEach(await _uow.AgentWebsites.FindAsync(x => x.AgentUserId == agentId), _uow.AgentWebsites);
        RemoveEach(await _uow.Clients.FindAsync(x => x.AgentUserId == agentId), _uow.Clients);
        RemoveEach(await _uow.ClientCategories.FindAsync(x => x.AgentUserId == agentId), _uow.ClientCategories);
        RemoveEach(await _uow.NewsLetters.FindAsync(x => x.AgentUserId == agentId), _uow.NewsLetters);
        RemoveEach(await _uow.DripCampaigns.FindAsync(x => x.AgentUserId == agentId), _uow.DripCampaigns);
        RemoveEach(await _uow.Schedulers.FindAsync(x => x.AgentUserId == agentId), _uow.Schedulers);
        RemoveEach(await _uow.Articles.FindAsync(x => x.AgentUserId == agentId), _uow.Articles);
        RemoveEach(await _uow.Coupons.FindAsync(x => x.AgentUserId == agentId), _uow.Coupons);
        RemoveEach(await _uow.CalendarEvents.FindAsync(x => x.AgentUserId == agentId), _uow.CalendarEvents);
        RemoveEach(await _uow.Testimonials.FindAsync(x => x.AgentUserId == agentId), _uow.Testimonials);
    }

    private static void RemoveEach<T>(IEnumerable<T> entities, IRepository<T> repository) where T : class
    {
        foreach (var entity in entities)
        {
            repository.Remove(entity);
        }
    }

    private static void NormalizeAgent(AgentUser agent)
    {
        agent.UserName = agent.UserName?.Trim() ?? "";
        agent.Email = agent.Email?.Trim() ?? "";
        agent.FirstName = agent.FirstName?.Trim() ?? "";
        agent.LastName = agent.LastName?.Trim() ?? "";
        agent.Designation = agent.Designation?.Trim() ?? "";
        agent.CompanyName = agent.CompanyName?.Trim() ?? "";
        agent.CompanyAddress = agent.CompanyAddress?.Trim() ?? "";
        agent.City = agent.City?.Trim() ?? "";
        agent.Province = agent.Province?.Trim() ?? "";
        agent.PostalCode = agent.PostalCode?.Trim() ?? "";
        agent.Country = agent.Country?.Trim() ?? "";
        agent.TimeZone = agent.TimeZone?.Trim() ?? "";
        agent.Phone = agent.Phone?.Trim() ?? "";
        agent.BusinessFax = agent.BusinessFax?.Trim() ?? "";
        agent.CellPhone = agent.CellPhone?.Trim() ?? "";
        agent.BusinessType = agent.BusinessType?.Trim() ?? "";
        agent.DomainName = agent.DomainName?.Trim() ?? "";
        agent.PromotionCode = agent.PromotionCode?.Trim() ?? "";
    }

    private void ValidateAgentEdit(AgentUser agent)
    {
        if (string.IsNullOrWhiteSpace(agent.UserName)) ModelState.AddModelError("", "Username is required.");
        if (string.IsNullOrWhiteSpace(agent.Email)) ModelState.AddModelError("", "Email is required.");
        if (string.IsNullOrWhiteSpace(agent.FirstName)) ModelState.AddModelError("", "First name is required.");
        if (string.IsNullOrWhiteSpace(agent.LastName)) ModelState.AddModelError("", "Last name is required.");
        if (string.IsNullOrWhiteSpace(agent.CompanyName)) ModelState.AddModelError("", "Company name is required.");
        if (string.IsNullOrWhiteSpace(agent.City)) ModelState.AddModelError("", "City is required.");
        if (string.IsNullOrWhiteSpace(agent.Province)) ModelState.AddModelError("", "Province is required.");
        if (string.IsNullOrWhiteSpace(agent.PostalCode)) ModelState.AddModelError("", "Postal code is required.");
        if (string.IsNullOrWhiteSpace(agent.Country)) ModelState.AddModelError("", "Country is required.");
        if (string.IsNullOrWhiteSpace(agent.Phone)) ModelState.AddModelError("", "Business phone is required.");
        if (string.IsNullOrWhiteSpace(agent.BusinessType)) ModelState.AddModelError("", "Business type is required.");
        if (agent.PackageId <= 1) ModelState.AddModelError("", "Package is required.");
    }

    private async Task ValidateUniqueAgentFieldsAsync(int id, AgentUser agent)
    {
        var existingUserName = await _uow.AgentUsers.FirstOrDefaultAsync(a => a.UserName == agent.UserName && a.Id != id);
        if (existingUserName != null) ModelState.AddModelError("", "Username is already used by another agent.");

        var existingEmail = await _uow.AgentUsers.FirstOrDefaultAsync(a => a.Email == agent.Email && a.Id != id);
        if (existingEmail != null) ModelState.AddModelError("", "Email is already used by another agent.");

        if (!string.IsNullOrWhiteSpace(agent.DomainName))
        {
            var existingDomain = await _uow.AgentUsers.FirstOrDefaultAsync(a => a.DomainName == agent.DomainName && a.Id != id);
            if (existingDomain != null) ModelState.AddModelError("", "Domain is already used by another agent.");
        }
    }
}
