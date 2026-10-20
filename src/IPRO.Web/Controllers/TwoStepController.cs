using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

// 593 (2026-10-20): two-step sign-in for the adviser portal, for advisers and their team members alike.
//
//  - Verify / SendCode: the second step of signing in. Nobody is signed in yet: the password page left a
//    protected cookie naming the account (TwoStepGate). A right code signs the person in.
//  - Index / Setup / StartEmail / Confirm / TurnOff / EmailMeACode / NewRecoveryCodes: the person's own
//    two-step, signed in. Turning it on, off, or making new recovery codes needs the current password
//    (counted as 583 counts it); turning it off or new recovery codes also needs a code (counted too).
[Authorize]
public class TwoStepController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IAgentService _agents;
    private readonly TwoStepGate _gate;
    private readonly ITwoStepNotices _notices;
    private readonly ISignInLockNotices _lockNotices;
    private readonly IPasswordHasher<TeamMember> _teamHasher;
    private readonly ILogger<TwoStepController> _logger;

    public TwoStepController(IPRODbContext db, IAgentService agents, TwoStepGate gate, ITwoStepNotices notices, ISignInLockNotices lockNotices,
        IPasswordHasher<TeamMember> teamHasher, ILogger<TwoStepController> logger)
    {
        _db = db;
        _agents = agents;
        _gate = gate;
        _notices = notices;
        _lockNotices = lockNotices;
        _teamHasher = teamHasher;
        _logger = logger;
    }

    // ---- the second step of signing in -------------------------------------------------------------------

    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> Verify()
    {
        var signIn = await PendingAsync();
        if (signIn == null) return BackToSignIn();
        return View(VerifyModel(signIn, error: null, notice: TempData["TwoStepNotice"] as string));
    }

    [AllowAnonymous]
    [EnableRateLimiting(EndpointRateLimits.SignIn)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Verify(string? code, bool rememberDevice = false)
    {
        var signIn = await PendingAsync();
        if (signIn == null) return BackToSignIn();

        var result = await _gate.CheckCodeAsync(HttpContext, signIn.Person.Account, code);
        if (result is TwoStepGate.CodeResult.Locked or TwoStepGate.CodeResult.Held or TwoStepGate.CodeResult.Wrong)
            return View(VerifyModel(signIn, TwoStepMessages.ForResult(result, signIn.Person.Member != null), null));

        _gate.ClearPending(HttpContext);
        // A recovery code is for a lost phone: this browser is not remembered on the strength of one.
        if (rememberDevice && result == TwoStepGate.CodeResult.Right)
            _gate.RememberDevice(HttpContext, signIn.Person.Account, signIn.Row.Stamp, signIn.Person.PasswordHash);

        var props = PortalSignIn.Session(signIn.Pending.R);
        var mustChangePassword = signIn.Person.Member?.MustChangePassword ?? signIn.Person.Agent.MustChangePassword;
        if (signIn.Person.Member is { } member)
        {
            await PortalSignIn.TeamMemberAsync(HttpContext, member, signIn.Person.Agent, props, signIn.Row.Stamp);
            await _db.TeamMembers.Where(t => t.Id == member.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastLoginAt, DateTime.UtcNow));
        }
        else
        {
            await PortalSignIn.AdviserAsync(HttpContext, signIn.Person.Agent, props, signIn.Row.Stamp);
            await _agents.UpdateLastLoginAsync(signIn.Person.Agent.Id);
        }

        if (result == TwoStepGate.CodeResult.RightRecoveryCode)
        {
            var left = await TwoStepStore.RecoveryCodesLeftAsync(_db, signIn.Person.Account);
            _logger.LogWarning("Signed in with a two-step recovery code: {Account}, {Left} left", TwoStepNotices.Describe(signIn.Person.Account), left);
            _notices.Queue(signIn.Person.Account, TwoStepNotice.RecoveryCodeUsed, left.ToString());
            TempData["Warning"] = $"You signed in with a recovery code; it will not work again. You have {left} left. " +
                                  "Make new ones under Two-step sign-in if you are running low.";
        }

        if (mustChangePassword) return Redirect("/portal/Account/ChangePassword");
        if (!string.IsNullOrWhiteSpace(signIn.Pending.U) && Url.IsLocalUrl(signIn.Pending.U)) return LocalRedirect(signIn.Pending.U);
        // Explicit path: "/" on an adviser's own domain is their public website (see AccountController.Login).
        return Redirect("/portal/Dashboard");
    }

    // A new emailed code (the email method). At most one a minute and 5 an hour per person (TwoStepStore).
    [AllowAnonymous]
    [EnableRateLimiting(EndpointRateLimits.PasswordReset)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SendCode()
    {
        var signIn = await PendingAsync();
        if (signIn == null) return BackToSignIn();
        if (signIn.Row.Method == TwoStepMethods.Email)
            TempData["TwoStepNotice"] = EmailCodeMessage(await _gate.SendEmailCodeAsync(signIn.Person.Account, signIn.Row.Stamp));
        return Redirect(TwoStepGate.VerifyPath);
    }

    // ---- the person's own two-step -------------------------------------------------------------------------

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var person = await CurrentPersonAsync();
        if (person == null) return Redirect("/portal/Account/Login");
        var row = await TwoStepStore.FindAsync(_db, person.Account);
        return View(new TwoStepStatusViewModel
        {
            IsOn = row != null,
            Method = row?.Method ?? string.Empty,
            EnabledAt = row?.EnabledAt,
            RecoveryCodesLeft = row == null ? 0 : await TwoStepStore.RecoveryCodesLeftAsync(_db, person.Account),
            Required = await TwoStepStore.IsRequiredAsync(_db, person.Agent.Id),
            IsTeamMember = person.Member != null,
            MaskedEmail = TwoStepGate.MaskEmail(person.Email),
            TimeZone = person.Agent.TimeZone ?? string.Empty
        });
    }

    // Choose a way; `method` = "app" shows the QR code and the typed key, "email" offers to send a code.
    [HttpGet]
    public async Task<IActionResult> Setup(string? method = null)
    {
        var person = await CurrentPersonAsync();
        if (person == null) return Redirect("/portal/Account/Login");
        if (await TwoStepStore.FindAsync(_db, person.Account) != null)
        {
            TempData["Success"] = "Two-step sign-in is already on. To change how you get your codes, turn it off first, then set it up again.";
            return RedirectToAction(nameof(Index));
        }

        var required = await TwoStepStore.IsRequiredAsync(_db, person.Agent.Id);
        var way = Way(method);
        // Either way starts with a code to the email on file: the person turning it on can read the
        // account's inbox, not only knows the password (StartEmail).
        return View(new TwoStepSetupViewModel { Method = way, Required = required, MaskedEmail = TwoStepGate.MaskEmail(person.Email) });
    }

    // The code to the email on file (the set-up's proof of the inbox), then the confirm form: for the app
    // way with the QR code. `setupToken`: the page's own, when it asks for a new code (the app way keeps
    // the secret already scanned).
    [EnableRateLimiting(EndpointRateLimits.PasswordReset)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> StartEmail(string? method = null, string? setupToken = null)
    {
        var person = await CurrentPersonAsync();
        if (person == null) return Redirect("/portal/Account/Login");
        if (await TwoStepStore.FindAsync(_db, person.Account) != null) return RedirectToAction(nameof(Index));
        var way = Way(method);
        if (way == null) return RedirectToAction(nameof(Setup));

        var earlier = _gate.ReadSetup(setupToken, person.Account, person.PasswordHash);
        var secret = way == TwoStepMethods.App
            ? (earlier?.Method == TwoStepMethods.App ? Totp.FromBase32(earlier.Secret) : null) ?? Totp.NewSecret()
            : null;
        var model = SetupModel(person, way, secret, await TwoStepStore.IsRequiredAsync(_db, person.Agent.Id), error: null, token: null);
        model.EmailSent = true;

        var code = await TwoStepStore.IssueSetupCodeAsync(_db, person.Account, _gate.Now);
        if (code == null) model.Error = TwoStepMessages.SetupCodeTooSoon;
        else if (!await _notices.SendCodeAsync(person.Account, code, forSetup: true)) model.Error = TwoStepMessages.CodeNotSent;
        return View(nameof(Setup), model);
    }

    // The code from the email (and, the app way, the app's code) and the current password: two-step is on.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(string? setupToken, string? code, string? emailCode, string? currentPassword)
    {
        var person = await CurrentPersonAsync();
        if (person == null) return Redirect("/portal/Account/Login");
        if (await TwoStepStore.FindAsync(_db, person.Account) != null) return RedirectToAction(nameof(Index));

        var state = _gate.ReadSetup(setupToken, person.Account, person.PasswordHash);
        if (state == null || !TwoStepMethods.IsKnown(state.Method))
        {
            TempData["Error"] = "That set-up page was open for more than 15 minutes (or your password changed), so please start again.";
            return RedirectToAction(nameof(Setup));
        }

        var required = await TwoStepStore.IsRequiredAsync(_db, person.Agent.Id);
        var secret = state.Method == TwoStepMethods.App ? Totp.FromBase32(state.Secret) : null;
        if (state.Method == TwoStepMethods.App && secret == null) return RedirectToAction(nameof(Setup));
        IActionResult Again(string error)
        {
            var again = SetupModel(person, state.Method, secret, required, error, setupToken);
            again.EmailSent = true;
            return View(nameof(Setup), again);
        }

        var password = await PasswordCheckAsync(person, currentPassword);
        if (password != PasswordResult.Right) return Again(password == PasswordResult.Locked ? TwoStepMessages.PasswordLocked : TwoStepMessages.WrongPassword);

        // The app's code first: checking it uses nothing up, so a typo there does not cost the emailed code.
        long step = 0;
        if (state.Method == TwoStepMethods.App)
        {
            var matched = Totp.MatchStep(secret!, code, _gate.Now, -1);
            if (matched == null) return Again(TwoStepMessages.WrongCode);
            step = matched.Value;
        }
        switch (await TwoStepStore.UseSetupCodeAsync(_db, person.Account, emailCode, _gate.Now))
        {
            case TwoStepStore.SetupCodeResult.Wrong: return Again(TwoStepMessages.WrongSetupCode);
            case TwoStepStore.SetupCodeResult.NoCode: return Again(TwoStepMessages.SetupCodeSpent);
        }

        var enabled = await TwoStepStore.EnableAsync(_db, person.Account, state.Method,
            secret == null ? null : _gate.ProtectSecret(secret), step, _gate.Now);
        if (enabled == null)
        {
            TempData["Success"] = "Two-step sign-in is already on.";
            return RedirectToAction(nameof(Index));
        }
        var (stamp, codes) = enabled.Value;
        // This session passed both steps just now; every other session of this person ends (wrong stamp).
        await ReissueSessionAsync(person, stamp);
        _logger.LogWarning("Two-step sign-in turned on ({Method}): {Account}", state.Method, TwoStepNotices.Describe(person.Account));
        _notices.Queue(person.Account, TwoStepNotice.TurnedOn, state.Method);
        return View("RecoveryCodes", new TwoStepRecoveryCodesViewModel { Codes = codes, JustTurnedOn = true });
    }

    // Off: the current password and a code (app, emailed or recovery), both counted.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TurnOff(string? currentPassword, string? code)
    {
        var person = await CurrentPersonAsync();
        if (person == null) return Redirect("/portal/Account/Login");
        if (await TwoStepStore.FindAsync(_db, person.Account) == null) return RedirectToAction(nameof(Index));

        var error = await PasswordAndCodeErrorAsync(person, currentPassword, code);
        if (error != null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Index));
        }

        await TwoStepStore.DisableAsync(_db, person.Account);
        await ReissueSessionAsync(person, null);
        _gate.ForgetDevice(HttpContext, person.Account);
        _logger.LogWarning("Two-step sign-in turned off by the person: {Account}", TwoStepNotices.Describe(person.Account));
        _notices.Queue(person.Account, TwoStepNotice.TurnedOff);
        TempData["Success"] = await TwoStepStore.IsRequiredAsync(_db, person.Agent.Id)
            ? "Two-step sign-in is off. It is required for your account, so set it up again now."
            : "Two-step sign-in is off. Your password alone now signs you in.";
        return RedirectToAction(nameof(Index));
    }

    // The email way's code for turning it off, making new recovery codes, or changing the login email on
    // the profile (`returnTo` = "profile" goes back there).
    [EnableRateLimiting(EndpointRateLimits.PasswordReset)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EmailMeACode(string? returnTo = null)
    {
        var person = await CurrentPersonAsync();
        if (person == null) return Redirect("/portal/Account/Login");
        var row = await TwoStepStore.FindAsync(_db, person.Account);
        if (row?.Method == TwoStepMethods.Email)
        {
            var sent = await _gate.SendEmailCodeAsync(person.Account, row.Stamp);
            TempData[sent == TwoStepGate.EmailCodeResult.Sent ? "Success" : "Error"] = EmailCodeMessage(sent);
        }
        return string.Equals(returnTo, "profile", StringComparison.Ordinal) ? Redirect("/portal/Account/Profile") : RedirectToAction(nameof(Index));
    }

    // Ten new recovery codes; the old ones stop working, and so does every other signed-in browser and
    // every remembered device (a new stamp): new codes are what someone does who fears the old were seen.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> NewRecoveryCodes(string? currentPassword, string? code)
    {
        var person = await CurrentPersonAsync();
        if (person == null) return Redirect("/portal/Account/Login");
        if (await TwoStepStore.FindAsync(_db, person.Account) == null) return RedirectToAction(nameof(Index));

        var error = await PasswordAndCodeErrorAsync(person, currentPassword, code);
        if (error != null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Index));
        }
        var row = await TwoStepStore.FindAsync(_db, person.Account);
        var replaced = row == null ? null : await TwoStepStore.ReplaceRecoveryCodesAsync(_db, person.Account, row.Stamp, _gate.Now);
        if (replaced == null) return RedirectToAction(nameof(Index));
        await ReissueSessionAsync(person, replaced.Value.Stamp);
        _logger.LogWarning("New two-step recovery codes made: {Account}", TwoStepNotices.Describe(person.Account));
        return View("RecoveryCodes", new TwoStepRecoveryCodesViewModel { Codes = replaced.Value.RecoveryCodes });
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private sealed record Person(TwoStepAccount Account, AgentUser Agent, TeamMember? Member)
    {
        public string PasswordHash => Member?.PasswordHash ?? Agent.PasswordHash;
        public string Email => Member?.Email ?? Agent.Email;
        // The name 583 counts a wrong password against (the sign-in page's name for this account).
        public string SignInName => Member?.Email ?? Agent.UserName;
    }

    private sealed record PendingSignIn(TwoStepGate.PendingSignIn Pending, Person Person, TwoStepSignIn Row);

    // The signed-in person: the adviser, or the team member acting in their account.
    private async Task<Person?> CurrentPersonAsync()
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var agentId)) return null;
        var agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId && a.IsActive);
        if (agent == null) return null;
        if (User.FindFirstValue("TeamMemberId") is { } memberClaim)
        {
            if (!int.TryParse(memberClaim, out var memberId)) return null;
            var member = await _db.TeamMembers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == memberId && t.AgentUserId == agentId && t.IsActive);
            return member == null ? null : new Person(TwoStepAccount.Member(agentId, member.Id), agent, member);
        }
        return new Person(TwoStepAccount.Adviser(agentId), agent, null);
    }

    // The half-finished sign-in, still good: the account is active, its two-step is the one the password
    // step saw, and its password has not changed since. Anything else ends it.
    private async Task<PendingSignIn?> PendingAsync()
    {
        var pending = _gate.ReadPending(HttpContext);
        if (pending == null) return null;
        var agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == pending.A && a.IsActive);
        TeamMember? member = null;
        if (agent != null && pending.M != 0)
            member = await _db.TeamMembers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == pending.M && t.AgentUserId == pending.A && t.IsActive);
        var person = agent == null || (pending.M != 0 && member == null) ? null : new Person(pending.Account, agent, member);
        var row = person == null ? null : await TwoStepStore.FindAsync(_db, person.Account);
        if (person == null || row == null || !string.Equals(row.Stamp, pending.S, StringComparison.Ordinal)
            || !string.Equals(TwoStepGate.Fingerprint(person.PasswordHash), pending.P, StringComparison.Ordinal))
        {
            _gate.ClearPending(HttpContext);
            return null;
        }
        return new PendingSignIn(pending, person, row);
    }

    private IActionResult BackToSignIn()
    {
        TempData["SignInNotice"] = TwoStepMessages.Expired;
        return Redirect("/portal/Account/Login");
    }

    private TwoStepVerifyViewModel VerifyModel(PendingSignIn signIn, string? error, string? notice) => new()
    {
        Method = signIn.Row.Method,
        MaskedEmail = TwoStepGate.MaskEmail(signIn.Person.Email),
        IsTeamMember = signIn.Person.Member != null,
        Error = error,
        Notice = notice
    };

    private static string? Way(string? method) =>
        string.Equals(method, "app", StringComparison.OrdinalIgnoreCase) ? TwoStepMethods.App
        : string.Equals(method, "email", StringComparison.OrdinalIgnoreCase) ? TwoStepMethods.Email
        : null;

    // The confirm form for either way; the app way with its QR code and typed key.
    private TwoStepSetupViewModel SetupModel(Person person, string way, byte[]? secret, bool required, string? error, string? token)
    {
        var model = new TwoStepSetupViewModel
        {
            Method = way,
            Required = required,
            Error = error,
            MaskedEmail = TwoStepGate.MaskEmail(person.Email),
            SetupToken = token ?? _gate.ProtectSetup(new TwoStepGate.SetupState(person.Account.AgentUserId, person.Account.TeamMemberId, way,
                secret == null ? null : Totp.ToBase32(secret), TwoStepGate.Fingerprint(person.PasswordHash), _gate.Now.Ticks))
        };
        if (secret != null)
        {
            var account = person.Member?.Email ?? person.Agent.UserName;
            model.QrSvg = QrSvg.Render(Totp.OtpAuthUri(TwoStepGate.Issuer, account, secret), "QR code for your authenticator app");
            model.ManualKey = Totp.GroupedKey(secret);
        }
        return model;
    }

    private static string EmailCodeMessage(TwoStepGate.EmailCodeResult result) => result switch
    {
        TwoStepGate.EmailCodeResult.Sent => TwoStepMessages.CodeSent,
        TwoStepGate.EmailCodeResult.TooSoon => TwoStepMessages.CodeTooSoon,
        _ => TwoStepMessages.CodeNotSent
    };

    private enum PasswordResult { Right, Wrong, Locked }

    // The current password, checked and counted as a sign-in is (583): a session left open must not be a
    // way to change two-step, or to try passwords without limit.
    private async Task<PasswordResult> PasswordCheckAsync(Person person, string? password)
    {
        if (string.IsNullOrWhiteSpace(password)) return PasswordResult.Wrong;
        var candidate = person.Member is { } member
            ? new SignInCandidate(new SignInAccount(SignInAccountKind.TeamMember, member.Id),
                () => _teamHasher.VerifyHashedPassword(member, member.PasswordHash, password) != PasswordVerificationResult.Failed)
            : new SignInCandidate(new SignInAccount(SignInAccountKind.Adviser, person.Agent.Id), () => _agents.PasswordMatches(person.Agent, password));
        var attempt = await SignInLockout.TryAsync(_db, SignInDoor.Portal, person.SignInName, new[] { candidate }, DateTime.UtcNow);
        SignInLockout.LogLocks(_logger, attempt, SignInDoor.Portal, RateLimitVisitor.Key(HttpContext));
        foreach (var locked in attempt.LockedNow) _lockNotices.Queue(locked);
        if (attempt.Succeeded) return PasswordResult.Right;
        return attempt.Locked ? PasswordResult.Locked : PasswordResult.Wrong;
    }

    // Null when both are right. The code is only tried after the password, so a wrong password costs no
    // code attempt.
    private async Task<string?> PasswordAndCodeErrorAsync(Person person, string? password, string? code)
    {
        var passwordResult = await PasswordCheckAsync(person, password);
        if (passwordResult == PasswordResult.Locked) return TwoStepMessages.PasswordLocked;
        if (passwordResult == PasswordResult.Wrong) return TwoStepMessages.WrongPassword;
        var result = await _gate.CheckCodeAsync(HttpContext, person.Account, code);
        return result is TwoStepGate.CodeResult.Right or TwoStepGate.CodeResult.RightRecoveryCode ? null : TwoStepMessages.ForResult(result, person.Member != null);
    }

    // The session goes on as the same person with the new stamp (or none). As the other re-issues of the
    // cookie (AccountController.Profile and the like): 8 hours, not kept when the browser closes.
    private Task ReissueSessionAsync(Person person, string? stamp)
    {
        var props = new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) };
        return person.Member is { } member
            ? PortalSignIn.TeamMemberAsync(HttpContext, member, person.Agent, props, stamp)
            : PortalSignIn.AdviserAsync(HttpContext, person.Agent, props, stamp);
    }
}
