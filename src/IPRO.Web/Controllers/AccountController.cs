using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using IPRO.Billing;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Email;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IPRO.Web.Controllers;

public class AccountController : Controller
{
    private const string RegistrationVerifyCodeSessionKey = "RegistrationVerifyCode";
    private readonly IAgentService _agents;
    private readonly IEmailService _email;
    private readonly IUnitOfWork _uow;
    private readonly IBillingService _billing;
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IBlobStorageService _blob;
    private readonly ILogger<AccountController> _logger;
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;

    public AccountController(IAgentService agents, IEmailService email, IUnitOfWork uow, IBillingService billing, IPRODbContext db, IPackageEntitlementService entitlements, IBlobStorageService blob, ILogger<AccountController> logger, IConfiguration configuration, IServiceScopeFactory scopeFactory)
    {
        _agents = agents;
        _email = email;
        _uow = uow;
        _billing = billing;
        _db = db;
        _entitlements = entitlements;
        _blob = blob;
        _logger = logger;
        _configuration = configuration;
        _scopeFactory = scopeFactory;
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string username, string password, bool rememberMe = false, string? returnUrl = null)
    {
        var user = await _agents.AuthenticateAsync(username, password);
        if (user == null)
        {
            // AuthenticateAsync returns immediately (skipping password-hash verification entirely)
            // when the account doesn't exist, which is measurably faster than the wrong-password
            // case and lets an attacker enumerate valid usernames/emails by timing. A flat delay on
            // every failure (matching Admin's login) swamps that few-millisecond gap.
            await Task.Delay(1500);
            ModelState.AddModelError("", "Invalid username or password."); return View();
        }
        var props = new AuthenticationProperties { IsPersistent = rememberMe, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(rememberMe ? 168 : 8) };
        await SignInAgentAsync(user, props);
        await _agents.UpdateLastLoginAsync(user.Id);
        if (user.MustChangePassword) return RedirectToAction(nameof(ChangePassword));
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        // Explicit path, not RedirectToAction("Index", "Dashboard") - that generates "/" because
        // Dashboard/Index are the default route's default values, and "/" on an agent's own
        // domain (temporary or custom) is reserved for their public website homepage, not the
        // portal. Bare "/" would silently strand them on their own marketing site after signing in.
        return Redirect("/Dashboard");
    }

    [HttpGet]
    public IActionResult ForgotPassword() => View();

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgotPassword(string email)
    {
        email = email?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(email))
        {
            var agent = await _agents.InitiatePasswordResetAsync(email);
            if (agent != null)
            {
                var resetUrl = $"{PortalUrlHelper.GetAgentPortalBaseUrl(_configuration)}/Account/ResetPassword?token={System.Net.WebUtility.UrlEncode(agent.PasswordResetToken)}";
                var fullName = $"{agent.FirstName} {agent.LastName}".Trim();
                var html = $"""
                    <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
                      <div style="padding:22px;background:#193f82;color:white"><h1 style="margin:0;font-size:24px">IPRO Advisers</h1></div>
                      <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                        <p>Hi {System.Net.WebUtility.HtmlEncode(fullName)},</p>
                        <p>We received a request to reset your IPRO Advisers password. This link expires in 1 hour.</p>
                        <p><a href="{resetUrl}" style="display:inline-block;padding:11px 18px;background:#193f82;color:white;text-decoration:none;border-radius:6px">Reset My Password</a></p>
                        <p>If you didn't request this, you can safely ignore this email.</p>
                      </div>
                    </div>
                    """;
                // Fired without awaiting so the response time is identical whether or not the
                // account exists - awaiting it here would leak account existence via timing even
                // though the response body is the same either way.
                QueuePasswordResetEmail(agent.Email, fullName, html);
            }
        }

