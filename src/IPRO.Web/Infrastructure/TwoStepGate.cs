using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Web.Infrastructure;

// 593 (2026-10-20): two-step sign-in for the adviser portal (advisers and their team members). After the
// right password, a person who has it on proves it is them a second way before any session exists: the
// code their authenticator app shows, a code iPro emails them, or one of their recovery codes.
//
// Between the two steps nothing is signed in. The password step leaves a protected cookie (10 minutes)
// naming the account, the two-step stamp and a fingerprint of the password hash; the code page reads it,
// so a password changed or two-step turned off in the meantime ends that half-finished sign-in.
//
// The session cookie made after the second step carries the account's stamp (claim "TwoStep"), and
// AgentCookieRevalidator compares it on every request: a session from before two-step was turned on (a
// "Remember me" cookie included) or from before it was reset has the wrong stamp, and is signed out.
//
// "Remember this device for 30 days" is a second protected cookie, one per account, holding the same
// stamp and password fingerprint: a new password, or two-step turned off (and on again), ends it.
public sealed class TwoStepGate
{
    public const string ClaimType = "TwoStep";
    // Set by AgentCookieRevalidator for a session whose account must have two-step and has none yet.
    public const string SetupRequiredItem = "IPRO.TwoStepSetupRequired";
    public const string PendingCookie = "IPRO.TwoStepPending";
    public const string DeviceCookiePrefix = "IPRO.TwoStepDevice.";
    public static readonly TimeSpan PendingFor = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RememberDeviceFor = TimeSpan.FromDays(30);
    public static readonly TimeSpan SetupFor = TimeSpan.FromMinutes(15);
    // Under /portal: on an adviser's own domain a bare path is their public website (INVARIANTS).
    public const string VerifyPath = "/portal/TwoStep/Verify";
    public const string SetupPath = "/portal/TwoStep/Setup";
    public const string Issuer = "iPro";

    private readonly IPRODbContext _db;
    private readonly IDataProtector _secrets;
    private readonly IDataProtector _pending;
    private readonly IDataProtector _devices;
    private readonly IDataProtector _setup;
    private readonly ITwoStepNotices _notices;
    private readonly ISignInLockNotices _lockNotices;
    private readonly ILogger<TwoStepGate> _logger;
    private readonly Func<DateTime> _now;

