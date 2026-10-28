using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// 593 (2026-10-20): two-step sign-in's rows (TwoStepSignIns, TwoStepRecoveryCodes, TwoStepRequirements).
// Advisers and their team members: after the password, a code from an authenticator app, a code emailed
// to them, or one of their ten recovery codes.
//
// As in SignInLockout, every step that counts or uses something up is ONE statement whose WHERE says when
// it may happen, so two requests at the same moment cannot both use one code or both pass a count. The
// rule is 583's: 5 wrong codes in a row lock the second step for 15 minutes. It is its own count and its
// own lock, kept here and not in the password columns, because a new password must NOT end it: someone
// who has the password and the person's email could otherwise reset the password, guess 5 more codes,
// and reset again for ever. The lock runs out, or SuperAdmin clears it.
//
// The 15-minute locks alone would still let someone with the password guess 480 codes a day (the review
// of 593: about 4% a month). So wrong codes are also counted since the last right one (WrongCodes), and
// after WrongCodesBeforeHold of them -- four locks' worth -- the second step stays locked until SuperAdmin
// unlocks it (or the person's adviser resets a team member's two-step): whoever is guessing has the
// password, and the person must hear about it and change it.
public static class TwoStepStore
{
    public const int WrongCodesBeforeLock = SignInLockout.WrongPasswordsBeforeLock;
    public static readonly TimeSpan LockFor = SignInLockout.LockFor;
    public const int RecoveryCodeCount = 10;
    public static readonly TimeSpan EmailCodeLifetime = TimeSpan.FromMinutes(10);
    // An emailed code no more often than once a minute, and no more than 5 an hour, per person: enough for
    // a slow inbox, too few for someone with the password to fill the person's inbox.
    public static readonly TimeSpan EmailCodeEvery = TimeSpan.FromMinutes(1);
    public const int EmailCodesPerHour = 5;
    public const int WrongCodesBeforeHold = 4 * WrongCodesBeforeLock;
    // The code that proves the inbox when two-step is set up: as the sign-in code, but at most 10 a day.
    public static readonly TimeSpan SetupCodesWindow = TimeSpan.FromHours(24);
    public const int SetupCodesPerWindow = 10;

    public static bool IsCodeLocked(TwoStepSignIn? row, DateTime now) => row != null && (row.CodeLockedUntil is { } until && until > now || IsCodeHeld(row));

    // Locked until SuperAdmin unlocks it (WrongCodesBeforeHold wrong codes since the last right one).
    public static bool IsCodeHeld(TwoStepSignIn? row) => row != null && row.WrongCodes >= WrongCodesBeforeHold;

    // ---- reading -------------------------------------------------------------------------------------

    public static Task<TwoStepSignIn?> FindAsync(IPRODbContext db, TwoStepAccount account) =>
        db.TwoStepSignIns.AsNoTracking().FirstOrDefaultAsync(t => t.AgentUserId == account.AgentUserId && t.TeamMemberId == account.TeamMemberId);

    // Whether this adviser's account must have two-step (the adviser and their team members). One place
    // decides, so a rule for a whole package can be added here later.
    public static Task<bool> IsRequiredAsync(IPRODbContext db, int agentUserId) =>
        db.TwoStepRequirements.AsNoTracking().AnyAsync(r => r.AgentUserId == agentUserId);

    public static Task<int> RecoveryCodesLeftAsync(IPRODbContext db, TwoStepAccount account) =>
        db.TwoStepRecoveryCodes.AsNoTracking().CountAsync(r => r.AgentUserId == account.AgentUserId && r.TeamMemberId == account.TeamMemberId && r.UsedAt == null);

    // ---- turning it on and off -------------------------------------------------------------------------

