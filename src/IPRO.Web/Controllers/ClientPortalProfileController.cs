using System.Security.Claims;
using IPRO.DataAccess;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize(AuthenticationSchemes = "ClientPortal")]
public class ClientPortalProfileController : Controller
{
    private readonly IPRODbContext _db;
    private int ClientId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public ClientPortalProfileController(IPRODbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == ClientId);
        if (client == null) return NotFound();

        return View(new PortalProfileViewModel
        {
            FirstName = client.FirstName,
            LastName = client.LastName,
            Email = client.Email,
            Phone = client.Phone,
            CellPhone = client.CellPhone,
            Address = client.Address,
            City = client.City,
            Province = client.Province,
            PostalCode = client.PostalCode,
            Country = client.Country
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(PortalProfileViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == ClientId);
        if (client == null) return NotFound();

        client.FirstName = model.FirstName.Trim();
        client.LastName = model.LastName.Trim();
        client.Email = model.Email.Trim().ToLowerInvariant();
        client.Phone = model.Phone?.Trim() ?? string.Empty;
        client.CellPhone = model.CellPhone?.Trim() ?? string.Empty;
        client.Address = model.Address?.Trim() ?? string.Empty;
        client.City = model.City?.Trim() ?? string.Empty;
        client.Province = model.Province?.Trim() ?? string.Empty;
        client.PostalCode = model.PostalCode?.Trim() ?? string.Empty;
        client.Country = model.Country?.Trim() ?? string.Empty;
        client.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Your information was updated.";
        return RedirectToAction(nameof(Index));
    }
}
