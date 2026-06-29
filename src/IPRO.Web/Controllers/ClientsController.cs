using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Web.Controllers;

[Authorize]
public class ClientsController : Controller
{
    private readonly IClientService _clients;
    private readonly IContactImporter _importer;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public ClientsController(IClientService clients, IContactImporter importer)
    { _clients = clients; _importer = importer; }

    public async Task<IActionResult> Index(string? search)
    {
        var clients = string.IsNullOrWhiteSpace(search)
            ? await _clients.GetByAgentAsync(AgentId)
            : await _clients.SearchAsync(AgentId, search);
        ViewBag.Search     = search;
        ViewBag.TotalCount = await _clients.GetCountAsync(AgentId);
        return View(clients);
    }

    public async Task<IActionResult> Details(int id)
    {
        var client = await _clients.GetByIdAsync(id);
        if (client == null || client.AgentUserId != AgentId) return NotFound();
        ViewBag.Comments = await _clients.GetCommentsAsync(id);
        return View(client);
    }

    public IActionResult Create() => View(new Client());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Client model)
    {
        if (!ModelState.IsValid) return View(model);
        model.AgentUserId = AgentId;
        await _clients.CreateAsync(model);
        TempData["Success"] = $"{model.FirstName} {model.LastName} added successfully.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var client = await _clients.GetByIdAsync(id);
        if (client == null || client.AgentUserId != AgentId) return NotFound();
        return View(client);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Client model)
    {
        if (!ModelState.IsValid) return View(model);
        if (model.AgentUserId != AgentId) return Forbid();
        await _clients.UpdateAsync(model);
        TempData["Success"] = "Client updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var client = await _clients.GetByIdAsync(id);
        if (client == null || client.AgentUserId != AgentId) return NotFound();
        await _clients.DeleteAsync(id);
        TempData["Success"] = "Client deleted.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddComment(int clientId, string comment)
    {
        var client = await _clients.GetByIdAsync(clientId);
        if (client == null || client.AgentUserId != AgentId) return NotFound();
        await _clients.AddCommentAsync(new ClientComment { ClientId = clientId, Comment = comment });
        return RedirectToAction(nameof(Details), new { id = clientId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportCsv(IFormFile file)
    {
        if (file == null || file.Length == 0)
        { TempData["Error"] = "Please select a CSV file."; return RedirectToAction(nameof(Index)); }

        using var stream = file.OpenReadStream();
        var result = await _importer.ImportCsvAsync(stream, AgentId);
        TempData["Success"] = $"Import complete: {result.Imported} imported, {result.Skipped} skipped, {result.Errors} errors.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> ExportCsv()
    {
        var clients = await _clients.GetByAgentAsync(AgentId);
        var bytes   = _importer.ExportToCsvAsync(clients);
        return File(bytes, "text/csv", $"clients-export-{DateTime.Now:yyyyMMdd}.csv");
    }
}
