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

    [HttpGet]
    [Route("/pub/register.aspx")]
    public IActionResult Register() => View(new AgentUser());

    [HttpPost]
    public async Task<IActionResult> Register(AgentUser model, string password, string confirmPassword, string verificationCode, bool acceptTerms = false)
    {
        if (string.IsNullOrWhiteSpace(model.FirstName)) ModelState.AddModelError("", "First name is required.");
        if (string.IsNullOrWhiteSpace(model.LastName)) ModelState.AddModelError("", "Last name is required.");
        if (string.IsNullOrWhiteSpace(model.Email)) ModelState.AddModelError("", "Email is required.");
        if (string.IsNullOrWhiteSpace(model.CompanyName)) ModelState.AddModelError("", "Company name is required.");
        if (string.IsNullOrWhiteSpace(model.City)) ModelState.AddModelError("", "City is required.");
        if (string.IsNullOrWhiteSpace(model.Province)) ModelState.AddModelError("", "Province is required.");
        if (string.IsNullOrWhiteSpace(model.PostalCode)) ModelState.AddModelError("", "Postal code is required.");
        if (string.IsNullOrWhiteSpace(model.Country)) ModelState.AddModelError("", "Country is required.");
        if (string.IsNullOrWhiteSpace(model.Phone)) ModelState.AddModelError("", "Business phone is required.");
        if (string.IsNullOrWhiteSpace(model.BusinessType)) ModelState.AddModelError("", "Business type is required.");
        if (string.IsNullOrWhiteSpace(model.UserName)) ModelState.AddModelError("", "Username is required.");
        if (string.IsNullOrWhiteSpace(model.DomainName)) ModelState.AddModelError("", "Website/domain name is required.");
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8) ModelState.AddModelError("", "Password must be at least 8 characters.");
        if (verificationCode != "5345") ModelState.AddModelError("", "Verify code is incorrect.");
        if (!acceptTerms) ModelState.AddModelError("", "You must accept the terms and conditions.");
        if (password != confirmPassword) { ModelState.AddModelError("", "Passwords do not match."); return View(model); }
        if (!ModelState.IsValid) return View(model);
        if (await _agents.UsernameExistsAsync(model.UserName)) { ModelState.AddModelError("", "Username already taken."); return View(model); }
        if (await _agents.DomainExistsAsync(model.DomainName)) { ModelState.AddModelError("", "Website/domain name already taken."); return View(model); }
        model.PackageId = model.PackageId <= 0 ? 1 : model.PackageId;
        model.TermsAcceptedAt = DateTime.UtcNow;
        model.RegistrationIpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
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
