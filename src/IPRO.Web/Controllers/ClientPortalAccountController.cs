using System.Security.Claims;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[AllowAnonymous]
public class ClientPortalAccountController : Controller
{
    private const string CandidatesSessionKey = "PortalLoginCandidates";

    private readonly IPRODbContext _db;
    private readonly IPasswordHasher<Client> _hasher;

    public ClientPortalAccountController(IPRODbContext db, IPasswordHasher<Client> hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string email, string password, string? returnUrl = null)
    {
        var normalizedEmail = (email?.Trim() ?? string.Empty).ToLowerInvariant();
        var candidates = await _db.Clients
            .Include(c => c.AgentUser)
            .Where(c => c.Email.ToLower() == normalizedEmail && c.PortalPasswordHash != null)
            .ToListAsync();

        var matches = candidates
            .Where(c => _hasher.VerifyHashedPassword(c, c.PortalPasswordHash!, password) == PasswordVerificationResult.Success)
            .ToList();

        if (matches.Count == 0)
        {
            ModelState.AddModelError("", "Invalid email or password.");
            ViewData["ReturnUrl"] = returnUrl;
            return View();
        }

        if (matches.Count > 1)
        {
            HttpContext.Session.SetString(CandidatesSessionKey, string.Join(",", matches.Select(c => c.Id)));
            return View("ChooseAccount", matches);
        }

        await SignInClientAsync(matches[0]);
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }
        return RedirectToAction("Index", "ClientPortal");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ChooseAccount(int clientId)
    {
        var allowed = (HttpContext.Session.GetString(CandidatesSessionKey) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(id => int.TryParse(id, out var value) ? value : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();

        if (!allowed.Contains(clientId)) return Forbid();

        var client = await _db.Clients.Include(c => c.AgentUser).FirstOrDefaultAsync(c => c.Id == clientId);
        if (client == null) return Forbid();

        HttpContext.Session.Remove(CandidatesSessionKey);
        await SignInClientAsync(client);
        return RedirectToAction("Index", "ClientPortal");
    }

    [HttpGet]
    public async Task<IActionResult> Activate(string token)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.PortalInviteToken == token);
        if (client == null) return NotFound();

        return View(new PortalActivateViewModel { Token = token, CompanyName = client.AgentUser?.CompanyName ?? string.Empty });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Activate(string token, string password, string confirmPassword)
    {
        var client = await _db.Clients.Include(c => c.AgentUser).FirstOrDefaultAsync(c => c.PortalInviteToken == token);
        if (client == null) return NotFound();

        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            ModelState.AddModelError("", "Password must be at least 8 characters.");
        }
        if (password != confirmPassword)
        {
            ModelState.AddModelError("", "Passwords do not match.");
        }
        if (!ModelState.IsValid)
        {
            return View(new PortalActivateViewModel { Token = token, CompanyName = client.AgentUser?.CompanyName ?? string.Empty });
        }

        client.PortalPasswordHash = _hasher.HashPassword(client, password);
        client.PortalActivatedAt = DateTime.UtcNow;
        client.PortalInviteToken = null;
        await _db.SaveChangesAsync();

        await SignInClientAsync(client);
        return RedirectToAction("Index", "ClientPortal");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync("ClientPortal");
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    public IActionResult AccessDenied() => View();

    private async Task SignInClientAsync(Client client)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, client.Id.ToString()),
            new("AgentUserId", client.AgentUserId.ToString()),
            new(ClaimTypes.Email, client.Email),
            new("FullName", $"{client.FirstName} {client.LastName}".Trim())
        };
        await HttpContext.SignInAsync(
            "ClientPortal",
            new ClaimsPrincipal(new ClaimsIdentity(claims, "ClientPortal")),
            new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });
    }
}

public class PortalActivateViewModel
{
    public string Token { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
}
