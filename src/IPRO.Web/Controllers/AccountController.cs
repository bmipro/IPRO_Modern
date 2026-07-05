using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using IPRO.Business.Interfaces;
using IPRO.Entities;
using IPRO.Email;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Web.Controllers;

public class AccountController : Controller
{
    private readonly IAgentService _agents;
    private readonly IEmailService _email;
    private readonly ILogger<AccountController> _logger;

    public AccountController(IAgentService agents, IEmailService email, ILogger<AccountController> logger)
    {
        _agents = agents;
        _email = email;
        _logger = logger;
    }

    [HttpGet] public IActionResult Login() => View();

    [HttpPost]
    public async Task<IActionResult> Login(string username, string password, bool rememberMe = false)
    {
        var user = await _agents.AuthenticateAsync(username, password);
        if (user == null) { ModelState.AddModelError("", "Invalid username or password."); return View(); }
        var props = new AuthenticationProperties { IsPersistent = rememberMe, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(rememberMe ? 168 : 8) };
        await SignInAgentAsync(user, props);
        await _agents.UpdateLastLoginAsync(user.Id);
        if (user.MustChangePassword) return RedirectToAction(nameof(ChangePassword));
        return RedirectToAction("Index", "Dashboard");
    }

    [HttpGet]
    public IActionResult Register() => View(new AgentUser());

    [HttpPost]
    public async Task<IActionResult> Register(AgentUser model, string verificationCode, bool acceptTerms = false)
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
        if (verificationCode != "5345") ModelState.AddModelError("", "Verify code is incorrect.");
        if (!acceptTerms) ModelState.AddModelError("", "You must accept the terms and conditions.");
        if (!ModelState.IsValid) return View(model);
        if (await _agents.EmailExistsAsync(model.Email.Trim()))
        {
            ModelState.AddModelError("", "An account already exists for this email address.");
            return View(model);
        }

        NormalizeRegistration(model);
        model.UserName = await GenerateUniqueUserNameAsync(model.FirstName, model.LastName);
        model.DomainName = await GenerateUniqueDomainAsync(model.UserName);
        model.PackageId = model.PackageId <= 0 ? 1 : model.PackageId;
        model.TermsAcceptedAt = DateTime.UtcNow;
        model.RegistrationIpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
        model.MustChangePassword = true;
        var temporaryPassword = GenerateTemporaryPassword(model.FirstName, model.LastName);
        try
        {
            await _agents.RegisterAsync(model, temporaryPassword);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registration failed for {Email}", model.Email);
            ModelState.AddModelError("", "We could not complete the registration. Please check the form and try again.");
            return View(model);
        }

        var welcome = BuildWelcomeModel(model, temporaryPassword);
        var emailSent = await _email.SendAsync(
            model.Email,
            welcome.FullName,
            "Account Registration",
            RegistrationWelcomeTemplate.BuildHtml(welcome),
            RegistrationWelcomeTemplate.BuildText(welcome));
        if (!emailSent)
        {
            _logger.LogWarning("Registration welcome email was not sent to {Email}", model.Email);
        }