        // InitiatePasswordResetAsync does an extra DB write (token + expiry) only when the account
        // exists, a residual timing gap smaller than the email-send one above but still measurable.
        // A flat delay on every request (found or not) swamps it, same approach as the login timing
        // fix (M-NEW-4).
        await Task.Delay(300);
        return RedirectToAction(nameof(ForgotPasswordConfirmation));
    }

    private void QueuePasswordResetEmail(string toEmail, string fullName, string html)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<AccountController>>();
                try
                {
                    var emailSent = await email.SendDetailedAsync(toEmail, fullName, "Reset your IPRO Advisers password", html);
                    if (!emailSent.Success)
                    {
                        logger.LogWarning("Password reset email was not sent to {Email}: {Message}", toEmail, emailSent.Message);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Password reset email failed for {Email}", toEmail);
                }
            }
            catch
            {
                // Scope creation/service resolution itself failing here would otherwise be an
                // unobserved task exception with nothing to log to - the logger is one of the
                // things that failed to resolve, so there's genuinely nothing more useful to do
                // than swallow it.
            }
        });
    }

    [HttpGet]
    public IActionResult ForgotPasswordConfirmation() => View();

    [HttpGet]
    public async Task<IActionResult> ResetPassword(string token)
    {
        var agent = await _agents.GetByValidPasswordResetTokenAsync(token);
        ViewBag.TokenValid = agent != null;
        return View(new ResetPasswordViewModel { Token = token });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(string token, string newPassword, string confirmPassword)
    {
        var agent = await _agents.GetByValidPasswordResetTokenAsync(token);
        if (agent == null)
        {
            ViewBag.TokenValid = false;
            return View(new ResetPasswordViewModel { Token = token });
        }

        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
            ModelState.AddModelError("", "New password must be at least 8 characters.");
        if (newPassword != confirmPassword)
            ModelState.AddModelError("", "Passwords do not match.");
        if (!ModelState.IsValid)
        {
            ViewBag.TokenValid = true;
            return View(new ResetPasswordViewModel { Token = token });
        }

        var succeeded = await _agents.ResetPasswordByTokenAsync(token, newPassword);
        if (!succeeded)
        {
            ViewBag.TokenValid = false;
            return View(new ResetPasswordViewModel { Token = token });
        }

        var user = await _agents.GetByIdAsync(agent.Id);
        if (user != null)
        {
            await SignInAgentAsync(user, new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });
        }
        // Explicit path, not RedirectToAction("Index", "Dashboard") - that generates "/" because
        // Dashboard/Index are the default route's default values, and "/" on an agent's own
        // domain (temporary or custom) is reserved for their public website homepage, not the
        // portal. Bare "/" would silently strand them on their own marketing site after signing in.
        return Redirect("/Dashboard");
    }

    [HttpGet]
    public async Task<IActionResult> Register(string? trialCode = null, string? firstName = null, string? lastName = null, string? companyName = null, string? businessType = null, [FromQuery(Name = "package")] string? packageName = null)
    {
        SetRegistrationVerifyCode();
        await LoadActivePackagesAsync();

        if (!string.IsNullOrWhiteSpace(trialCode))
        {
            var (invite, package, error) = await ResolveTrialInviteAsync(trialCode);
            if (invite != null && package != null)
            {
                ViewBag.TrialPackage = package;
            }
            else
            {
                ViewBag.TrialCodeError = error ?? "This invitation link is not valid.";
            }
        }

        // Optional prefill from the unauthenticated /Preview flow -- a prospect who already typed
        // their name and vertical there shouldn't have to retype it at the exact moment they've
        // decided to sign up. Plain optional querystring values, same trust level as any other GET.
        var prefillPackageId = 0;
        if (!string.IsNullOrWhiteSpace(packageName) && ViewBag.Packages is IEnumerable<BillingRule> loadedPackages)
        {
            prefillPackageId = loadedPackages.FirstOrDefault(p => p.PackageName == packageName)?.Id ?? 0;
        }

        return View(new AgentRegistrationViewModel
        {
            TrialCode = trialCode,
            FirstName = firstName ?? string.Empty,
            LastName = lastName ?? string.Empty,
            CompanyName = companyName ?? string.Empty,
            BusinessType = businessType ?? string.Empty,
            PackageId = prefillPackageId
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(AgentRegistrationViewModel model, string verificationCode, bool acceptTerms = false)
    {
        NormalizeRegistration(model);
        var expectedVerificationCode = HttpContext.Session.GetString(RegistrationVerifyCodeSessionKey);
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
        BillingRule? submittedPackage = null;
        TrialInviteCode? trialInvite = null;
        if (model.PackageId <= 0) ModelState.AddModelError("", "Package is required.");
        else
        {
            submittedPackage = await _uow.BillingRules.FirstOrDefaultAsync(p => p.Id == model.PackageId && p.IsActive);
            if (submittedPackage == null)
            {
                ModelState.AddModelError("", "Please choose an active package.");
            }
            else if (submittedPackage.IsTrialPackage)
            {
                var (invite, package, error) = await ResolveTrialInviteAsync(model.TrialCode);
                if (invite == null || package == null || package.Id != submittedPackage.Id)
                {
                    ModelState.AddModelError("", error ?? "A valid invitation is required for this package.");
                }
                else
                {
                    trialInvite = invite;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(expectedVerificationCode)
            || !string.Equals(verificationCode?.Trim(), expectedVerificationCode, StringComparison.Ordinal))
        {
            ModelState.AddModelError("", "Verify code is incorrect.");
        }
        if (!acceptTerms) ModelState.AddModelError("", "You must accept the terms and conditions.");
        if (!string.IsNullOrWhiteSpace(model.PromotionCode) && model.PackageId > 0)
        {
            var promo = await _billing.ValidatePromotionCodeAsync(model.PromotionCode, model.PackageId);
            if (promo == null)
            {
                ModelState.AddModelError("", "That promotion code is not valid for the selected package, or has expired/reached its redemption limit.");
            }
        }
        if (!ModelState.IsValid)
        {
            SetRegistrationVerifyCode();
            await LoadActivePackagesAsync();
            await RepopulateTrialViewBagAsync(model.TrialCode);
            return View(model);
        }
        if (await _agents.EmailExistsAsync(model.Email))
        {
            ModelState.AddModelError("", "An account already exists for this email address.");
            SetRegistrationVerifyCode();
            await LoadActivePackagesAsync();
            await RepopulateTrialViewBagAsync(model.TrialCode);
            return View(model);
        }

        var agent = ToAgentUser(model);
        agent.UserName = await GenerateUniqueUserNameAsync(agent.FirstName, agent.LastName);
        agent.DomainName = await GenerateUniqueDomainAsync(agent.UserName);
        agent.TermsAcceptedAt = DateTime.UtcNow;
        agent.RegistrationIpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
        agent.MustChangePassword = true;
        if (trialInvite != null && submittedPackage != null)
        {
            agent.TrialEndsAt = DateTime.UtcNow.AddDays(submittedPackage.TrialDurationDays ?? 14);
        }
        var temporaryPassword = GenerateTemporaryPassword();
        try
        {
            await _agents.RegisterAsync(agent, temporaryPassword);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registration failed for {Email}", model.Email);
            ModelState.AddModelError("", "We could not complete the registration. Please check the form and try again.");
            SetRegistrationVerifyCode();
            await LoadActivePackagesAsync();
            await RepopulateTrialViewBagAsync(model.TrialCode);
            return View(model);
        }

        if (trialInvite != null)
        {
            // Atomic conditional increment, same reasoning as the promo-code redemption fix --
            // the database re-checks MaxRedemptions at update time instead of trusting a
            // stale in-memory RedemptionCount read earlier in this request.
            var claimed = await _db.TrialInviteCodes
                .Where(c => c.Id == trialInvite.Id && (c.MaxRedemptions == null || c.RedemptionCount < c.MaxRedemptions))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.RedemptionCount, c => c.RedemptionCount + 1));
            if (claimed == 0)
            {
                // Race loser (review H-10): the trial account already exists by this point, so
                // record the truth -- count past the cap and alert -- rather than leave the
                // counter and the redemption rows disagreeing.
                await _db.TrialInviteCodes
                    .Where(c => c.Id == trialInvite.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.RedemptionCount, c => c.RedemptionCount + 1));
                _logger.LogError(
                    "Trial invite code {TrialInviteCodeId} was redeemed past its cap by new agent {AgentUserId}. Review the code's limits.",
                    trialInvite.Id, agent.Id);
            }
            await _uow.TrialInviteCodeRedemptions.AddAsync(new TrialInviteCodeRedemption
            {
                TrialInviteCodeId = trialInvite.Id,
                AgentUserId = agent.Id,
                RedeemedAt = DateTime.UtcNow
            });
            await _uow.SaveChangesAsync();
        }

        var welcome = BuildWelcomeModel(agent, temporaryPassword);
        var emailSent = await _email.SendAsync(
            agent.Email,
            welcome.FullName,
            "Account Registration",
            RegistrationWelcomeTemplate.BuildHtml(welcome),
            RegistrationWelcomeTemplate.BuildText(welcome));
        if (!emailSent)
        {
            _logger.LogWarning("Registration welcome email was not sent to {Email}", agent.Email);
        }

        TempData["RegistrationEmailSent"] = emailSent ? "true" : "false";
        TempData["RegistrationFullName"] = welcome.FullName;
        TempData["RegistrationEmail"] = welcome.Email;
        TempData["RegistrationUserName"] = agent.UserName;
        TempData["RegistrationPassword"] = temporaryPassword;
        TempData["RegistrationDomain"] = agent.DomainName;
        TempData["RegistrationTrialEndsAt"] = agent.TrialEndsAt?.ToString("O");
        HttpContext.Session.Remove(RegistrationVerifyCodeSessionKey);
        return RedirectToAction(nameof(RegisterSuccess));
    }

    private async Task<(TrialInviteCode? Invite, BillingRule? Package, string? Error)> ResolveTrialInviteAsync(string? trialCode)
    {
        if (string.IsNullOrWhiteSpace(trialCode)) return (null, null, "An invitation code is required for this package.");

        var code = trialCode.Trim();
        var invite = await _uow.TrialInviteCodes.FirstOrDefaultAsync(c => c.Code == code);
        if (invite == null) return (null, null, "This invitation link is not valid.");
        if (!invite.IsActive) return (null, null, "This invitation is no longer active.");
        if (invite.ExpiresAt.HasValue && invite.ExpiresAt.Value < DateTime.UtcNow) return (null, null, "This invitation has expired.");
        if (invite.MaxRedemptions.HasValue && invite.RedemptionCount >= invite.MaxRedemptions.Value) return (null, null, "This invitation has already been used the maximum number of times.");

        var package = await _uow.BillingRules.GetByIdAsync(invite.BillingRuleId);
        if (package == null || !package.IsActive || !package.IsTrialPackage) return (null, null, "This invitation's package is no longer available.");

        return (invite, package, null);
    }

    private async Task RepopulateTrialViewBagAsync(string? trialCode)
    {
        if (string.IsNullOrWhiteSpace(trialCode)) return;
        var (invite, package, error) = await ResolveTrialInviteAsync(trialCode);
        if (invite != null && package != null)
        {
            ViewBag.TrialPackage = package;
        }
        else
        {
            ViewBag.TrialCodeError = error ?? "This invitation link is not valid.";
        }
    }

    [HttpPost]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> ValidatePromoCode(string code, int packageId)
    {
        var package = await _uow.BillingRules.FirstOrDefaultAsync(p => p.Id == packageId && p.IsActive);
        if (package == null)
        {
            return Json(new { valid = false, message = "Choose a valid package first." });
        }

        var promo = await _billing.ValidatePromotionCodeAsync(code, packageId);
        if (promo == null)
        {
            return Json(new { valid = false, message = "That code is not valid for the selected package, or has expired/reached its redemption limit." });
        }

        var parts = new List<string>();
        if (promo.RecurringDiscountType != PromoDiscountType.None)
        {
            var durationText = promo.RecurringDurationCycles == null
                ? "for the life of your subscription"
                : promo.RecurringDurationCycles == 1
                    ? "on your first billing cycle only"
                    : $"for your first {promo.RecurringDurationCycles} billing cycles";
            var discountText = promo.RecurringDiscountType == PromoDiscountType.PercentOff
                ? $"{promo.RecurringDiscountValue}% off"
                : $"${promo.RecurringDiscountValue} off";
            parts.Add($"{discountText} the recurring price {durationText}");
        }
        if (promo.SetupFeeDiscountType != PromoDiscountType.None)
        {
            var discountText = promo.SetupFeeDiscountType == PromoDiscountType.PercentOff
                ? $"{promo.SetupFeeDiscountValue}% off"
                : $"${promo.SetupFeeDiscountValue} off";
            parts.Add($"{discountText} the setup fee");
        }

        var message = parts.Count == 0
            ? "Code accepted."
            : $"Code accepted: {string.Join(" and ", parts)}.";
        return Json(new { valid = true, message });
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

        var trialEndsAtRaw = TempData["RegistrationTrialEndsAt"] as string;
        ViewBag.TrialEndsAt = !string.IsNullOrWhiteSpace(trialEndsAtRaw)
            ? DateTime.Parse(trialEndsAtRaw, null, System.Globalization.DateTimeStyles.RoundtripKind)
            : (DateTime?)null;

        return View(welcome);
    }

    [Authorize]
    [HttpGet]
    public IActionResult ChangePassword()
    {
        ViewBag.RequireCurrentPassword = !User.HasClaim(c => c.Type == "MustChangePassword" && c.Value == "true");
        return View();
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> Profile()
    {
        var agent = await GetCurrentAgentAsync();
        if (agent == null) return RedirectToAction(nameof(Login));

        // Show the package the agent is actually ON, not the one they signed up with.
        //
        // This used to read agent.PackageId alone, which nothing in the billing flow ever updated --
        // an agent who upgraded Silver -> Gold -> Platinum still saw "IPro Silver" here while the
        // Billing page correctly said Platinum. Reported 2026-08-06.
        //
        // The active Billing row is the same source entitlements resolve from
        // (PackageEntitlementService.ResolveBillingRuleIdAsync), so this page can no longer disagree
        // with what the agent can actually do. PackageId is now kept in sync as well, but reading the
        // billing row first means a stale value can never surface here again.
        var activeBilling = await _uow.Billings.FirstOrDefaultAsync(b =>
            b.AgentUserId == agent.Id && b.Status == BillingStatus.Active);
        var packageId = activeBilling?.BillingRuleId ?? agent.PackageId;
        var package = packageId > 0 ? await _uow.BillingRules.GetByIdAsync(packageId) : null;
        LoadTimeZoneOptions();

        ViewBag.GoogleCalendarAccess = await _entitlements.GetAccessAsync(agent.Id, PackageFeatureCodes.GoogleCalendarSync);
        ViewBag.GoogleCalendarConnection = await _db.GoogleCalendarConnections.FirstOrDefaultAsync(c => c.AgentUserId == agent.Id && c.IsActive);

        return View(ToProfileViewModel(agent, package?.PackageName ?? ""));
    }

    [Authorize]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Profile(AgentProfileViewModel model)
    {
        var agent = await GetCurrentAgentAsync();
        if (agent == null) return RedirectToAction(nameof(Login));

        NormalizeProfile(model);
        if (string.IsNullOrWhiteSpace(model.FirstName)) ModelState.AddModelError("", "First name is required.");
        if (string.IsNullOrWhiteSpace(model.LastName)) ModelState.AddModelError("", "Last name is required.");
        if (string.IsNullOrWhiteSpace(model.Email)) ModelState.AddModelError("", "Email is required.");
        if (string.IsNullOrWhiteSpace(model.CompanyName)) ModelState.AddModelError("", "Company name is required.");
        if (string.IsNullOrWhiteSpace(model.City)) ModelState.AddModelError("", "City is required.");
        if (string.IsNullOrWhiteSpace(model.Province)) ModelState.AddModelError("", "Province/state is required.");
        if (string.IsNullOrWhiteSpace(model.PostalCode)) ModelState.AddModelError("", "Postal code is required.");
        if (string.IsNullOrWhiteSpace(model.Country)) ModelState.AddModelError("", "Country is required.");
        if (string.IsNullOrWhiteSpace(model.Phone)) ModelState.AddModelError("", "Business phone is required.");
        if (string.IsNullOrWhiteSpace(model.BusinessType)) ModelState.AddModelError("", "Business type is required.");

        var emailOwner = await _uow.AgentUsers.FirstOrDefaultAsync(u => u.Email == model.Email && u.Id != agent.Id);
        if (emailOwner != null)
        {
            ModelState.AddModelError("", "Another account already uses that email address.");
        }

        if (!ModelState.IsValid)
        {
            var package = agent.PackageId > 0 ? await _uow.BillingRules.GetByIdAsync(agent.PackageId) : null;
            model.Id = agent.Id;
            model.UserName = agent.UserName;
            model.DomainName = agent.DomainName;
            model.PackageName = package?.PackageName ?? "";
            LoadTimeZoneOptions();
            return View(model);
        }

        agent.FirstName = model.FirstName;
        agent.LastName = model.LastName;
        agent.Email = model.Email;
        agent.Designation = model.Designation ?? "";
        agent.CompanyName = model.CompanyName;
        agent.CompanyAddress = model.CompanyAddress ?? "";
        agent.City = model.City;
        agent.Province = model.Province;
        agent.PostalCode = model.PostalCode;
        agent.Country = model.Country;
        agent.TimeZone = model.TimeZone ?? "";
        agent.Phone = model.Phone;
        agent.BusinessFax = model.BusinessFax ?? "";
        agent.CellPhone = model.CellPhone ?? "";
        agent.BusinessType = model.BusinessType;
        agent.PromotionCode = model.PromotionCode ?? "";
        agent.DefaultPaymentLink = model.DefaultPaymentLink;

        await _agents.UpdateAsync(agent);
        await SignInAgentAsync(agent, new AuthenticationProperties
        {
            IsPersistent = false,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
        });

        TempData["Success"] = "Profile updated.";
        return RedirectToAction(nameof(Profile));
    }

    private static readonly HashSet<string> PortalAccentColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "#1457d9", "#d9541f", "#1f7a4d", "#4b5563", "#7a1f3d", "#5b2f9e"
    };

    [Authorize]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPortalAccentColor(string? color, string? returnUrl)
    {
        var agent = await GetCurrentAgentAsync();
        if (agent == null) return RedirectToAction(nameof(Login));

        if (!string.IsNullOrWhiteSpace(color) && PortalAccentColors.Contains(color))
        {
            agent.PortalAccentColor = color.ToLowerInvariant();
            await _agents.UpdateAsync(agent);
            await SignInAgentAsync(agent, new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });
        }

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        // Explicit path, not RedirectToAction("Index", "Dashboard") - that generates "/" because
        // Dashboard/Index are the default route's default values, and "/" on an agent's own
        // domain (temporary or custom) is reserved for their public website homepage, not the
        // portal. Bare "/" would silently strand them on their own marketing site after signing in.
        return Redirect("/Dashboard");
    }

    [Authorize]
    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> UploadPhoto(IFormFile? photo)
    {
        var agent = await GetCurrentAgentAsync();
        if (agent == null) return RedirectToAction(nameof(Login));

        if (photo == null || photo.Length == 0)
        {
            TempData["Error"] = "Choose a photo to upload.";
            return RedirectToAction(nameof(Profile));
        }
        if (photo.Length > 8 * 1024 * 1024)
        {
            TempData["Error"] = "Photos must be 8 MB or smaller.";
            return RedirectToAction(nameof(Profile));
        }

        var extension = Path.GetExtension(photo.FileName).ToLowerInvariant();
        var expectedContentType = extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => string.Empty
        };
        if (string.IsNullOrEmpty(expectedContentType) ||
            !string.Equals(photo.ContentType, expectedContentType, StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "Only JPG, JPEG, PNG, GIF, and WebP image files are allowed.";
            return RedirectToAction(nameof(Profile));
        }

        await using var stream = photo.OpenReadStream();
        if (!await HasValidImageSignatureAsync(stream, extension))
        {
            TempData["Error"] = "That file does not contain a valid supported image.";
            return RedirectToAction(nameof(Profile));
        }
        stream.Position = 0;

        var previousPhotoUrl = agent.PhotoUrl;
        var url = await _blob.UploadAsync(stream, photo.FileName, "agent-photos", expectedContentType, isPrivate: false);
        agent.PhotoUrl = url;
        await _agents.UpdateAsync(agent);

        if (!string.IsNullOrWhiteSpace(previousPhotoUrl))
        {
            try { await _blob.DeleteAsync(previousPhotoUrl); } catch { /* best effort */ }
        }

        TempData["Success"] = "Photo updated.";
        return RedirectToAction(nameof(Profile));
    }

    [Authorize]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RemovePhoto()
    {
        var agent = await GetCurrentAgentAsync();
        if (agent == null) return RedirectToAction(nameof(Login));

        if (!string.IsNullOrWhiteSpace(agent.PhotoUrl))
        {
            try { await _blob.DeleteAsync(agent.PhotoUrl); } catch { /* best effort */ }
            agent.PhotoUrl = null;
            await _agents.UpdateAsync(agent);
        }

        TempData["Success"] = "Photo removed.";
        return RedirectToAction(nameof(Profile));
    }

    private static async Task<bool> HasValidImageSignatureAsync(Stream stream, string extension)
    {
        var header = new byte[12];
        var read = await stream.ReadAsync(header.AsMemory(0, header.Length));
        if (read < 6) return false;
        return extension switch
        {
            ".jpg" or ".jpeg" => header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".png" => read >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".gif" => System.Text.Encoding.ASCII.GetString(header, 0, 6) is "GIF87a" or "GIF89a",
            ".webp" => read >= 12 && System.Text.Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(header, 8, 4) == "WEBP",
            _ => false
        };
    }

    [Authorize]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(string? currentPassword, string newPassword, string confirmPassword)
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(idValue, out var id)) return RedirectToAction(nameof(Login));

        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return RedirectToAction(nameof(Login));

        ViewBag.RequireCurrentPassword = !agent.MustChangePassword;

        // The forced first-login flow has no meaningful "current password" to check -- the
        // temporary one was just handed to the agent and exists only to get them here.
        // A voluntary change later must re-verify it, so a hijacked/unattended session can't
        // silently lock the real owner out.
        if (!agent.MustChangePassword)
        {
            if (string.IsNullOrWhiteSpace(currentPassword) ||
                await _agents.AuthenticateAsync(agent.UserName, currentPassword) == null)
            {
                ModelState.AddModelError("", "Current password is incorrect.");
            }
        }
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
            ModelState.AddModelError("", "New password must be at least 8 characters.");
        if (newPassword != confirmPassword)
            ModelState.AddModelError("", "Passwords do not match.");
        if (!ModelState.IsValid) return View();

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
        // Explicit path, not RedirectToAction("Index", "Dashboard") - that generates "/" because
        // Dashboard/Index are the default route's default values, and "/" on an agent's own
        // domain (temporary or custom) is reserved for their public website homepage, not the
        // portal. Bare "/" would silently strand them on their own marketing site after signing in.
        return Redirect("/Dashboard");
    }

    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Login");
    }
    public IActionResult AccessDenied() => View();

    private void SetRegistrationVerifyCode()
    {
        var code = RandomNumberGenerator.GetInt32(1000, 10000).ToString();
        HttpContext.Session.SetString(RegistrationVerifyCodeSessionKey, code);
        ViewBag.VerificationCode = code;
    }

    private async Task LoadActivePackagesAsync()
    {
        try
        {
            // Trial packages are invitation-only (see trialCode handling in Register) - never
            // shown in the normal self-serve dropdown.
            var packages = await _uow.BillingRules.FindAsync(p => p.IsActive && !p.IsTrialPackage);
            ViewBag.Packages = packages
                .OrderBy(GetPackageRank)
                .ThenBy(p => p.MonthlyPrice <= 0 ? decimal.MaxValue : p.MonthlyPrice)
                .ThenBy(p => p.PackageName)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to load active registration packages.");
            ViewBag.Packages = new List<BillingRule>();
        }

        LoadTimeZoneOptions();
    }

    private void LoadTimeZoneOptions()
    {
        ViewBag.TimeZones = AgentTimeZoneHelper.Options;
    }

    private static int GetPackageRank(BillingRule package) => package.PackageName switch
    {
        "IPro Silver" => 1,
        "IPro Gold" => 2,
        "IPro Platinum" => 3,
        "Broker Package" => 4,
        _ => 50
    };

    private async Task SignInAgentAsync(AgentUser user, AuthenticationProperties props)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Email, user.Email),
            new("FullName", $"{user.FirstName} {user.LastName}"),
            new("PackageId", user.PackageId.ToString()),
            new("MustChangePassword", user.MustChangePassword ? "true" : "false"),
            new("PortalAccentColor", user.PortalAccentColor ?? "")
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
        var candidate = $"{baseName}.247advisers.com";
        var suffix = 1;
        while (await _agents.DomainExistsAsync(candidate))
        {
            candidate = $"{baseName}{suffix}.247advisers.com";
            suffix++;
        }
        return candidate;
    }

    private static string NormalizeIdentifier(string value)
    {
        return Regex.Replace(value, "[^A-Za-z0-9]", "");
    }

    private static string GenerateTemporaryPassword() => EncryptionService.GenerateToken(12);

    private static RegistrationWelcomeModel BuildWelcomeModel(AgentUser model, string temporaryPassword) => new()
    {
        FullName = $"{model.FirstName} {model.LastName}".Trim(),
        Email = model.Email,
        UserName = model.UserName,
        TemporaryPassword = temporaryPassword,
        SetupDomain = model.DomainName
    };

    private static AgentUser ToAgentUser(AgentRegistrationViewModel model) => new()
    {
        FirstName = model.FirstName,
        LastName = model.LastName,
        Email = model.Email,
        Designation = model.Designation ?? "",
        CompanyName = model.CompanyName,
        CompanyAddress = model.CompanyAddress ?? "",
        City = model.City,
        Province = model.Province,
        PostalCode = model.PostalCode,
        Country = model.Country,
        TimeZone = model.TimeZone ?? "",
        Phone = model.Phone,
        BusinessFax = model.BusinessFax ?? "",
        CellPhone = model.CellPhone ?? "",
        BusinessType = model.BusinessType,
        PackageId = model.PackageId,
        PromotionCode = model.PromotionCode ?? "",
        IsActive = true
    };

    private async Task<AgentUser?> GetCurrentAgentAsync()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(idValue, out var id) ? await _agents.GetByIdAsync(id) : null;
    }

    private static AgentProfileViewModel ToProfileViewModel(AgentUser agent, string packageName) => new()
    {
        Id = agent.Id,
        UserName = agent.UserName,
        DomainName = agent.DomainName,
        PackageName = packageName,
        FirstName = agent.FirstName,
        LastName = agent.LastName,
        Email = agent.Email,
        Designation = agent.Designation,
        CompanyName = agent.CompanyName,
        CompanyAddress = agent.CompanyAddress,
        City = agent.City,
        Province = agent.Province,
        PostalCode = agent.PostalCode,
        Country = agent.Country,
        TimeZone = agent.TimeZone,
        Phone = agent.Phone,
        BusinessFax = agent.BusinessFax,
        CellPhone = agent.CellPhone,
        BusinessType = agent.BusinessType,
        PromotionCode = agent.PromotionCode,
        DefaultPaymentLink = agent.DefaultPaymentLink,
        PhotoUrl = agent.PhotoUrl
    };

    private static void NormalizeRegistration(AgentRegistrationViewModel model)
    {
        model.FirstName = model.FirstName?.Trim() ?? "";
        model.LastName = model.LastName?.Trim() ?? "";
        model.Email = (model.Email?.Trim() ?? "").ToLowerInvariant();
        model.Designation = model.Designation?.Trim() ?? "";
        model.CompanyName = model.CompanyName?.Trim() ?? "";
        model.CompanyAddress = model.CompanyAddress?.Trim() ?? "";
        model.City = model.City?.Trim() ?? "";
        model.Province = model.Province?.Trim() ?? "";
        model.PostalCode = model.PostalCode?.Trim() ?? "";
        model.Country = model.Country?.Trim() ?? "";
        model.TimeZone = AgentTimeZoneHelper.Normalize(model.TimeZone);
        model.Phone = model.Phone?.Trim() ?? "";
        model.BusinessFax = model.BusinessFax?.Trim() ?? "";
        model.CellPhone = model.CellPhone?.Trim() ?? "";
        model.BusinessType = model.BusinessType?.Trim() ?? "";
        model.PromotionCode = model.PromotionCode?.Trim() ?? "";
    }

    private static void NormalizeProfile(AgentProfileViewModel model)
    {
        model.FirstName = model.FirstName?.Trim() ?? "";
        model.LastName = model.LastName?.Trim() ?? "";
        model.Email = (model.Email?.Trim() ?? "").ToLowerInvariant();
        model.Designation = model.Designation?.Trim() ?? "";
        model.CompanyName = model.CompanyName?.Trim() ?? "";
        model.CompanyAddress = model.CompanyAddress?.Trim() ?? "";
        model.City = model.City?.Trim() ?? "";
        model.Province = model.Province?.Trim() ?? "";
        model.PostalCode = model.PostalCode?.Trim() ?? "";
        model.Country = model.Country?.Trim() ?? "";
        model.TimeZone = AgentTimeZoneHelper.Normalize(model.TimeZone);
        model.Phone = model.Phone?.Trim() ?? "";
        model.BusinessFax = model.BusinessFax?.Trim() ?? "";
        model.CellPhone = model.CellPhone?.Trim() ?? "";
        model.BusinessType = model.BusinessType?.Trim() ?? "";
        model.PromotionCode = model.PromotionCode?.Trim() ?? "";
        model.DefaultPaymentLink = NormalizePaymentLink(model.DefaultPaymentLink);
    }

    private static string? NormalizePaymentLink(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.Contains("://") ? value : $"https://{value}";
    }
}
