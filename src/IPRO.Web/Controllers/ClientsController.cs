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

    public async Task<IActionResult> Index(string? search, int? accountTypeId, string? newsletter)
    {
        var query = _db.Clients
            .Include(c => c.Categories)
            .Where(c => c.AgentUserId == AgentId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            search = search.Trim();
            query = query.Where(c =>
                c.FirstName.Contains(search) ||
                c.LastName.Contains(search) ||
                c.Email.Contains(search) ||
                c.Email2.Contains(search) ||
                c.CompanyName.Contains(search) ||
                c.Phone.Contains(search) ||
                c.HomePhone2.Contains(search) ||
                c.BusinessPhone.Contains(search) ||
                c.BusinessPhone2.Contains(search) ||
                c.CellPhone.Contains(search) ||
                c.CellPhone2.Contains(search) ||
                c.City.Contains(search) ||
                c.Categories.Any(cat => cat.Name.Contains(search)));
        }

        if (accountTypeId.HasValue)
        {
            query = query.Where(c => c.Categories.Any(cat => cat.Id == accountTypeId.Value));
        }

        if (string.Equals(newsletter, "yes", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(c => c.IsNewsletterSubscribed);
        }
        else if (string.Equals(newsletter, "no", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(c => !c.IsNewsletterSubscribed);
        }

        ViewBag.Search     = search;
        ViewBag.AccountTypeId = accountTypeId;
        ViewBag.Newsletter = newsletter;
        ViewBag.TotalCount = await _clients.GetCountAsync(AgentId);
        ViewBag.AccountTypes = await _db.ClientCategories
            .Where(c => c.AgentUserId == AgentId)
            .OrderBy(c => c.Name)
            .ToListAsync();
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
        await ValidateUniqueEmailAsync(model.Email);
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
        await ValidateUniqueEmailAsync(model.Email, client.Id);
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
    public async Task<IActionResult> UpdateAccountType(int id, string name, string? description)
    {
        name = name?.Trim() ?? "";
        description = description?.Trim() ?? "";

        var accountType = await _db.ClientCategories
            .FirstOrDefaultAsync(c => c.Id == id && c.AgentUserId == AgentId);
        if (accountType == null) return NotFound();

        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "Account type name is required.";
            return RedirectToAction(nameof(AccountTypes));
        }

        var duplicate = await _db.ClientCategories.AnyAsync(c =>
            c.AgentUserId == AgentId &&
            c.Id != id &&
            c.Name.ToLower() == name.ToLower());
        if (duplicate)
        {
            TempData["Error"] = "That account type already exists.";
            return RedirectToAction(nameof(AccountTypes));
        }

        accountType.Name = name;
        accountType.Description = description;
        await _db.SaveChangesAsync();
        TempData["Success"] = "Account type updated.";
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
        var existingEmails = await _db.Clients
            .Where(c => c.AgentUserId == AgentId)
            .Select(c => c.Email.ToLower())
            .ToListAsync();
        var seenEmails = existingEmails.ToHashSet();
        var toImport = new List<Client>();
        var skipped = result.Skipped;

        foreach (var client in result.Clients)
        {
            NormalizeClient(client);
            if (string.IsNullOrWhiteSpace(client.FirstName) ||
                string.IsNullOrWhiteSpace(client.LastName) ||
                string.IsNullOrWhiteSpace(client.Email) ||
                seenEmails.Contains(client.Email))
            {
                skipped++;
                continue;
            }

            client.AgentUserId = AgentId;
            client.CreatedAt = DateTime.UtcNow;
            client.UpdatedAt = DateTime.UtcNow;
            seenEmails.Add(client.Email);
            toImport.Add(client);
        }

        if (toImport.Count > 0)
        {
            await _db.Clients.AddRangeAsync(toImport);
            await _db.SaveChangesAsync();
        }

        TempData["Success"] = $"Import complete: {toImport.Count} imported, {skipped} skipped, {result.Errors} errors.";
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
        client.DateOfBirth = model.DateOfBirth;
        client.CompanyName = model.CompanyName;
        client.Email = model.Email;
        client.Email2 = model.Email2;
        client.Phone = model.Phone;
        client.HomePhone2 = model.HomePhone2;
        client.BusinessPhone = model.BusinessPhone;
        client.BusinessPhone2 = model.BusinessPhone2;
        client.CellPhone = model.CellPhone;
        client.CellPhone2 = model.CellPhone2;
        client.Fax = model.Fax;
        client.Fax2 = model.Fax2;
        client.Address = model.Address;
        client.UnitNumber = model.UnitNumber;
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
        client.CompanyName = client.CompanyName?.Trim() ?? "";
        client.Email = (client.Email?.Trim() ?? "").ToLowerInvariant();
        client.Email2 = (client.Email2?.Trim() ?? "").ToLowerInvariant();
        client.Phone = client.Phone?.Trim() ?? "";
        client.HomePhone2 = client.HomePhone2?.Trim() ?? "";
        client.BusinessPhone = client.BusinessPhone?.Trim() ?? "";
        client.BusinessPhone2 = client.BusinessPhone2?.Trim() ?? "";
        client.CellPhone = client.CellPhone?.Trim() ?? "";
        client.CellPhone2 = client.CellPhone2?.Trim() ?? "";
        client.Fax = client.Fax?.Trim() ?? "";
        client.Fax2 = client.Fax2?.Trim() ?? "";
        client.Address = client.Address?.Trim() ?? "";
        client.UnitNumber = client.UnitNumber?.Trim() ?? "";
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

    private async Task ValidateUniqueEmailAsync(string email, int? currentClientId = null)
    {
        if (string.IsNullOrWhiteSpace(email)) return;

        var exists = await _db.Clients.AnyAsync(c =>
            c.AgentUserId == AgentId &&
            c.Email == email &&
            (!currentClientId.HasValue || c.Id != currentClientId.Value));
        if (exists)
        {
            ModelState.AddModelError(nameof(Client.Email), "A client with this email already exists.");
        }
    }

    private void ClearOptionalClientModelState()
    {
        foreach (var key in new[]
        {
            nameof(Client.AgentUser),
            nameof(Client.Categories),
            nameof(Client.Comments),
            nameof(Client.AgentUserId),
            nameof(Client.DateOfBirth),
            nameof(Client.CompanyName),
            nameof(Client.Email2),
            nameof(Client.Phone),
            nameof(Client.HomePhone2),
            nameof(Client.BusinessPhone),
            nameof(Client.BusinessPhone2),
            nameof(Client.CellPhone),
            nameof(Client.CellPhone2),
            nameof(Client.Fax),
            nameof(Client.Fax2),
            nameof(Client.Address),
            nameof(Client.UnitNumber),
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