    public TwoStepGate(IPRODbContext db, IDataProtectionProvider protection, ITwoStepNotices notices, ISignInLockNotices lockNotices,
        ILogger<TwoStepGate> logger, Func<DateTime>? now = null)
    {
        _db = db;
        _secrets = protection.CreateProtector("IPRO.Web.TwoStep.Secret.v1");
        _pending = protection.CreateProtector("IPRO.Web.TwoStep.Pending.v1");
        _devices = protection.CreateProtector("IPRO.Web.TwoStep.Device.v1");
        _setup = protection.CreateProtector("IPRO.Web.TwoStep.Setup.v1");
        _notices = notices;
        _lockNotices = lockNotices;
        _logger = logger;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public DateTime Now => _now();

    // Whose two-step a signed-in session is: the adviser's, or the team member's acting in the account.
    public static TwoStepAccount? AccountOf(System.Security.Claims.ClaimsPrincipal user)
    {
        if (!int.TryParse(user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var agentId)) return null;
        var member = user.FindFirst("TeamMemberId")?.Value;
        if (member == null) return TwoStepAccount.Adviser(agentId);
        return int.TryParse(member, out var memberId) ? TwoStepAccount.Member(agentId, memberId) : null;
    }

    // ---- after the password ------------------------------------------------------------------------------

    // `Next`: where to send the person (the code page), or null to sign them in now -- two-step is off, or
    // this browser is remembered -- with `Stamp` for the session (null when two-step is off).
    public sealed record Decision(IActionResult? Next, string? Stamp);

    // Every place a right password would sign someone in to the portal comes here first: the sign-in page
    // (advisers and team members, "Remember me" or not) and the forgot-password reset.
    public async Task<Decision> AfterPasswordAsync(HttpContext http, TwoStepAccount account, string? passwordHash, bool rememberMe, string? returnUrl)
    {
        var row = await TwoStepStore.FindAsync(_db, account);
        if (row == null) return new Decision(null, null);
        if (IsRememberedDevice(http, account, row.Stamp, passwordHash)) return new Decision(null, row.Stamp);

        WritePending(http, new PendingSignIn(account.AgentUserId, account.TeamMemberId, row.Stamp, Fingerprint(passwordHash), rememberMe,
            returnUrl is { Length: > 0 } && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\") ? returnUrl : null,
            _now().Ticks));
        if (row.Method == TwoStepMethods.Email) await SendEmailCodeAsync(account, row.Stamp);
        return new Decision(new RedirectResult(VerifyPath), null);
    }

    // The half-finished sign-in. `R`: "Remember me" was ticked; `U`: where they were going; `T`: when.
    public sealed record PendingSignIn(int A, int M, string S, string P, bool R, string? U, long T)
    {
        public TwoStepAccount Account => new(A, M);
    }

    public PendingSignIn? ReadPending(HttpContext http)
    {
        var pending = Unprotect<PendingSignIn>(_pending, http.Request.Cookies[PendingCookie]);
        if (pending == null) return null;
        var age = _now() - new DateTime(pending.T, DateTimeKind.Utc);
        return age <= PendingFor && age >= TimeSpan.FromMinutes(-1) ? pending : null;
    }

    public void ClearPending(HttpContext http) => http.Response.Cookies.Delete(PendingCookie, CookieOptions(null));

    private void WritePending(HttpContext http, PendingSignIn pending) =>
        http.Response.Cookies.Append(PendingCookie, Protect(_pending, pending), CookieOptions(null));

    // ---- remember this device ------------------------------------------------------------------------------

    private sealed record RememberedDevice(int A, int M, string S, string P, long E);

    public bool IsRememberedDevice(HttpContext http, TwoStepAccount account, string stamp, string? passwordHash)
    {
        var device = Unprotect<RememberedDevice>(_devices, http.Request.Cookies[DeviceCookieName(account)]);
        return device != null
               && device.A == account.AgentUserId && device.M == account.TeamMemberId
               && string.Equals(device.S, stamp, StringComparison.Ordinal)
               && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(device.P), Encoding.ASCII.GetBytes(Fingerprint(passwordHash)))
               && new DateTime(device.E, DateTimeKind.Utc) > _now();
    }

    public void RememberDevice(HttpContext http, TwoStepAccount account, string stamp, string? passwordHash)
    {
        var expires = _now() + RememberDeviceFor;
        http.Response.Cookies.Append(DeviceCookieName(account),
            Protect(_devices, new RememberedDevice(account.AgentUserId, account.TeamMemberId, stamp, Fingerprint(passwordHash), expires.Ticks)),
            CookieOptions(expires));
    }

    public void ForgetDevice(HttpContext http, TwoStepAccount account) =>
        http.Response.Cookies.Delete(DeviceCookieName(account), CookieOptions(null));

    // One cookie per account (an adviser and their assistant may share a computer); the name says nothing
    // about the account to someone reading the browser's cookies.
    public static string DeviceCookieName(TwoStepAccount account) =>
        DeviceCookiePrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{account.AgentUserId}:{account.TeamMemberId}")))[..12].ToLowerInvariant();

