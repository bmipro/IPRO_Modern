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
using Microsoft.AspNetCore.RateLimiting;

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
    private readonly Microsoft.AspNetCore.Identity.IPasswordHasher<TeamMember> _teamHasher;
    private readonly ISignInLockNotices _lockNotices;
    private readonly TwoStepGate _twoStep;

    public AccountController(IAgentService agents, IEmailService email, IUnitOfWork uow, IBillingService billing, IPRODbContext db, IPackageEntitlementService entitlements, IBlobStorageService blob, ILogger<AccountController> logger, IConfiguration configuration, IServiceScopeFactory scopeFactory, Microsoft.AspNetCore.Identity.IPasswordHasher<TeamMember> teamHasher, ISignInLockNotices lockNotices, TwoStepGate twoStep)
    {
        _twoStep = twoStep;
        _teamHasher = teamHasher;
        _lockNotices = lockNotices;
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
        // 617: someone already signed in has no use for the sign-in page, and a form from it would be refused.
        if (User?.Identity?.IsAuthenticated == true) return Redirect("/portal/Dashboard");
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [EnableRateLimiting(EndpointRateLimits.SignIn)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string username, string password, bool rememberMe = false, string? returnUrl = null)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        // 583: the name finds an agent (username or email) and, on the same form, a team member (a
        // secretary/assistant login working inside one agent's account, #379); SignInLockout counts the
        // wrong passwords against whichever it found, or against the name itself when it is no account.
        var user = await _agents.FindForSignInAsync(username);
        var member = await FindTeamMemberForSignInAsync(username);
        var candidates = new List<SignInCandidate>();
        if (user != null) candidates.Add(new SignInCandidate(new SignInAccount(SignInAccountKind.Adviser, user.Id), () => _agents.PasswordMatches(user, password)));
        if (member != null) candidates.Add(new SignInCandidate(new SignInAccount(SignInAccountKind.TeamMember, member.Id), () => TeamPasswordMatches(member, password)));
        var attempt = await SignInLockout.TryAsync(_db, SignInDoor.Portal, username, candidates, DateTime.UtcNow);
        NoteLocks(attempt, SignInDoor.Portal);

        var agentSignsIn = user != null && attempt.Matched.Contains(new SignInAccount(SignInAccountKind.Adviser, user.Id));
        if (!agentSignsIn && member != null && attempt.Matched.Contains(new SignInAccount(SignInAccountKind.TeamMember, member.Id)))
        {
            return await SignInTeamMemberAndRedirectAsync(member, rememberMe, returnUrl);
        }
        if (!agentSignsIn || user == null)
        {
            // A name that is no account costs no password check, and a locked one none either, so a
            // refusal would come back faster for them than for a real account's wrong password and tell
            // which names exist. Every refusal is answered 1.5 seconds after the request began
            // (SignInLockout.PadAsync), whatever it took to decide; the sentence is the same for all.
            await SignInLockout.PadAsync(started);
            ModelState.AddModelError("", attempt.Locked ? SignInMessages.PortalLocked : "Invalid username or password."); return View();
        }
        // 593: with two-step sign-in on, the right password is half of it: on to the code page, signed in
        // to nothing yet (unless this browser is remembered for the account).
        var twoStep = await _twoStep.AfterPasswordAsync(HttpContext, TwoStepAccount.Adviser(user.Id), user.PasswordHash, rememberMe, returnUrl);
        if (twoStep.Next != null) return twoStep.Next;
        var props = PortalSignIn.Session(rememberMe);
        await SignInAgentAsync(user, props, twoStep.Stamp);
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
        return Redirect("/portal/Dashboard");
    }

    [HttpGet]
    public IActionResult ForgotPassword() => View();

    [EnableRateLimiting(EndpointRateLimits.PasswordReset)]
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

    [EnableRateLimiting(EndpointRateLimits.PasswordReset)]
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

        var wasLocked = SignInLockout.IsLocked(agent.SignInLockedUntil, DateTime.UtcNow);
        var succeeded = await _agents.ResetPasswordByTokenAsync(token, newPassword);
        if (!succeeded)
        {
            ViewBag.TokenValid = false;
            return View(new ResetPasswordViewModel { Token = token });
        }
        // 583: the reset unlocked sign-in (AgentService.ResetPasswordByTokenAsync); the lock was logged, so is this.
        if (wasLocked) SignInLockout.LogCleared(_logger, new SignInAccount(SignInAccountKind.Adviser, agent.Id), "forgot-password reset");

        var user = await _agents.GetByIdAsync(agent.Id);
        if (user != null)
        {
            // 593: the reset proves the inbox, not the second step: with two-step sign-in on, the code
            // page comes next (a remembered device is not, since its password fingerprint is the old one).
            var twoStep = await _twoStep.AfterPasswordAsync(HttpContext, TwoStepAccount.Adviser(user.Id), user.PasswordHash, false, null);
            if (twoStep.Next != null)
            {
                TempData["TwoStepNotice"] = "Your new password is saved. Type your code to finish signing in.";
                return twoStep.Next;
            }
            var props = new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            };
            await SignInAgentAsync(user, props, twoStep.Stamp);
        }
        // Explicit path, not RedirectToAction("Index", "Dashboard") - that generates "/" because
        // Dashboard/Index are the default route's default values, and "/" on an agent's own
        // domain (temporary or custom) is reserved for their public website homepage, not the
        // portal. Bare "/" would silently strand them on their own marketing site after signing in.
        return Redirect("/portal/Dashboard");
    }

    [HttpGet]
    public async Task<IActionResult> Register(string? trialCode = null, string? firstName = null, string? lastName = null, string? companyName = null, string? businessType = null, [FromQuery(Name = "package")] string? packageName = null, [FromQuery(Name = "ref")] string? referralCode = null)
    {
        SetRegistrationVerifyCode();
        // 532: a Refer a Friend link fills the promotion code (one code per sign-up) and the page leads with the gift.
        // A code that is paused, closed or switched off is simply not filled: the friend signs up without the gift.
        var referralGift = await ReferralProgram.FindUsableAsync(_db, referralCode);
        ViewBag.ReferralGift = referralGift;
        await LoadActivePackagesAsync();
        // 512: the registration page is one of the four public pages whose visits are counted.
        await PlatformVisitRecorder.RecordAsync(HttpContext, _db, "/Account/Register");

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
        //
        // When ?package= resolves, the form shows a locked plan summary instead of the dropdown --
        // the visitor already chose on the pricing card, and re-asking was one of the three
        // package-picks the old funnel forced (signup v2, 2026-08-13).
        var prefillPackageId = 0;
        if (!string.IsNullOrWhiteSpace(packageName) && ViewBag.Packages is IEnumerable<BillingRule> loadedPackages)
        {
            var selected = loadedPackages.FirstOrDefault(p => p.PackageName == packageName);
            prefillPackageId = selected?.Id ?? 0;
            ViewBag.SelectedPackage = selected;
        }

        return View(new AgentRegistrationViewModel
        {
            TrialCode = trialCode,
            FirstName = firstName ?? string.Empty,
            LastName = lastName ?? string.Empty,
            CompanyName = companyName ?? string.Empty,
            BusinessType = businessType ?? string.Empty,
            PackageId = prefillPackageId,
            PlanLocked = prefillPackageId > 0,
            PromotionCode = referralGift?.Code
        });
    }

    [EnableRateLimiting(EndpointRateLimits.Registration)]
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
        // Signup v2: the registrant chooses their own password here, which replaced the
        // temp-password ceremony entirely for self-signup. Same minimum as ChangePassword.
        if (string.IsNullOrWhiteSpace(model.Password) || model.Password.Length < 8)
        {
            ModelState.AddModelError("", "Choose a password of at least 8 characters.");
        }
        else if (!string.Equals(model.Password, model.ConfirmPassword, StringComparison.Ordinal))
        {
            ModelState.AddModelError("", "The two passwords do not match.");
        }
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
        if (string.IsNullOrWhiteSpace(expectedVerificationCode))
        {
            // SIGNUP-VERIFY (2026-08-18, fixed 2026-08-20): the expected code lives in session
            // (30-minute idle timeout). When it expires this used to fall into the SAME branch as
            // a wrong code, telling the user "Verify code is incorrect" while they typed exactly
            // the digits on screen -- an unwinnable loop that then ran into the 5-per-hour rate
            // limit. The re-render below regenerates the code, so with an honest message the very
            // next attempt succeeds.
            ModelState.AddModelError("", "Your session timed out, so the form was refreshed with a NEW verify code. Please enter the code now shown and submit again.");
        }
        else if (!string.Equals(verificationCode?.Trim(), expectedVerificationCode, StringComparison.Ordinal))
        {
            ModelState.AddModelError("", "Verify code is incorrect.");
        }
        if (!acceptTerms) ModelState.AddModelError("", "You must accept the terms and conditions.");
        if (!string.IsNullOrWhiteSpace(model.PromotionCode) && model.PackageId > 0)
        {
            var promo = await _billing.ValidatePromotionCodeAsync(model.PromotionCode, model.PackageId);
            if (promo == null)
            {
                // 532: one code per sign-up -- a Refer a Friend code is accepted in the same field.
                if (await ReferralProgram.FindUsableAsync(_db, model.PromotionCode) == null)
                {
                    ModelState.AddModelError("", "That promotion code is not valid for the selected package, or has expired/reached its redemption limit.");
                }
            }
            else
            {
                // 508: a code for one billing period. Checkout would quietly drop it for the other period;
                // here the customer is still choosing, so say which period the code needs.
                var chosenPeriod = string.Equals(model.BillingPeriodChoice, "Annually", StringComparison.OrdinalIgnoreCase)
                    ? BillingPeriod.Annually
                    : BillingPeriod.Monthly;
                var periodLimit = await PromotionCodePeriod.LimitAsync(_db, promo.Id);
                if (periodLimit.HasValue && periodLimit != chosenPeriod)
                {
                    ModelState.AddModelError("", PromotionCodeText.WrongPeriod(periodLimit.Value));
                }
            }
        }
        if (!ModelState.IsValid)
        {
            return await RerenderRegisterAsync(model);
        }
        if (await _agents.EmailExistsAsync(model.Email))
        {
            ModelState.AddModelError("", "An account already exists for this email address.");
            return await RerenderRegisterAsync(model);
        }

        // Claim the trial slot BEFORE creating the account (audit #2, A2-H4). The old order
        // created the agent first and then discovered the cap was full, at which point the only
        // honest option left was recording an over-redemption. Claiming first turns a full code
        // into a clean rejection: the conditional WHERE makes the database the arbiter under
        // concurrency, and the catch below releases the slot if registration itself fails.
        if (trialInvite != null)
        {
            var claimed = await _db.TrialInviteCodes
                .Where(c => c.Id == trialInvite.Id && (c.MaxRedemptions == null || c.RedemptionCount < c.MaxRedemptions))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.RedemptionCount, c => c.RedemptionCount + 1));
            if (claimed == 0)
            {
                ModelState.AddModelError("", "This trial invitation has reached its redemption limit. Please contact us for a new invitation.");
                return await RerenderRegisterAsync(model);
            }
        }

        var agent = ToAgentUser(model);
        agent.UserName = await GenerateUniqueUserNameAsync(agent.FirstName, agent.LastName);
        agent.DomainName = await GenerateUniqueDomainAsync(agent.UserName);
        agent.TermsAcceptedAt = DateTime.UtcNow;
        agent.RegistrationIpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
        // Signup v2: they just chose this password themselves, so there is nothing to force-change
        // and no temporary password anywhere in the flow. (Admin-created accounts keep theirs.)
        agent.MustChangePassword = false;
        if (trialInvite != null && submittedPackage != null)
        {
            agent.TrialEndsAt = DateTime.UtcNow.AddDays(submittedPackage.TrialDurationDays ?? 14);
        }
        try
        {
            await _agents.RegisterAsync(agent, model.Password);
        }
        catch (Exception ex)
        {
            if (trialInvite != null)
            {
                // The account was never created, so release the slot claimed above.
                await _db.TrialInviteCodes
                    .Where(c => c.Id == trialInvite.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.RedemptionCount, c => c.RedemptionCount - 1));
            }
            _logger.LogError(ex, "Registration failed for {Email}", model.Email);
            ModelState.AddModelError("", "We could not complete the registration. Please check the form and try again.");
            return await RerenderRegisterAsync(model);
        }

        // 512: where this sign-up came from -- the most recent counted visit by the same hashed
        // visitor within the month. Never fails a registration.
        await PlatformVisitRecorder.RecordSignupAsync(HttpContext, _db, agent.Id);

        // 532: a friend who signed up with a Refer a Friend code joins the referrer's ledger. Never fails a sign-up.
        try
        {
            await ReferralProgram.RecordSignupAsync(_db, agent, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record the Refer a Friend referral for new agent {AgentId}", agent.Id);
        }

        if (trialInvite != null)
        {
            await _uow.TrialInviteCodeRedemptions.AddAsync(new TrialInviteCodeRedemption
            {
                TrialInviteCodeId = trialInvite.Id,
                AgentUserId = agent.Id,
                RedeemedAt = DateTime.UtcNow
            });
            await _uow.SaveChangesAsync();
        }

        // 496: the adviser's site is written AND published now, not when they find the Publish button.
        // The home page, both landing pages and the email below all say the site is there from day
        // one, and the email's main button opens it. Safe before payment: an unpaid account's public
        // site answers 404 until billing is active. Its own scope and context, so nothing it tracks
        // leaks into this request; and a failure here must never cost a sign-up -- the adviser can
        // still press Publish under My Website, which runs the same code.
        try
        {
            using var provisioningScope = _scopeFactory.CreateScope();
            var provisioningServices = provisioningScope.ServiceProvider;
            await IPRO.Web.Infrastructure.WebsiteProvisioning.EnsurePublishedAsync(
                provisioningServices.GetRequiredService<IPRODbContext>(),
                provisioningServices.GetRequiredService<IPRO.Business.Interfaces.IWebsiteService>(),
                agent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not provision the website for new agent {AgentId}; it can still be published from My Website.", agent.Id);
        }

        // Welcome email carries NO credentials (they chose their password on the form; C-1 stays
        // honoured because nothing secret ever travels by email).
        var welcome = BuildWelcomeModel(agent, string.Empty);
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

        // 551: iPro hears about the sign-up -- before PayPal, so a registrant who never pays is still news.
        await SendSignupNoticeAsync(agent, submittedPackage, model, trialInvite);

        HttpContext.Session.Remove(RegistrationVerifyCodeSessionKey);

        // Signup v2 (2026-08-13): registration is the first half of CHECKOUT, not a destination.
        // The old flow ended on a receipt-style success page before any money changed hands, and
        // the customer had to sign in, change a temp password, and pick their package a third time
        // on /Billing before PayPal ever appeared. Now: sign them in and go straight to payment.
        await SignInAgentAsync(agent, new AuthenticationProperties { IsPersistent = false });
        // 522 (2026-09-26): that sign-in IS the adviser's first login. Only the Login page wrote
        // LastLoginAt, so the SuperAdmin page read "Last Login: Never" for the first real customer
        // the morning after he had been in the portal.
        await _agents.UpdateLastLoginAsync(agent.Id);

        if (trialInvite != null)
        {
            TempData["Success"] = $"Welcome, {agent.FirstName}! Your free trial is active — this is your dashboard.";
            return Redirect("/portal/Dashboard");
        }

        var period = string.Equals(model.BillingPeriodChoice, "Annually", StringComparison.OrdinalIgnoreCase)
            ? BillingPeriod.Annually
            : BillingPeriod.Monthly;
        // Session-host-aware (WEB-H-1). This POST arrives on whichever host the prospect signed up
        // from — the templates deliberately sell signup on the agent's own domain — and the cookie
        // SignInAgentAsync just issued is host-only. A canonical return URL here logged the buyer
        // out between PayPal approval and capture: money moved, nothing activated. The URL producer
        // lives in PortalUrlHelper; this used to be a hand-rolled copy of BillingController's.
        var checkout = await _billing.CreateSubscriptionAsync(
            agent.Id,
            model.PackageId,
            period,
            await PortalUrlHelper.BuildBillingActionUrlAsync(Request, _configuration, _db, "PayPalReturn", _logger),
            await PortalUrlHelper.BuildBillingActionUrlAsync(Request, _configuration, _db, "Cancel", _logger));

        if (checkout.Success && checkout.RequiresPayment && !string.IsNullOrWhiteSpace(checkout.ApprovalUrl))
        {
            return Redirect(checkout.ApprovalUrl);
        }
        if (checkout.Success)
        {
            // Fully-comped promotion: no PayPal step exists, the subscription is already active.
            TempData["Success"] = checkout.Message;
            return Redirect("/portal/Dashboard");
        }

        // Payment could not start (e.g. PayPal unreachable). The account exists and they are
        // signed in; Billing is the recovery surface and shows exactly one step left.
        _logger.LogWarning("Post-registration checkout could not start for agent {AgentId}: {Message}", agent.Id, checkout.Message);
        TempData["Error"] = "Your account was created, but the payment step could not start. Pick your plan below to finish — you will not be charged twice.";
        return Redirect("/Billing");
    }

    // 551: the owner's "someone has registered" email (SignupNotice). Never fails the registration: the
    // send swallows its own failures, and the origin lookup is guarded the same way.
    private async Task SendSignupNoticeAsync(AgentUser agent, BillingRule? package, AgentRegistrationViewModel model, TrialInviteCode? trialInvite)
    {
        try
        {
            var origin = await _db.PlatformSignupOrigins.AsNoTracking().FirstOrDefaultAsync(o => o.AgentUserId == agent.Id);
            // 532: a Refer a Friend code says whose gift it is.
            var codeText = agent.PromotionCode;
            var referral = await _db.Referrals.AsNoTracking().FirstOrDefaultAsync(r => r.FriendAgentUserId == agent.Id);
            if (referral != null)
            {
                var referrer = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == referral.AgentUserId);
                codeText = $"{referral.Code} (Refer a Friend: a gift from {referrer?.FirstName} {referrer?.LastName})".Replace(" )", ")");
            }
            var annual = string.Equals(model.BillingPeriodChoice, "Annually", StringComparison.OrdinalIgnoreCase);
            var now = DateTime.UtcNow;
            await SignupNotice.SendAsync(_email, _configuration, _logger, SignupNotice.ForRegistration(new SignupNotice.Registration(
                agent.Id, agent.FirstName, agent.LastName, agent.CompanyName, agent.BusinessType,
                agent.Email, agent.Phone, agent.City, agent.Province,
                package?.PackageName ?? "(unknown package)", annual ? "Annually" : "Monthly",
                package == null ? 0m : (annual ? package.AnnualPrice : package.MonthlyPrice),
                package?.EffectiveSetupFee(now) ?? 0m, codeText,
                trialInvite?.Code, agent.TrialEndsAt, SignupNotice.CameFrom(origin), now), _configuration));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sign-up notice could not be prepared for agent {AgentId}", agent.Id);
        }
    }

    // One place to rebuild everything the Register view needs when validation sends the form back.
    private async Task<IActionResult> RerenderRegisterAsync(AgentRegistrationViewModel model)
    {
        SetRegistrationVerifyCode();
        ViewBag.ReferralGift = await ReferralProgram.FindUsableAsync(_db, model.PromotionCode);
        await LoadActivePackagesAsync();
        await RepopulateTrialViewBagAsync(model.TrialCode);
        if (model.PlanLocked && model.PackageId > 0 && ViewBag.Packages is IEnumerable<BillingRule> loaded)
        {
            // Keep the locked plan summary through validation-failure re-renders.
            ViewBag.SelectedPackage = loaded.FirstOrDefault(p => p.Id == model.PackageId);
        }
        return View("Register", model);
    }

    private async Task<(TrialInviteCode? Invite, BillingRule? Package, string? Error)> ResolveTrialInviteAsync(string? trialCode)
    {
        if (string.IsNullOrWhiteSpace(trialCode)) return (null, null, "An invitation code is required for this package.");

        var code = trialCode.Trim();
        var invite = await _uow.TrialInviteCodes.FirstOrDefaultAsync(c => c.Code == code);
        if (invite == null) return (null, null, "This invitation link is not valid.");
        if (!invite.IsActive) return (null, null, "This invitation is no longer active.");
        // 518: the Expires date is the last day the invitation works, in the platform's zone.
        if (PlatformDay.HasExpired(invite.ExpiresAt, DateTime.UtcNow, _configuration["Admin:TimeZone"])) return (null, null, "This invitation has expired.");
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

    // Anonymous by necessity -- the account does not exist yet at this point in signup -- but NOT
    // callable by anything that has not loaded a real Register page. Previously this was
    // [IgnoreAntiforgeryToken], so anyone on the internet could POST codes at it in bulk and read
    // the exact discount terms back: a promo-code oracle (WEB-H-2, open since the 2026-08-14
    // ultra-audit). Antiforgery costs the legitimate visitor nothing (the token is already on the
    // page they are standing on) while removing the "from anywhere, with no session" property.
    // Paired with a limit of 5 in 5 minutes per visitor on the action itself (EndpointRateLimits, 582).
    [EnableRateLimiting(EndpointRateLimits.PromoCode)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ValidatePromoCode(string code, int packageId, string? period = null)
    {
        var package = await _uow.BillingRules.FirstOrDefaultAsync(p => p.Id == packageId && p.IsActive);
        if (package == null)
        {
            return Json(new { valid = false, message = "Choose a valid package first." });
        }

        var promo = await _billing.ValidatePromotionCodeAsync(code, packageId);
        if (promo == null)
        {
            // 532: a Refer a Friend code answers with the gift it brings on this plan.
            var referral = await ReferralProgram.FindUsableAsync(_db, code);
            if (referral != null)
            {
                var referralPeriod = PromotionCodePeriod.Parse(period) ?? BillingPeriod.Monthly;
                return Json(new
                {
                    valid = true,
                    message = ReferralProgram.DescribeGift(referral, package.PackageName, referralPeriod, package.EffectiveSetupFee(DateTime.UtcNow),
                        referralPeriod == BillingPeriod.Annually ? package.AnnualPrice : package.MonthlyPrice)
                });
            }
            return Json(new { valid = false, message = "That code is not valid for the selected package, or has expired/reached its redemption limit." });
        }

        // 508: the page sends the billing period the customer has chosen. A code for the other period
        // is answered with the period it needs; an accepted one says which billing it works with.
        var periodLimit = await PromotionCodePeriod.LimitAsync(_db, promo.Id);
        var chosenPeriod = PromotionCodePeriod.Parse(period);
        if (periodLimit.HasValue && chosenPeriod.HasValue && periodLimit != chosenPeriod)
        {
            return Json(new { valid = false, message = PromotionCodeText.WrongPeriod(periodLimit.Value) });
        }

        return Json(new { valid = true, message = PromotionCodeText.Accepted(promo, periodLimit) });
    }

    // RegisterSuccess (the v1 signup receipt) was DELETED 2026-08-29 by the front-door truth
    // sweep. It was orphaned -- nothing redirected to it since signup v2 (2026-08-13) -- and on
    // its only reachable path (a direct GET) it fabricated Sample() data, claimed a temporary-
    // password ceremony v2 removed, and ALWAYS showed a false "confirmation email could not be
    // sent" warning. The welcome model/template classes remain: they build the real welcome
    // email for admin-created agents.

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

        // 532: the Refer a Friend card, just above Calendar Source (the owner's spot).
        ViewBag.ReferralSummary = await ReferralProgram.SummaryAsync(_db, agent.Id);

        var profile = ToProfileViewModel(agent, package?.PackageName ?? "");
        profile.FollowUpReminderEmails = await FollowUpReminderPreference.IsEnabledAsync(_db, agent.Id);
        await LoadEmailChangeStateAsync(agent.Id);
        return View(profile);
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

        // 593 (review): the login email is where "Forgot password?" and emailed two-step codes go, so a new
        // one takes the current password -- and a code, when two-step sign-in is on -- and only the adviser
        // may set it: a team member, or anyone holding a session left open, could otherwise move the
        // account's keys to their own inbox. Checked last, so a form sent back for another reason costs no try.
        var oldEmail = (agent.Email ?? string.Empty).Trim();
        var emailChanging = !string.Equals(model.Email, oldEmail, StringComparison.OrdinalIgnoreCase);
        if (emailChanging && ModelState.IsValid)
        {
            var proofError = await EmailChangeProofErrorAsync(agent, model.CurrentPassword, model.TwoStepCode);
            if (proofError != null) ModelState.AddModelError("", proofError);
        }

        if (!ModelState.IsValid)
        {
            var package = agent.PackageId > 0 ? await _uow.BillingRules.GetByIdAsync(agent.PackageId) : null;
            model.Id = agent.Id;
            model.UserName = agent.UserName;
            model.DomainName = agent.DomainName;
            model.PackageName = package?.PackageName ?? "";
            model.CurrentPassword = null;
            model.TwoStepCode = null;
            LoadTimeZoneOptions();
            await LoadEmailChangeStateAsync(agent.Id);
            return View(model);
        }

        agent.FirstName = model.FirstName;
        agent.LastName = model.LastName;
        agent.Email = model.Email;
        agent.Designation = model.Designation ?? "";
        agent.CompanyName = model.CompanyName;
        agent.CompanyAddress = model.CompanyAddress ?? "";
        agent.TaxRegistrationNumber = model.TaxRegistrationNumber ?? "";   // 570
        agent.City = model.City;
        agent.Province = model.Province;
        agent.PostalCode = model.PostalCode;
        agent.Country = model.Country;
        agent.TimeZone = model.TimeZone ?? "";
        agent.Phone = model.Phone;
        agent.BusinessFax = model.BusinessFax ?? "";
        agent.CellPhone = model.CellPhone ?? "";
        agent.BusinessType = model.BusinessType;
        // 492 (audit BILLING-12): the promotion code is recorded at sign-up and shown read-only here;
        // an agent may not rewrite it from the profile form.
        // 527 (2026-09-28): the payment link moved to the Payments page (a method per service); the
        // profile form no longer carries it, and the old value stays as the invoices' fallback until
        // that page is saved once.

        await _agents.UpdateAsync(agent);
        // 498: the morning follow-ups email's switch lives in its own table, not on the adviser's row.
        await FollowUpReminderPreference.SetAsync(_db, agent.Id, model.FollowUpReminderEmails);
        await RefreshSessionAsync(agent);
        if (emailChanging)
        {
            _logger.LogWarning("Login email changed for adviser {AgentId}", agent.Id);
            _twoStep.NoticeEmailChanged(TwoStepAccount.Adviser(agent.Id), oldEmail);
        }

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
            await RefreshSessionAsync(agent);
        }

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        // Explicit path, not RedirectToAction("Index", "Dashboard") - that generates "/" because
        // Dashboard/Index are the default route's default values, and "/" on an agent's own
        // domain (temporary or custom) is reserved for their public website homepage, not the
        // portal. Bare "/" would silently strand them on their own marketing site after signing in.
        return Redirect("/portal/Dashboard");
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

        // 585: judged by what it is, not by its name: a PNG called me.jpg is a PNG (UploadedPicture).
        await using var stream = photo.OpenReadStream();
        var header = await IPRO.Utility.UploadedPicture.HeaderAsync(stream);
        var picture = IPRO.Utility.UploadedPicture.Of(header);
        if (picture == null)
        {
            TempData["Error"] = IPRO.Utility.UploadedPicture.Refusal(header);
            return RedirectToAction(nameof(Profile));
        }

        var previousPhotoUrl = agent.PhotoUrl;
        var url = await _blob.UploadAsync(stream, IPRO.Utility.UploadedPicture.StoredName(photo.FileName, picture), "agent-photos", picture.ContentType, isPrivate: false);
        agent.PhotoUrl = url;
        await _agents.UpdateAsync(agent);

        // The old photo may still be embedded in newsletter footers already composed or delivered
        // (A5-H14's shape): keep the file whenever anything still references it.
        if (!string.IsNullOrWhiteSpace(previousPhotoUrl) &&
            !await IPRO.DataAccess.BlobReferences.IsReferencedAsync(_db, previousPhotoUrl))
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
            // Row first, file second — the old order deleted the file before the save, so a failed
            // save left the profile pointing at a destroyed photo. And the file only goes at all
            // when nothing else (newsletter footers, most likely) still references it.
            var removedPhotoUrl = agent.PhotoUrl;
            agent.PhotoUrl = null;
            await _agents.UpdateAsync(agent);
            if (!await IPRO.DataAccess.BlobReferences.IsReferencedAsync(_db, removedPhotoUrl))
            {
                try { await _blob.DeleteAsync(removedPhotoUrl); } catch { /* best effort */ }
            }
        }

        TempData["Success"] = "Photo removed.";
        return RedirectToAction(nameof(Profile));
    }


    [Authorize]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(string? currentPassword, string newPassword, string confirmPassword)
    {
        // A team member changes THEIR password, never the agent's -- NameIdentifier here is the
        // owning agent's id, so without this branch a staff login would overwrite the owner's
        // credentials.
        if (int.TryParse(User.FindFirstValue("TeamMemberId"), out var teamMemberId))
        {
            return await ChangeTeamMemberPasswordAsync(teamMemberId, currentPassword, newPassword, confirmPassword);
        }

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
            // 583: the current password is checked like a sign-in and counts the same way: a session
            // left open must not be a way to try passwords without limit.
            var check = string.IsNullOrWhiteSpace(currentPassword) ? null
                : await SignInLockout.TryAsync(_db, SignInDoor.Portal, agent.UserName,
                    new[] { new SignInCandidate(new SignInAccount(SignInAccountKind.Adviser, agent.Id), () => _agents.PasswordMatches(agent, currentPassword!)) },
                    DateTime.UtcNow);
            if (check != null) NoteLocks(check, SignInDoor.Portal);
            if (check == null || !check.Succeeded)
            {
                ModelState.AddModelError("", check?.Locked == true ? SignInMessages.ChangePasswordLocked : "Current password is incorrect.");
            }
        }
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
            ModelState.AddModelError("", "New password must be at least 8 characters.");
        if (newPassword != confirmPassword)
            ModelState.AddModelError("", "Passwords do not match.");
        if (!ModelState.IsValid) return View();

        var wasLocked = SignInLockout.IsLocked(agent.SignInLockedUntil, DateTime.UtcNow);
        await _agents.ChangePasswordAsync(id, newPassword);   // 583: unlocks sign-in too
        if (wasLocked) SignInLockout.LogCleared(_logger, new SignInAccount(SignInAccountKind.Adviser, id), "new password");
        var user = await _agents.GetByIdAsync(id);
        if (user != null)
        {
            await SignInAgentAsync(user, new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            }, CurrentTwoStepStamp);
        }
        // Explicit path, not RedirectToAction("Index", "Dashboard") - that generates "/" because
        // Dashboard/Index are the default route's default values, and "/" on an agent's own
        // domain (temporary or custom) is reserved for their public website homepage, not the
        // portal. Bare "/" would silently strand them on their own marketing site after signing in.
        return Redirect("/portal/Dashboard");
    }

    private async Task<IActionResult> ChangeTeamMemberPasswordAsync(int teamMemberId, string? currentPassword, string newPassword, string confirmPassword)
    {
        var member = await _uow.TeamMembers.GetByIdAsync(teamMemberId);
        if (member == null || !member.IsActive) return RedirectToAction(nameof(Login));

        ViewBag.RequireCurrentPassword = !member.MustChangePassword;
        if (!member.MustChangePassword)
        {
            // 583: counted like a sign-in, as the agent's own Change Password is.
            var check = string.IsNullOrWhiteSpace(currentPassword) ? null
                : await SignInLockout.TryAsync(_db, SignInDoor.Portal, member.Email,
                    new[] { new SignInCandidate(new SignInAccount(SignInAccountKind.TeamMember, member.Id), () => TeamPasswordMatches(member, currentPassword)) },
                    DateTime.UtcNow);
            if (check != null) NoteLocks(check, SignInDoor.Portal);
            if (check == null || !check.Succeeded)
            {
                ModelState.AddModelError("", check?.Locked == true ? SignInMessages.ChangePasswordLocked : "Current password is incorrect.");
            }
        }
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
            ModelState.AddModelError("", "New password must be at least 8 characters.");
        if (newPassword != confirmPassword)
            ModelState.AddModelError("", "Passwords do not match.");
        if (!ModelState.IsValid) return View();

        member.PasswordHash = _teamHasher.HashPassword(member, newPassword);
        member.MustChangePassword = false;
        // Tracked (GetByIdAsync): these columns only. Update() wrote the whole row (593: a request that read
        // it before a password change could write the old hash back).
        await _uow.SaveChangesAsync();
        // 583: a new password unlocks sign-in at once.
        if (await SignInLockout.ClearAsync(_db, new SignInAccount(SignInAccountKind.TeamMember, member.Id), DateTime.UtcNow))
            SignInLockout.LogCleared(_logger, new SignInAccount(SignInAccountKind.TeamMember, member.Id), "new password");

        var owner = await _uow.AgentUsers.GetByIdAsync(member.AgentUserId);
        if (owner != null)
        {
            await SignInTeamMemberAsync(member, owner, new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            }, CurrentTwoStepStamp);
        }
        return Redirect("/portal/Dashboard");
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
            // shown in the normal self-serve dropdown. Hidden test packages (QA daily-billing
            // sandbox plans) are reachable only by a direct billingRuleId POST, never rendered here.
            var packages = await _uow.BillingRules.FindAsync(p => p.IsActive && !p.IsTrialPackage && !p.IsHiddenTestPackage);
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

    // A team member authenticates with their OWN credentials but acts AS the owning agent:
    // ClaimTypes.NameIdentifier is the agent's id, so every controller in the portal works
    // unchanged. The TeamMemberId marker claim is what keeps Billing and Team management
    // owner-only (middleware in Program.cs) and routes ChangePassword at the member's own row.
    // An active team member of an active agent, by the email typed (583: found before any password is
    // checked, so the sign-in lock can count). A member of a deactivated agent is no account here.
    private async Task<TeamMember?> FindTeamMemberForSignInAsync(string? username)
    {
        var email = (username ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email)) return null;
        var member = await _db.TeamMembers.Include(t => t.AgentUser)
            .FirstOrDefaultAsync(t => t.Email == email && t.IsActive && t.AgentUser.IsActive);
        return member != null && SignInLockout.SameName(member.Email, email) ? member : null;
    }

    private bool TeamPasswordMatches(TeamMember member, string? password) =>
        !string.IsNullOrEmpty(password)
        && _teamHasher.VerifyHashedPassword(member, member.PasswordHash, password)
            != Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed;

    // 583: a lock this attempt set is logged at Warning, and its holder gets the one email.
    private void NoteLocks(SignInAttempt attempt, SignInDoor door)
    {
        SignInLockout.LogLocks(_logger, attempt, door, RateLimitVisitor.Key(HttpContext));
        foreach (var account in attempt.LockedNow) _lockNotices.Queue(account);
    }

    private async Task<IActionResult> SignInTeamMemberAndRedirectAsync(TeamMember member, bool rememberMe, string? returnUrl)
    {
        var owner = member.AgentUser;

        // 593: a team member's own two-step sign-in, exactly as the adviser's (TwoStepController finishes it).
        var twoStep = await _twoStep.AfterPasswordAsync(HttpContext, TwoStepAccount.Member(member.AgentUserId, member.Id), member.PasswordHash, rememberMe, returnUrl);
        if (twoStep.Next != null) return twoStep.Next;

        // Tracked since FindTeamMemberForSignInAsync: only this column is written (an Update() of the
        // member would write its owner's whole row back too, through the navigation).
        member.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var props = PortalSignIn.Session(rememberMe);
        await SignInTeamMemberAsync(member, owner, props, twoStep.Stamp);

        if (member.MustChangePassword) return RedirectToAction(nameof(ChangePassword));
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return Redirect("/portal/Dashboard");
    }

    // The claims are PortalSignIn's (593: TwoStepController makes the same cookie). `twoStepStamp`: the
    // session passed two-step sign-in, or comes from a session that had (TwoStepGate.ClaimType).
    private Task SignInTeamMemberAsync(TeamMember member, AgentUser owner, AuthenticationProperties props, string? twoStepStamp = null) =>
        PortalSignIn.TeamMemberAsync(HttpContext, member, owner, props, twoStepStamp);

    private Task SignInAgentAsync(AgentUser user, AuthenticationProperties props, string? twoStepStamp = null) =>
        PortalSignIn.AdviserAsync(HttpContext, user, props, twoStepStamp);

    // The stamp this session was made with (TwoStepGate.ClaimType): a cookie made again for the same
    // session keeps it, or AgentCookieRevalidator would end the session at the next request.
    private string? CurrentTwoStepStamp => User.FindFirstValue(TwoStepGate.ClaimType);

    // 593: null when the login email may change: the adviser's own session, the current password (counted
    // as a sign-in is, 583), and a code when two-step sign-in is on (counted as at the code page).
    private async Task<string?> EmailChangeProofErrorAsync(AgentUser agent, string? currentPassword, string? code)
    {
        if (User.FindFirstValue("TeamMemberId") != null)
            return "Only the account owner can change the login email.";
        if (string.IsNullOrWhiteSpace(currentPassword))
            return "To change the login email, type your current password.";
        var check = await SignInLockout.TryAsync(_db, SignInDoor.Portal, agent.UserName,
            new[] { new SignInCandidate(new SignInAccount(SignInAccountKind.Adviser, agent.Id), () => _agents.PasswordMatches(agent, currentPassword!)) },
            DateTime.UtcNow);
        NoteLocks(check, SignInDoor.Portal);
        if (!check.Succeeded)
            return check.Locked ? TwoStepMessages.PasswordLocked : "Current password is incorrect.";

        var account = TwoStepAccount.Adviser(agent.Id);
        if (await TwoStepStore.FindAsync(_db, account) == null) return null;
        var result = await _twoStep.CheckCodeAsync(HttpContext, account, code);
        return result is TwoStepGate.CodeResult.Right or TwoStepGate.CodeResult.RightRecoveryCode
            ? null
            : result == TwoStepGate.CodeResult.Wrong
                ? "To change the login email, type a two-step sign-in code (or one of your recovery codes) too."
                : TwoStepMessages.ForResult(result, false);
    }

    // What the profile's Login Email card shows: whether a code is needed, and whether this session may
    // change the email at all.
    private async Task LoadEmailChangeStateAsync(int agentId)
    {
        ViewBag.EmailChangeByTeamMember = User.FindFirstValue("TeamMemberId") != null;
        var row = await TwoStepStore.FindAsync(_db, TwoStepAccount.Adviser(agentId));
        ViewBag.EmailChangeTwoStepMethod = row?.Method;
    }

    // The cookie made again after the account row changed (profile, portal colour), as the same person.
    // 593: a team member stays a team member. Before, this signed them in again as the adviser -- without
    // the TeamMemberId claim, so Billing and My Team (more logins, other members' passwords) opened to
    // them the moment they saved the profile form or picked a colour.
    private async Task RefreshSessionAsync(AgentUser agent)
    {
        var props = new AuthenticationProperties
        {
            IsPersistent = false,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
        };
        if (User.FindFirstValue("TeamMemberId") is { } memberClaim)
        {
            var member = int.TryParse(memberClaim, out var memberId)
                ? await _db.TeamMembers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == memberId && t.AgentUserId == agent.Id && t.IsActive)
                : null;
            if (member == null)
            {
                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }
            await SignInTeamMemberAsync(member, agent, props, CurrentTwoStepStamp);
            return;
        }
        await SignInAgentAsync(agent, props, CurrentTwoStepStamp);
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

    private static string NormalizeIdentifier(string value) => SignUpIdentifier.From(value);

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
        TaxRegistrationNumber = agent.TaxRegistrationNumber,
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
        model.TaxRegistrationNumber = model.TaxRegistrationNumber?.Trim() ?? "";
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
}