    // Turns two-step on: a new stamp, new recovery codes (returned, shown once, never stored as they are).
    // `lastUsedStep`: the authenticator step of the code that confirmed the set-up, so the same code cannot
    // then sign anyone in. Null when it is on already: it never replaces one that is on (two set-ups at the
    // same moment -- the person's and someone else's -- must not let the later one quietly win); changing
    // the way is turning it off (password and a code) and on again.
    public static async Task<(string Stamp, IReadOnlyList<string> RecoveryCodes)?> EnableAsync(IPRODbContext db, TwoStepAccount account,
        string method, string? protectedSecret, long lastUsedStep, DateTime now)
    {
        if (!TwoStepMethods.IsKnown(method)) throw new ArgumentOutOfRangeException(nameof(method));
        var stamp = NewStamp();
        var codes = NewRecoveryCodes();
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO `TwoStepSignIns` (`AgentUserId`, `TeamMemberId`, `Method`, `ProtectedSecret`, `Stamp`, `EnabledAt`, `LastUsedStep`, `FailedCodeCount`, `WrongCodes`, `EmailCodesSent`) " +
                "VALUES ({0}, {1}, {2}, NULLIF({3}, ''), {4}, {5}, {6}, 0, 0, 0)",
                account.AgentUserId, account.TeamMemberId, method, protectedSecret ?? string.Empty, stamp, now, lastUsedStep);
        }
        catch (MySqlConnector.MySqlException ex) when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.DuplicateKeyEntry)
        {
            await transaction.RollbackAsync();
            return null;
        }
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM `TwoStepRecoveryCodes` WHERE `AgentUserId` = {0} AND `TeamMemberId` = {1}", account.AgentUserId, account.TeamMemberId);
        await InsertRecoveryCodesAsync(db, account, codes, now);
        // The set-up code is spent; its row (how many went out in the last 24 hours) stays, so turning it on
        // and off again does not start a new allowance of emails (the re-check of 593).
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE `TwoStepSetupCodes` SET `CodeHash` = NULL, `ExpiresAt` = NULL WHERE `AgentUserId` = {0} AND `TeamMemberId` = {1}", account.AgentUserId, account.TeamMemberId);
        await transaction.CommitAsync();
        return (stamp, codes);
    }

    // Turns it off: the row and the recovery codes go. True when it was on.
    public static async Task<bool> DisableAsync(IPRODbContext db, TwoStepAccount account)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var wasOn = await DeleteRowsAsync(db, account);
        await transaction.CommitAsync();
        return wasOn;
    }

    // A team member removed for good: nothing of theirs stays behind.
    public static async Task ForgetAsync(IPRODbContext db, TwoStepAccount account)
    {
        await DisableAsync(db, account);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM `TwoStepSetupCodes` WHERE `AgentUserId` = {0} AND `TeamMemberId` = {1}", account.AgentUserId, account.TeamMemberId);
    }

    // Ten new recovery codes in place of the old ones (all of them, used or not), and a new stamp: new codes
    // are what someone does who thinks the old ones were seen, so every other signed-in browser and every
    // remembered device ends too. Null when two-step is not on (or `stamp` is no longer its stamp).
    public static async Task<(string Stamp, IReadOnlyList<string> RecoveryCodes)?> ReplaceRecoveryCodesAsync(IPRODbContext db, TwoStepAccount account,
        string stamp, DateTime now)
    {
        var codes = NewRecoveryCodes();
        var newStamp = NewStamp();
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (await db.Database.ExecuteSqlRawAsync(
                "UPDATE `TwoStepSignIns` SET `Stamp` = {3}, `EmailCodeHash` = NULL, `EmailCodeExpiresAt` = NULL WHERE `AgentUserId` = {0} AND `TeamMemberId` = {1} AND `Stamp` = {2}",
                account.AgentUserId, account.TeamMemberId, stamp, newStamp) == 0)
        {
            await transaction.RollbackAsync();
            return null;
        }
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM `TwoStepRecoveryCodes` WHERE `AgentUserId` = {0} AND `TeamMemberId` = {1}", account.AgentUserId, account.TeamMemberId);
        await InsertRecoveryCodesAsync(db, account, codes, now);
        await transaction.CommitAsync();
        return (newStamp, codes);
    }

    // SuperAdmin's rule for one adviser's account. True when it changed.
    public static async Task<bool> SetRequiredAsync(IPRODbContext db, int agentUserId, bool required, string by, DateTime now) =>
        required
            ? await db.Database.ExecuteSqlRawAsync(
                "INSERT IGNORE INTO `TwoStepRequirements` (`AgentUserId`, `RequiredAt`, `RequiredBy`) VALUES ({0}, {1}, {2})",
                agentUserId, now, Clip(by, 200)) > 0
            : await db.Database.ExecuteSqlRawAsync("DELETE FROM `TwoStepRequirements` WHERE `AgentUserId` = {0}", agentUserId) > 0;

    // ---- one code attempt (5 wrong in a row lock it for 15 minutes) -----------------------------------

    // Takes one attempt: false when the second step is locked (or 5 attempts are already being checked).
    public static async Task<bool> BeginCodeAsync(IPRODbContext db, TwoStepAccount account, DateTime now)
    {
        // A lock that has run out starts a new run of five.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE `TwoStepSignIns` SET `FailedCodeCount` = 0, `CodeLockedUntil` = NULL WHERE `AgentUserId` = {0} AND `TeamMemberId` = {1} AND `CodeLockedUntil` <= {2}",
            account.AgentUserId, account.TeamMemberId, now);
        return await db.Database.ExecuteSqlRawAsync(
            $"UPDATE `TwoStepSignIns` SET `FailedCodeCount` = `FailedCodeCount` + 1 WHERE `AgentUserId` = {{0}} AND `TeamMemberId` = {{1}} AND `CodeLockedUntil` IS NULL AND `FailedCodeCount` < {WrongCodesBeforeLock} AND `WrongCodes` < {WrongCodesBeforeHold}",
            account.AgentUserId, account.TeamMemberId) > 0;
    }

    // The code was wrong: counted since the last right one too. True when this call locked the second step.
    public static async Task<bool> CodeFailedAsync(IPRODbContext db, TwoStepAccount account, DateTime now)
    {
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE `TwoStepSignIns` SET `WrongCodes` = `WrongCodes` + 1 WHERE `AgentUserId` = {0} AND `TeamMemberId` = {1}",
            account.AgentUserId, account.TeamMemberId);
        return await db.Database.ExecuteSqlRawAsync(
            $"UPDATE `TwoStepSignIns` SET `CodeLockedUntil` = {{2}} WHERE `AgentUserId` = {{0}} AND `TeamMemberId` = {{1}} AND `CodeLockedUntil` IS NULL AND (`FailedCodeCount` >= {WrongCodesBeforeLock} OR `WrongCodes` >= {WrongCodesBeforeHold})",
            account.AgentUserId, account.TeamMemberId, now + LockFor) > 0;
    }

    // The code was right: both counts start again (and a lock set by a wrong code sent at the same moment,
    // counted with this attempt's own reservation, ends -- as in SignInLockout.SucceededAsync).
    public static Task CodeSucceededAsync(IPRODbContext db, TwoStepAccount account) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE `TwoStepSignIns` SET `FailedCodeCount` = 0, `WrongCodes` = 0, `CodeLockedUntil` = NULL WHERE `AgentUserId` = {0} AND `TeamMemberId` = {1}",
            account.AgentUserId, account.TeamMemberId);

    // SuperAdmin's Unlock: the 15-minute lock and the hold. True when either was in force.
    public static async Task<bool> ClearCodeLockAsync(IPRODbContext db, TwoStepAccount account, DateTime now)
    {
        var wasLocked = await db.Database.ExecuteSqlRawAsync(
            $"UPDATE `TwoStepSignIns` SET `FailedCodeCount` = 0, `WrongCodes` = 0, `CodeLockedUntil` = NULL WHERE `AgentUserId` = {{0}} AND `TeamMemberId` = {{1}} AND (`CodeLockedUntil` > {{2}} OR `WrongCodes` >= {WrongCodesBeforeHold})",
            account.AgentUserId, account.TeamMemberId, now);
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE `TwoStepSignIns` SET `FailedCodeCount` = 0, `WrongCodes` = 0, `CodeLockedUntil` = NULL WHERE `AgentUserId` = {0} AND `TeamMemberId` = {1}",
            account.AgentUserId, account.TeamMemberId);
        return wasLocked > 0;
    }

    // The code-lock email's own once-a-day (the password lock's is SignInLockout.ClaimNoticeAsync): a
    // password lock that day must not keep this one -- "your password was typed correctly" -- from going.
    public static async Task<bool> ClaimCodeLockNoticeAsync(IPRODbContext db, TwoStepAccount account, DateTime claimedAt)
    {
        claimedAt = new DateTime(claimedAt.Ticks - claimedAt.Ticks % 10, claimedAt.Kind);
        return await db.Database.ExecuteSqlRawAsync(
            "UPDATE `TwoStepSignIns` SET `CodeLockNoticeAt` = {2} WHERE `AgentUserId` = {0} AND `TeamMemberId` = {1} AND (`CodeLockNoticeAt` IS NULL OR `CodeLockNoticeAt` <= {3})",
            account.AgentUserId, account.TeamMemberId, claimedAt, claimedAt - SignInLockout.NoticeAtMostEvery) > 0;
    }

    public static Task ReleaseCodeLockNoticeAsync(IPRODbContext db, TwoStepAccount account, DateTime claimedAt)
    {
        claimedAt = new DateTime(claimedAt.Ticks - claimedAt.Ticks % 10, claimedAt.Kind);
        return db.Database.ExecuteSqlRawAsync(
            "UPDATE `TwoStepSignIns` SET `CodeLockNoticeAt` = NULL WHERE `AgentUserId` = {0} AND `TeamMemberId` = {1} AND `CodeLockNoticeAt` = {2}",
            account.AgentUserId, account.TeamMemberId, claimedAt);
    }

    // ---- the set-up code: proof of the inbox before two-step is turned on ---------------------------------

    public enum SetupCodeResult { Right, Wrong, NoCode }

    // A new set-up code for the email on file (the one before it stops working), or null when one went out
    // less than a minute ago or 10 went out in the last 24 hours. The caller emails it.
    public static async Task<string?> IssueSetupCodeAsync(IPRODbContext db, TwoStepAccount account, DateTime now)
    {
        await db.Database.ExecuteSqlRawAsync(
            "INSERT IGNORE INTO `TwoStepSetupCodes` (`AgentUserId`, `TeamMemberId`, `SentCount`, `WrongCount`) VALUES ({0}, {1}, 0, 0)",
            account.AgentUserId, account.TeamMemberId);
        var code = NewEmailCode();
        var windowStart = now - SetupCodesWindow;
        // Left to right, as in IssueEmailCodeAsync: SentCount is worked out before SentSince moves.
        var issued = await db.Database.ExecuteSqlRawAsync(
            "UPDATE `TwoStepSetupCodes` SET " +
            "`SentCount` = CASE WHEN `SentSince` IS NULL OR `SentSince` <= {2} THEN 1 ELSE `SentCount` + 1 END, " +
            "`SentSince` = CASE WHEN `SentSince` IS NULL OR `SentSince` <= {2} THEN {3} ELSE `SentSince` END, " +
            "`CodeHash` = {4}, `ExpiresAt` = {5}, `SentAt` = {3}, `WrongCount` = 0 " +
            "WHERE `AgentUserId` = {0} AND `TeamMemberId` = {1} " +
            "AND (`SentAt` IS NULL OR `SentAt` <= {6}) " +
            $"AND (`SentSince` IS NULL OR `SentSince` <= {{2}} OR `SentCount` < {SetupCodesPerWindow})",
            account.AgentUserId, account.TeamMemberId, windowStart, now, SetupCodeHash(account, code), now + EmailCodeLifetime, now - EmailCodeEvery);
        return issued > 0 ? code : null;
    }

    // The set-up code, used once, before it runs out; 5 tries and it is spent (ask for a new one). A try is
    // taken BEFORE the code is compared, as BeginCodeAsync does, so tries sent together get 5, not 5 each.
    public static async Task<SetupCodeResult> UseSetupCodeAsync(IPRODbContext db, TwoStepAccount account, string? typed, DateTime now)
    {
        if (await db.Database.ExecuteSqlRawAsync(
                $"UPDATE `TwoStepSetupCodes` SET `WrongCount` = `WrongCount` + 1 WHERE `AgentUserId` = {{0}} AND `TeamMemberId` = {{1}} AND `CodeHash` IS NOT NULL AND `ExpiresAt` > {{2}} AND `WrongCount` < {WrongCodesBeforeLock}",
                account.AgentUserId, account.TeamMemberId, now) == 0)
            return SetupCodeResult.NoCode;
        var code = NormalizeDigits(typed);
        return code != null && await db.Database.ExecuteSqlRawAsync(
                "UPDATE `TwoStepSetupCodes` SET `CodeHash` = NULL, `ExpiresAt` = NULL WHERE `AgentUserId` = {0} AND `TeamMemberId` = {1} AND `CodeHash` = {2} AND `ExpiresAt` > {3}",
                account.AgentUserId, account.TeamMemberId, SetupCodeHash(account, code), now) > 0
            ? SetupCodeResult.Right
            : SetupCodeResult.Wrong;
    }

    private static string SetupCodeHash(TwoStepAccount account, string code) => EmailCodeHash($"setup:{account.AgentUserId}:{account.TeamMemberId}", code);

    // ---- using a code up ---------------------------------------------------------------------------------

    // An authenticator code's step, taken once: false when this step (or a later one) was used already.
    public static async Task<bool> ClaimAppStepAsync(IPRODbContext db, TwoStepAccount account, string stamp, long step) =>
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE `TwoStepSignIns` SET `LastUsedStep` = {3} WHERE `AgentUserId` = {0} AND `TeamMemberId` = {1} AND `Stamp` = {2} AND `LastUsedStep` < {3}",
            account.AgentUserId, account.TeamMemberId, stamp, step) > 0;

    // A new emailed code (the one before it stops working), or null when one went out less than a minute
    // ago or 5 went out this hour. The caller emails it.
    public static async Task<string?> IssueEmailCodeAsync(IPRODbContext db, TwoStepAccount account, string stamp, DateTime now)
    {
        var code = NewEmailCode();
        var hourAgo = now - TimeSpan.FromHours(1);
        // MySQL assigns left to right, each assignment seeing the ones before it: EmailCodesSent is worked
        // out from EmailCodesSince before EmailCodesSince itself is moved.
        var issued = await db.Database.ExecuteSqlRawAsync(
            "UPDATE `TwoStepSignIns` SET " +
            "`EmailCodesSent` = CASE WHEN `EmailCodesSince` IS NULL OR `EmailCodesSince` <= {3} THEN 1 ELSE `EmailCodesSent` + 1 END, " +
            "`EmailCodesSince` = CASE WHEN `EmailCodesSince` IS NULL OR `EmailCodesSince` <= {3} THEN {4} ELSE `EmailCodesSince` END, " +
            "`EmailCodeHash` = {5}, `EmailCodeExpiresAt` = {6}, `EmailCodeSentAt` = {4} " +
            "WHERE `AgentUserId` = {0} AND `TeamMemberId` = {1} AND `Stamp` = {2} " +
            "AND (`EmailCodeSentAt` IS NULL OR `EmailCodeSentAt` <= {7}) " +
            $"AND (`EmailCodesSince` IS NULL OR `EmailCodesSince` <= {{3}} OR `EmailCodesSent` < {EmailCodesPerHour})",
            account.AgentUserId, account.TeamMemberId, stamp, hourAgo, now, EmailCodeHash(stamp, code), now + EmailCodeLifetime, now - EmailCodeEvery);
        return issued > 0 ? code : null;
    }

    // The emailed code, used once and only before it runs out.
    public static async Task<bool> UseEmailCodeAsync(IPRODbContext db, TwoStepAccount account, string stamp, string? typed, DateTime now)
    {
        var code = NormalizeDigits(typed);
        if (code == null) return false;
        return await db.Database.ExecuteSqlRawAsync(
            "UPDATE `TwoStepSignIns` SET `EmailCodeHash` = NULL, `EmailCodeExpiresAt` = NULL " +
            "WHERE `AgentUserId` = {0} AND `TeamMemberId` = {1} AND `Stamp` = {2} AND `EmailCodeHash` = {3} AND `EmailCodeExpiresAt` > {4}",
            account.AgentUserId, account.TeamMemberId, stamp, EmailCodeHash(stamp, code), now) > 0;
    }

    // A recovery code, used once. Every unused code is checked (each a slow hash), whatever matched first.
    public static async Task<bool> UseRecoveryCodeAsync(IPRODbContext db, TwoStepAccount account, string? typed, DateTime now)
    {
        var code = NormalizeRecoveryCode(typed);
        if (code == null) return false;
        var unused = await db.TwoStepRecoveryCodes.AsNoTracking()
            .Where(r => r.AgentUserId == account.AgentUserId && r.TeamMemberId == account.TeamMemberId && r.UsedAt == null)
            .Select(r => new { r.Id, r.CodeHash })
            .ToListAsync();
        int? match = null;
        foreach (var row in unused)
            if (RecoveryCodeMatches(row.CodeHash, code) && match == null) match = row.Id;
        if (match == null) return false;
        return await db.Database.ExecuteSqlRawAsync(
            "UPDATE `TwoStepRecoveryCodes` SET `UsedAt` = {1} WHERE `Id` = {0} AND `UsedAt` IS NULL", match.Value, now) > 0;
    }

    // ---- the codes themselves ---------------------------------------------------------------------------

    // Letters and digits that cannot be mistaken for one another (no i, l, o, 0, 1): 31 of them, ten to a
    // code, shown as two groups of five.
    private const string RecoveryAlphabet = "23456789abcdefghjkmnpqrstuvwxyz";
    private const int RecoveryCodeLength = 10;
    private const int RecoveryHashIterations = 10_000;

    public static IReadOnlyList<string> NewRecoveryCodes() =>
        Enumerable.Range(0, RecoveryCodeCount).Select(_ => NewRecoveryCode()).ToList();

    private static string NewRecoveryCode()
    {
        var chars = new char[RecoveryCodeLength];
        for (var i = 0; i < chars.Length; i++) chars[i] = RecoveryAlphabet[RandomNumberGenerator.GetInt32(RecoveryAlphabet.Length)];
        return new string(chars, 0, 5) + "-" + new string(chars, 5, 5);
    }

    // The ten letters, lower-case, without the dash or spaces; null for anything that cannot be one.
    public static string? NormalizeRecoveryCode(string? typed)
    {
        if (typed == null) return null;
        var letters = new StringBuilder();
        foreach (var c in typed.Trim().ToLowerInvariant())
        {
            if (c is ' ' or '-') continue;
            if (RecoveryAlphabet.IndexOf(c) < 0) return null;
            letters.Append(c);
        }
        return letters.Length == RecoveryCodeLength ? letters.ToString() : null;
    }

    public static bool LooksLikeRecoveryCode(string? typed) => NormalizeRecoveryCode(typed) != null;

    // "v1:" + salt + ":" + PBKDF2-SHA256 of the normalized code (10,000 rounds): a stolen table does not
    // give the codes back.
    public static string HashRecoveryCode(string code)
    {
        var normalized = NormalizeRecoveryCode(code) ?? throw new ArgumentException("Not a recovery code", nameof(code));
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(normalized), salt, RecoveryHashIterations, HashAlgorithmName.SHA256, 32);
        return "v1:" + Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);
    }

    private static bool RecoveryCodeMatches(string stored, string normalized)
    {
        var parts = stored.Split(':');
        if (parts.Length != 3 || parts[0] != "v1") return false;
        byte[] salt, expected;
        try { salt = Convert.FromBase64String(parts[1]); expected = Convert.FromBase64String(parts[2]); }
        catch (FormatException) { return false; }
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(normalized), salt, RecoveryHashIterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string NewEmailCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    // The emailed code is only good for 10 minutes; its hash is salted with the row's stamp.
    public static string EmailCodeHash(string salt, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(salt + ":" + code))).ToLowerInvariant();

    // Six digits, with any spaces or dashes a person types between them taken out (as Totp.NormalizeCode).
    public static string? NormalizeDigits(string? typed)
    {
        if (typed == null) return null;
        var digits = new StringBuilder();
        foreach (var c in typed)
        {
            if (c is ' ' or '-') continue;
            if (c is < '0' or > '9') return null;
            digits.Append(c);
        }
        return digits.Length == 6 ? digits.ToString() : null;
    }

    public static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    // ---- helpers ----------------------------------------------------------------------------------------

    private static async Task<bool> DeleteRowsAsync(IPRODbContext db, TwoStepAccount account)
    {
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM `TwoStepRecoveryCodes` WHERE `AgentUserId` = {0} AND `TeamMemberId` = {1}", account.AgentUserId, account.TeamMemberId);
        return await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM `TwoStepSignIns` WHERE `AgentUserId` = {0} AND `TeamMemberId` = {1}", account.AgentUserId, account.TeamMemberId) > 0;
    }

    private static async Task InsertRecoveryCodesAsync(IPRODbContext db, TwoStepAccount account, IEnumerable<string> codes, DateTime now)
    {
        foreach (var code in codes)
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO `TwoStepRecoveryCodes` (`AgentUserId`, `TeamMemberId`, `CodeHash`, `CreatedAt`) VALUES ({0}, {1}, {2}, {3})",
                account.AgentUserId, account.TeamMemberId, HashRecoveryCode(code), now);
    }

    private static string Clip(string? value, int max) => (value ?? string.Empty).Length <= max ? value ?? string.Empty : value![..max];
}

// Whose two-step it is: an adviser (TeamMemberId 0), or one of their team members.
public readonly record struct TwoStepAccount(int AgentUserId, int TeamMemberId)
{
    public static TwoStepAccount Adviser(int agentUserId) => new(agentUserId, 0);
    public static TwoStepAccount Member(int agentUserId, int teamMemberId) => new(agentUserId, teamMemberId);

    public bool IsTeamMember => TeamMemberId != 0;

    // The same person as 583's lock knows them (the lock email, the daily email limit).
    public SignInAccount SignIn => IsTeamMember
        ? new SignInAccount(SignInAccountKind.TeamMember, TeamMemberId)
        : new SignInAccount(SignInAccountKind.Adviser, AgentUserId);
}
