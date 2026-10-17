using System.Security.Claims;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using IPRO.Web.Infrastructure;
using EndpointRateLimits = IPRO.Web.Infrastructure.EndpointRateLimits;

namespace IPRO.Web.Controllers;

[AllowAnonymous]
public class ClientPortalAccountController : Controller
{
    private const string CandidatesSessionKey = "PortalLoginCandidates";

    private readonly IPRODbContext _db;
    private readonly IPasswordHasher<Client> _hasher;
    private readonly ISignInLockNotices _lockNotices;
    private readonly ILogger<ClientPortalAccountController> _logger;

    public ClientPortalAccountController(IPRODbContext db, IPasswordHasher<Client> hasher, ISignInLockNotices lockNotices, ILogger<ClientPortalAccountController> logger)
    {
        _db = db;
        _hasher = hasher;
        _lockNotices = lockNotices;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [EnableRateLimiting(EndpointRateLimits.SignIn)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string email, string password, string? returnUrl = null)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var normalizedEmail = (email?.Trim() ?? string.Empty).ToLowerInvariant();
        var candidates = normalizedEmail.Length == 0 ? new List<Client>() : (await _db.Clients
            .Include(c => c.AgentUser)
            .Where(c => c.Email.ToLower() == normalizedEmail && c.PortalPasswordHash != null)
            .ToListAsync())
            // 583: by its letters, as the lock counts an email that is no client (SignInLockout.SameName).
            .Where(c => SignInLockout.SameName(c.Email, normalizedEmail))
            .ToList();

        // 583: each row is a sign-in of its own (a client with two advisers has two), counted by
        // SignInLockout: 5 wrong passwords in a row lock it for 15 minutes. A locked row's password is
        // not checked; an email that is no client counts too, so its fifth try reads the same.
        var attempt = await SignInLockout.TryAsync(_db, SignInDoor.ClientPortal, normalizedEmail,
            candidates.Select(c => new SignInCandidate(new SignInAccount(SignInAccountKind.Client, c.Id),
                () => !string.IsNullOrEmpty(password) && _hasher.VerifyHashedPassword(c, c.PortalPasswordHash!, password) == PasswordVerificationResult.Success)).ToList(),
            DateTime.UtcNow);
        SignInLockout.LogLocks(_logger, attempt, SignInDoor.ClientPortal, IPRO.Utility.RateLimitVisitor.Key(HttpContext));
        foreach (var account in attempt.LockedNow) _lockNotices.Queue(account);
        var matches = candidates.Where(c => attempt.Matched.Contains(new SignInAccount(SignInAccountKind.Client, c.Id))).ToList();

        if (matches.Count == 0)
        {
            // An email that is no client costs no password check, and a locked one none either - a
            // refusal for them would come back faster than a real client's wrong password and tell
            // which emails are registered. Every refusal is answered 1.5 seconds after the request began
            // (SignInLockout.PadAsync).
            await SignInLockout.PadAsync(started);
            ModelState.AddModelError("", attempt.Locked ? SignInMessages.ClientPortalLocked : "Invalid email or password.");
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
        var client = await FindByInviteTokenAsync(token);
        if (client == null) return NotFound();

        return View(new PortalActivateViewModel { Token = token, CompanyName = client.AgentUser?.CompanyName ?? string.Empty });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Activate(string token, string password, string confirmPassword)
    {
        var client = await FindByInviteTokenAsync(token);
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
        client.PortalInviteTokenExpiresAt = null;
        await _db.SaveChangesAsync();
        // 583: a new invitation is a client's way out of a sign-in lock: the password chosen here unlocks it.
        if (await SignInLockout.ClearAsync(_db, new SignInAccount(SignInAccountKind.Client, client.Id), DateTime.UtcNow))
            SignInLockout.LogCleared(_logger, new SignInAccount(SignInAccountKind.Client, client.Id), "new portal invitation");

        await SignInClientAsync(client);
        return RedirectToAction("Index", "ClientPortal");
    }

    // A missing/empty token must NEVER match a row. EF rewrites `PortalInviteToken == null` into
    // `IS NULL`, and NULL is the normal state for every uninvited client (all public-lead-form
    // contacts) and every activated/revoked one -- so an omitted token would otherwise match an
    // arbitrary client of an arbitrary agent and hand the caller their portal (the 2026-08-14
    // ultra-audit's critical finding). The guard here mirrors EmailPreferencesController's, which
    // documents the identical attack. Requiring a non-null expiry is defence in depth: every real
    // invite sets one (ClientsController.InvitePortal), so this can only ever reject a forged call.
    private async Task<Client?> FindByInviteTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        return await _db.Clients.Include(c => c.AgentUser).FirstOrDefaultAsync(c =>
            c.PortalInviteToken == token
            && c.PortalInviteTokenExpiresAt != null
            && c.PortalInviteTokenExpiresAt > DateTime.UtcNow);
    }

    // 455 (2026-09-02): the logout form renders on pages authorized with the ClientPortal scheme,
    // so its antiforgery token is bound to the CLIENT identity. This controller is [AllowAnonymous];
    // without an Authorize of its own the POST arrived with HttpContext.User = the default scheme's
    // principal (the agent's cookie, or anonymous) and the antiforgery filter refused the token as
    // meant for a different user -- 400 Bad Request on the owner's first click. Authenticating
    // against ClientPortal here installs the client principal before the token is checked.
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(AuthenticationSchemes = "ClientPortal")]
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