        TempData["RegistrationFullName"] = welcome.FullName;
        TempData["RegistrationEmail"] = welcome.Email;
        TempData["RegistrationUserName"] = model.UserName;
        TempData["RegistrationPassword"] = temporaryPassword;
        TempData["RegistrationDomain"] = model.DomainName;
        return RedirectToAction(nameof(RegisterSuccess));
    }

    [HttpGet]
    public IActionResult RegisterSuccess()
    {
        var welcome = new RegistrationWelcomeModel
        {
            FullName = TempData["RegistrationFullName"] as string ?? string.Empty,
            Email = TempData["RegistrationEmail"] as string ?? string.Empty,
            UserName = TempData["RegistrationUserName"] as string ?? string.Empty,
            TemporaryPassword = TempData["RegistrationPassword"] as string ?? string.Empty,
            SetupDomain = TempData["RegistrationDomain"] as string ?? string.Empty
        };

        if (string.IsNullOrWhiteSpace(welcome.UserName))
        {
            welcome = RegistrationWelcomeTemplate.Sample();
        }

        return View(welcome);
    }

    [Authorize]
    [HttpGet]
    public IActionResult ChangePassword() => View();

    [Authorize]
    [HttpPost]
    public async Task<IActionResult> ChangePassword(string newPassword, string confirmPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
            ModelState.AddModelError("", "New password must be at least 8 characters.");
        if (newPassword != confirmPassword)
            ModelState.AddModelError("", "Passwords do not match.");
        if (!ModelState.IsValid) return View();

        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(idValue, out var id)) return RedirectToAction(nameof(Login));

        await _agents.ChangePasswordAsync(id, newPassword);
        var user = await _agents.GetByIdAsync(id);
        if (user != null)
        {
            await SignInAgentAsync(user, new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });
        }
        return RedirectToAction("Index", "Dashboard");
    }

    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Login");
    }
    public IActionResult AccessDenied() => View();

    private async Task SignInAgentAsync(AgentUser user, AuthenticationProperties props)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Email, user.Email),
            new("FullName", $"{user.FirstName} {user.LastName}"),
            new("PackageId", user.PackageId.ToString()),
            new("MustChangePassword", user.MustChangePassword ? "true" : "false")
        };
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            props);
    }

    private async Task<string> GenerateUniqueUserNameAsync(string firstName, string lastName)
    {
        var baseName = NormalizeIdentifier($"{firstName}{lastName}");
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "agent";

        var candidate = baseName;
        var suffix = 1;
        while (await _agents.UsernameExistsAsync(candidate))
        {
            candidate = $"{baseName}{suffix}";
            suffix++;
        }
        return candidate;
    }

    private async Task<string> GenerateUniqueDomainAsync(string userName)
    {
        var baseName = NormalizeIdentifier(userName);
        var candidate = $"{baseName}.247Advisers.com";
        var suffix = 1;
        while (await _agents.DomainExistsAsync(candidate))
        {
            candidate = $"{baseName}{suffix}.247Advisers.com";
            suffix++;
        }
        return candidate;
    }

    private static string NormalizeIdentifier(string value)
    {
        return Regex.Replace(value, "[^A-Za-z0-9]", "");
    }

    private static string GenerateTemporaryPassword(string firstName, string lastName)
    {
        var initials = $"{firstName.FirstOrDefault()}{lastName.FirstOrDefault()}".ToUpperInvariant();
        var random = RandomNumberGenerator.GetInt32(100000, 999999);
        return $"IPRO-{initials}-{DateTime.UtcNow:yyyyMMdd}-{random}!";
    }

    private static RegistrationWelcomeModel BuildWelcomeModel(AgentUser model, string temporaryPassword) => new()
    {
        FullName = $"{model.FirstName} {model.LastName}".Trim(),
        Email = model.Email,
        UserName = model.UserName,
        TemporaryPassword = temporaryPassword,
        SetupDomain = model.DomainName
    };

    private static void NormalizeRegistration(AgentUser model)
    {
        model.FirstName = model.FirstName.Trim();
        model.LastName = model.LastName.Trim();
        model.Email = model.Email.Trim();
        model.Designation = model.Designation?.Trim() ?? "";
        model.CompanyName = model.CompanyName.Trim();
        model.CompanyAddress = model.CompanyAddress?.Trim() ?? "";
        model.City = model.City.Trim();
        model.Province = model.Province.Trim();
        model.PostalCode = model.PostalCode.Trim();
        model.Country = model.Country.Trim();
        model.TimeZone = model.TimeZone?.Trim() ?? "";
        model.Phone = model.Phone.Trim();
        model.BusinessFax = model.BusinessFax?.Trim() ?? "";
        model.CellPhone = model.CellPhone?.Trim() ?? "";
        model.BusinessType = model.BusinessType?.Trim() ?? "";
        model.PromotionCode = model.PromotionCode?.Trim() ?? "";
    }
}