    // Changes whenever the password does (every way of setting one writes a new hash).
    public static string Fingerprint(string? passwordHash) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash ?? string.Empty)))[..32].ToLowerInvariant();

    // ---- one code ----------------------------------------------------------------------------------------------

    // Held: locked until SuperAdmin unlocks it (TwoStepStore.WrongCodesBeforeHold wrong codes since a right one).
    public enum CodeResult { Right, RightRecoveryCode, Wrong, Locked, Held }

    // Checks what the person typed -- an authenticator code, an emailed code or a recovery code -- counted
    // as 583 counts passwords: 5 wrong in a row lock the second step for 15 minutes (one email, one log line),
    // and 20 since the last right one hold it until SuperAdmin unlocks it.
    public async Task<CodeResult> CheckCodeAsync(HttpContext http, TwoStepAccount account, string? typed)
    {
        var now = _now();
        if (!await TwoStepStore.BeginCodeAsync(_db, account, now))
        {
            var current = await TwoStepStore.FindAsync(_db, account);
            return current == null ? CodeResult.Wrong : TwoStepStore.IsCodeHeld(current) ? CodeResult.Held : CodeResult.Locked;
        }

        var row = await TwoStepStore.FindAsync(_db, account);
        var result = CodeResult.Wrong;
        if (row != null)
        {
            if (TwoStepStore.LooksLikeRecoveryCode(typed))
            {
                if (await TwoStepStore.UseRecoveryCodeAsync(_db, account, typed, now)) result = CodeResult.RightRecoveryCode;
            }
            else if (row.Method == TwoStepMethods.App)
            {
                var secret = UnprotectSecret(row.ProtectedSecret);
                var step = secret == null ? null : Totp.MatchStep(secret, typed, now, row.LastUsedStep);
                if (step != null && await TwoStepStore.ClaimAppStepAsync(_db, account, row.Stamp, step.Value)) result = CodeResult.Right;
            }
            else if (row.Method == TwoStepMethods.Email)
            {
                if (await TwoStepStore.UseEmailCodeAsync(_db, account, row.Stamp, typed, now)) result = CodeResult.Right;
            }
        }

        if (result != CodeResult.Wrong)
        {
            await TwoStepStore.CodeSucceededAsync(_db, account);
            return result;
        }
        if (!await TwoStepStore.CodeFailedAsync(_db, account, now)) return CodeResult.Wrong;

        var held = TwoStepStore.IsCodeHeld(await TwoStepStore.FindAsync(_db, account));
        if (held)
            _logger.LogWarning("Two-step sign-in held until SuperAdmin unlocks it after {Count} wrong codes since the last right one: {Account}, last try from {Visitor}",
                TwoStepStore.WrongCodesBeforeHold, TwoStepNotices.Describe(account), RateLimitVisitor.Key(http));
        else
            _logger.LogWarning("Two-step sign-in locked for 15 minutes after 5 wrong codes in a row: {Account}, last try from {Visitor}",
                TwoStepNotices.Describe(account), RateLimitVisitor.Key(http));
        _lockNotices.QueueCodeLock(account);
        return held ? CodeResult.Held : CodeResult.Locked;
    }

    // ---- the emailed code -----------------------------------------------------------------------------------

    // 593 (review): the old address hears when the login email changes (the new one gets every reset link
    // and emailed code from now on).
    public void NoticeEmailChanged(TwoStepAccount account, string oldEmail) =>
        _notices.Queue(account, TwoStepNotice.EmailChanged, oldEmail);

    public enum EmailCodeResult { Sent, TooSoon, NotSent }

    public async Task<EmailCodeResult> SendEmailCodeAsync(TwoStepAccount account, string stamp)
    {
        var code = await TwoStepStore.IssueEmailCodeAsync(_db, account, stamp, _now());
        if (code == null) return EmailCodeResult.TooSoon;
        return await _notices.SendCodeAsync(account, code, forSetup: false) ? EmailCodeResult.Sent : EmailCodeResult.NotSent;
    }

    // "p***@lee.example.com": enough for the person to know which inbox, not enough to give it away.
    public static string MaskEmail(string? email)
    {
        var value = email?.Trim() ?? string.Empty;
        var at = value.IndexOf('@');
        if (at <= 0) return "your email address";
        return value[0] + "***" + value[at..];
    }

    // ---- the authenticator secret ----------------------------------------------------------------------------

    public string ProtectSecret(byte[] secret) => _secrets.Protect(Totp.ToBase32(secret));

    public byte[]? UnprotectSecret(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            return Totp.FromBase32(_secrets.Unprotect(stored));
        }
        catch (CryptographicException ex)
        {
            // The keys that protected it are gone (a new server without the old key ring): app codes cannot
            // be checked; recovery codes and a reset still work.
            _logger.LogError(ex, "An authenticator secret could not be read; the person can use a recovery code, or SuperAdmin can reset two-step");
            return null;
        }
    }

    // ---- setting it up ----------------------------------------------------------------------------------------

    // What the set-up page carries between showing the QR code and the confirmation: the way, the new
    // secret (the app way); for whom; against which password; when. The emailed set-up code itself is in
    // TwoStepSetupCodes (counted, used once), not here.
    public sealed record SetupState(int A, int M, string Method, string? Secret, string P, long T);

    public string ProtectSetup(SetupState state) => Protect(_setup, state);

    public SetupState? ReadSetup(string? token, TwoStepAccount account, string? passwordHash)
    {
        var state = Unprotect<SetupState>(_setup, token);
        if (state == null || state.A != account.AgentUserId || state.M != account.TeamMemberId) return null;
        if (!string.Equals(state.P, Fingerprint(passwordHash), StringComparison.Ordinal)) return null;
        var age = _now() - new DateTime(state.T, DateTimeKind.Utc);
        return age <= SetupFor && age >= TimeSpan.FromMinutes(-1) ? state : null;
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static CookieOptions CookieOptions(DateTime? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Path = "/",
        Expires = expires is { } e ? new DateTimeOffset(e, TimeSpan.Zero) : null
    };

    private static string Protect<T>(IDataProtector protector, T value) => protector.Protect(JsonSerializer.Serialize(value));

    private static T? Unprotect<T>(IDataProtector protector, string? raw) where T : class
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(protector.Unprotect(raw));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }
}

