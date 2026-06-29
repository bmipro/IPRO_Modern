using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Web.Controllers;

public class AccountController : Controller
{
    private readonly IAgentService _agents;
    public AccountController(IAgentService agents) => _agents = agents;

    [HttpGet] public IActionResult Login() => View();

    [HttpPost]
    public async Task<IActionResult> Login(string username, string password, bool rememberMe = false)
    {
        var user = await _agents.AuthenticateAsync(username, password);
        if (user == null) { ModelState.AddModelError("", "Invalid username or password."); return View(); }
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Email, user.Email),
            new("FullName", $"{user.FirstName} {user.LastName}"),
            new("PackageId", user.PackageId.ToString())
        };
        var props = new AuthenticationProperties { IsPersistent = rememberMe, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(rememberMe ? 168 : 8) };
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)), props);
        await _agents.UpdateLastLoginAsync(user.Id);
        return RedirectToAction("Index", "Dashboard");
    }

    [HttpGet] public IActionResult Register() => View(new AgentUser());

    [HttpPost]
    public async Task<IActionResult> Register(AgentUser model, string password, string confirmPassword)
    {
        if (password != confirmPassword) { ModelState.AddModelError("", "Passwords do not match."); return View(model); }
        if (await _agents.UsernameExistsAsync(model.UserName)) { ModelState.AddModelError("", "Username already taken."); return View(model); }
        await _agents.RegisterAsync(model, password);
        return RedirectToAction("Login");
    }

    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Login");
    }
    public IActionResult AccessDenied() => View();
}
