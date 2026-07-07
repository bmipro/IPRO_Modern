using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class ClientsController : Controller
{
    private readonly IClientService _clients;
    private readonly IContactImporter _importer;
    private readonly IUnitOfWork _uow;
    private readonly IPRODbContext _db;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public ClientsController(IClientService clients, IContactImporter importer, IUnitOfWork uow, IPRODbContext db)
    { _clients = clients; _importer = importer; _uow = uow; _db = db; }

    public async Task<IActionResult> Index(string? search)
    {
        var query = _db.Clients
            .Include(c => c.Categories)
            .Where(c => c.AgentUserId == AgentId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(c =>
                c.FirstName.Contains(search) ||
                c.LastName.Contains(search) ||
                c.Email.Contains(search) ||
                c.Phone.Contains(search));
        }

        ViewBag.Search     = search;
        ViewBag.TotalCount = await _clients.GetCountAsync(AgentId);
        return View(await query.OrderByDescending(c => c.CreatedAt).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var client = await _db.Clients
            .Include(c => c.Categories)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (client == null || client.AgentUserId != AgentId) return NotFound();
        ViewBag.Comments = await _clients.GetCommentsAsync(id);
        return View(client);
    }

    public async Task<IActionResult> Create()
    {
        await LoadAccountTypesAsync();
        return View(new Client());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Client model, int[] categoryIds)
    {
        NormalizeClient(model);
        ClearOptionalClientModelState();
        ValidateClient(model);
        if (!ModelState.IsValid)
        {
            await LoadAccountTypesAsync(categoryIds);
            return View(model);
        }

        model.AgentUserId = AgentId;
        model.CreatedAt = DateTime.UtcNow;
        model.UpdatedAt = DateTime.UtcNow;
        await ApplyCategoriesAsync(model, categoryIds);
        await _db.Clients.AddAsync(model);
        await _db.SaveChangesAsync();

        TempData["Success"] = $"{model.FirstName} {model.LastName} added successfully.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var client = await _db.Clients
            .Include(c => c.Categories)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (client == null || client.AgentUserId != AgentId) return NotFound();
        await LoadAccountTypesAsync(client.Categories.Select(c => c.Id));
        return View(client);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Client model, int[] categoryIds)
    {
        var client = await _db.Clients
            .Include(c => c.Categories)
            .FirstOrDefaultAsync(c => c.Id == model.Id);
        if (client == null || client.AgentUserId != AgentId) return NotFound();

        model.AgentUserId = client.AgentUserId;
        NormalizeClient(model);
        ClearOptionalClientModelState();
        ValidateClient(model);
        if (!ModelState.IsValid)
        {
            await LoadAccountTypesAsync(categoryIds);
            return View(model);
        }

        ApplyClientFields(client, model);
        client.UpdatedAt = DateTime.UtcNow;
        client.Categories.Clear();
        await ApplyCategoriesAsync(client, categoryIds);
        await _db.SaveChangesAsync();

        TempData["Success"] = "Client updated.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> AccountTypes()
    {
        ViewBag.AccountTypes = (await _uow.ClientCategories.FindAsync(c => c.AgentUserId == AgentId))
            .OrderBy(c => c.Name)
            .ToList();
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateAccountType(string name, string? description)
    {
        name = name?.Trim() ?? "";
        description = description?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "Account type name is required.";
            return RedirectToAction(nameof(AccountTypes));
        }

        var exists = await _uow.ClientCategories.ExistsAsync(c =>
            c.AgentUserId == AgentId && c.Name == name);
        if (exists)
        {
            TempData["Error"] = "That account type already exists.";
            return RedirectToAction(nameof(AccountTypes));
        }

        await _uow.ClientCategories.AddAsync(new ClientCategory
        {
            AgentUserId = AgentId,
            Name = name,
            Description = description
        });
        await _uow.SaveChangesAsync();

        TempData["Success"] = "Account type added.";
        return RedirectToAction(nameof(AccountTypes));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAccountType(int id)
    {
        var accountType = await _db.ClientCategories
            .Include(c => c.Clients)
            .FirstOrDefaultAsync(c => c.Id == id && c.AgentUserId == AgentId);
        if (accountType == null) return NotFound();

        _db.ClientCategories.Remove(accountType);
        await _db.SaveChangesAsync();
        TempData["Warning"] = "Account type deleted.";
        return RedirectToAction(nameof(AccountTypes));
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

    private async Task LoadAccountTypesAsync(IEnumerable<int>? selectedIds = null)
    {
        ViewBag.AccountTypes = (await _uow.ClientCategories.FindAsync(c => c.AgentUserId == AgentId))
            .OrderBy(c => c.Name)
            .ToList();
        ViewBag.SelectedCategoryIds = selectedIds?.ToHashSet() ?? new HashSet<int>();
    }

    private async Task ApplyCategoriesAsync(Client client, IEnumerable<int> categoryIds)
    {
        var selectedIds = categoryIds.Distinct().ToList();
        if (selectedIds.Count == 0) return;

        var categories = await _db.ClientCategories
            .Where(c => c.AgentUserId == AgentId && selectedIds.Contains(c.Id))
            .ToListAsync();
        foreach (var category in categories)
        {
            client.Categories.Add(category);
        }
    }

    private static void ApplyClientFields(Client client, Client model)
    {
        client.FirstName = model.FirstName;
        client.LastName = model.LastName;
        client.Email = model.Email;
        client.Phone = model.Phone;
        client.Address = model.Address;
        client.City = model.City;
        client.Province = model.Province;
        client.PostalCode = model.PostalCode;
        client.Country = model.Country;
        client.IsNewsletterSubscribed = model.IsNewsletterSubscribed;
        client.Notes = model.Notes;
    }

    private static void NormalizeClient(Client client)
    {
        client.FirstName = client.FirstName?.Trim() ?? "";
        client.LastName = client.LastName?.Trim() ?? "";
        client.Email = (client.Email?.Trim() ?? "").ToLowerInvariant();
        client.Phone = client.Phone?.Trim() ?? "";
        client.Address = client.Address?.Trim() ?? "";
        client.City = client.City?.Trim() ?? "";
        client.Province = client.Province?.Trim() ?? "";
        client.PostalCode = client.PostalCode?.Trim() ?? "";
        client.Country = string.IsNullOrWhiteSpace(client.Country) ? "Canada" : client.Country.Trim();
        client.Notes = client.Notes?.Trim() ?? "";
    }

    private void ValidateClient(Client client)
    {
        if (string.IsNullOrWhiteSpace(client.FirstName)) ModelState.AddModelError("", "First name is required.");
        if (string.IsNullOrWhiteSpace(client.LastName)) ModelState.AddModelError("", "Last name is required.");
        if (string.IsNullOrWhiteSpace(client.Email)) ModelState.AddModelError("", "Email is required.");
    }

    private void ClearOptionalClientModelState()
    {
        foreach (var key in new[]
        {
            nameof(Client.AgentUser),
            nameof(Client.Categories),
            nameof(Client.Comments),
            nameof(Client.AgentUserId),
            nameof(Client.Phone),
            nameof(Client.Address),
            nameof(Client.City),
            nameof(Client.Province),
            nameof(Client.PostalCode),
            nameof(Client.Country),
            nameof(Client.Notes)
        })
        {
            ModelState.Remove(key);
        }
    }
}