// 593: what two-step sign-in's pages say.
public static class TwoStepMessages
{
    public const string WrongCode = "That code is not right. Type the newest code and try again.";

    public const string LockedAdviser =
        "Sign-in is locked for 15 minutes after 5 wrong codes in a row. Try again after 15 minutes. If you cannot get a code, " +
        "use one of your recovery codes then, or call iPro support at " + IPRO.Entities.TwoStepLetters.SupportPhone + ".";

    public const string LockedTeamMember =
        "Sign-in is locked for 15 minutes after 5 wrong codes in a row. Try again after 15 minutes. If you cannot get a code, " +
        "use one of your recovery codes then, or ask the account owner to reset your two-step sign-in on My Team.";

    public static string Locked(bool teamMember) => teamMember ? LockedTeamMember : LockedAdviser;

    public const string HeldAdviser =
        "Sign-in is locked after 20 wrong codes without a right one: someone may know your password. Call iPro support at " +
        IPRO.Entities.TwoStepLetters.SupportPhone + " to unlock it, and choose a new password with \"Forgot password?\".";

    public const string HeldTeamMember =
        "Sign-in is locked after 20 wrong codes without a right one: someone may know your password. Ask the account owner " +
        "to reset your password and your two-step sign-in on My Team.";

    public static string Held(bool teamMember) => teamMember ? HeldTeamMember : HeldAdviser;

    public static string ForResult(TwoStepGate.CodeResult result, bool teamMember) => result switch
    {
        TwoStepGate.CodeResult.Held => Held(teamMember),
        TwoStepGate.CodeResult.Locked => Locked(teamMember),
        _ => WrongCode
    };

    public const string WrongSetupCode = "That emailed code is not right. Type the code from the newest email.";
    public const string SetupCodeSpent = "That emailed code has run out, or was typed wrong 5 times. Email yourself a new one.";
    public const string SetupCodeTooSoon = "We emailed you a code less than a minute ago (and no more than 10 a day). Check your inbox, and your junk folder.";

    public const string Expired = "That sign-in took longer than 10 minutes, so please sign in again.";

    public const string WrongPassword = "Your current password is not right.";

    public const string PasswordLocked =
        "Your sign-in is locked for 15 minutes after 5 wrong passwords in a row. Try again after 15 minutes.";

    public const string CodeSent = "We emailed you a new code.";
    public const string CodeTooSoon = "We sent you a code less than a minute ago (and no more than 5 an hour). Check your inbox, and your junk folder.";
    public const string CodeNotSent = "We could not send the email just now. Try again in a minute.";
}
