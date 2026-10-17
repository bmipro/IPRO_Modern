using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.DataAccess;

// 583 (2026-10-16), the owner's rule: 5 wrong passwords in a row lock that account's sign-in for 15
// minutes, and the holder gets one email when it locks. A sign-in that works starts the count again; a
// new password (the forgot-password reset, a reset by the account's owner or SuperAdmin) unlocks at
// once; SuperAdmin can see and clear a lock. Every place a password is checked counts: the three sign-in
// pages and the current-password check on Change Password (a session left open must not become a
// way to try passwords without limit -- that screen had no limit at all).
//
// Each step is ONE statement whose WHERE says when it may happen, so two requests at the same moment
// cannot both pass a check before either writes. The count goes up BEFORE the password is checked
// (BeginAsync), and only while the account is unlocked and under 5: a burst of guesses sent together
// gets 5 checked, not 5 each. A wrong password then tries to lock (FailedAsync, true for the one call
// that locked it: one lock, one email, one log line); a right one puts the count back to 0. Where one name
// is several accounts (a client of two advisers), a password counts as wrong for each account it misses.
//
// A name that is no account counts the same way, in SignInNameLocks (a hash of the name, per sign-in
// page), so its fifth wrong password shows the same lock sentence: the lock must not tell a stranger
// which names exist. The sign-in pages also answer every refusal no sooner than PadFailure after the
// request began, so the work an existing account costs (a password hash, a few more statements) does
// not show in the time either.
public static class SignInLockout
{
    public const int WrongPasswordsBeforeLock = 5;
    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);
    // One lock email at most this often, however often a bot locks the account again (every 15 minutes
    // would be 96 a day).
    public static readonly TimeSpan NoticeAtMostEvery = TimeSpan.FromHours(24);
    public static readonly TimeSpan ForgetNamesAfter = TimeSpan.FromDays(30);
    public static readonly TimeSpan PadFailure = TimeSpan.FromMilliseconds(1500);

    public static bool IsLocked(DateTime? lockedUntil, DateTime now) => lockedUntil is { } until && until > now;

    // ---- one sign-in attempt ------------------------------------------------------------------------

    // Checks a password against the accounts a typed name belongs to, counting as the rule says.
    // `candidates` are the accounts the name finds on this sign-in page (usually one; a client with two
    // advisers has a row at each); none means the name is no account. A locked account's password is
    // never checked: during a lock the right password and a wrong one get the same answer.
    public static async Task<SignInAttempt> TryAsync(IPRODbContext db, SignInDoor door, string? name,
        IReadOnlyList<SignInCandidate> candidates, DateTime now)
    {
        if (candidates.Count == 0)
        {
            var key = NameKey(door, name);
            await ForgetOldNamesAsync(db, now);
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO `SignInNameLocks` (`NameKey`, `FailedSignInCount`, `SignInLockedUntil`, `LastFailedAt`) " +
                "VALUES ({0}, 0, NULL, {1}) ON DUPLICATE KEY UPDATE `NameKey` = `NameKey`", key, now);
            if (!await BeginAsync(db, Target.Name(key), now)) return SignInAttempt.Refused(Array.Empty<SignInAccount>(), unknownLockedNow: false);
            var lockedNow = await FailedAsync(db, Target.Name(key), now);
            return SignInAttempt.Refused(Array.Empty<SignInAccount>(), unknownLockedNow: lockedNow, locked: lockedNow);
        }

        var open = new List<SignInCandidate>();
        var anyLocked = false;
        foreach (var candidate in candidates)
        {
            if (await BeginAsync(db, Target.Of(candidate.Account), now)) open.Add(candidate);
            else anyLocked = true;
        }

        var matched = open.Where(c => c.PasswordMatches()).ToList();
        var ended = new List<SignInAccount>();
        foreach (var c in matched)
            if (await SucceededAsync(db, c.Account)) ended.Add(c.Account);

        // A password that missed an account is a wrong one for that account, even when it opened another
        // account of the same name. (The 583 review: an adviser can give their own client the email of
        // another adviser's client and a password of their choosing; were a miss not counted when their
        // own row matched, every guess at the other client's password would be free.)
        var newlyLocked = new List<SignInAccount>();
        foreach (var c in open.Except(matched))
            if (await FailedAsync(db, Target.Of(c.Account), now)) newlyLocked.Add(c.Account);

        return matched.Count > 0
            ? SignInAttempt.Allowed(matched.Select(c => c.Account).ToList(), newlyLocked, ended)
            : SignInAttempt.Refused(newlyLocked, unknownLockedNow: false, locked: anyLocked || newlyLocked.Count > 0);
    }

    // The name as an account is found by it: trimmed, and the same letters whatever their case -- compared
    // here, not by the database, whose collation would also take "jose" with an accent for "jose". A name the database
    // matches but this does not is no account, and is counted as the name it is (NameKey), so no spelling
    // reaches an account's count unless it also reaches the same name's.
    public static bool SameName(string? stored, string? typed) =>
        string.Equals(Normalize(stored), Normalize(typed), StringComparison.Ordinal);

    private static string Normalize(string? name) => (name ?? string.Empty).Trim().ToLowerInvariant();

    // Answers a refused sign-in no sooner than PadFailure after `startedTimestamp` (Stopwatch.GetTimestamp
    // at the start of the request), whatever the refusal cost to work out.
    public static Task PadAsync(long startedTimestamp)
    {
        var left = PadFailure - Stopwatch.GetElapsedTime(startedTimestamp);
        return left > TimeSpan.Zero ? Task.Delay(left) : Task.CompletedTask;
    }

    // ---- the steps ----------------------------------------------------------------------------------

    // Takes one attempt: false when the account is locked (or 5 attempts are already being checked).
    public static Task<bool> BeginAsync(IPRODbContext db, SignInAccount account, DateTime now) =>
        BeginAsync(db, Target.Of(account), now);

    // The password was right: the count starts again. A lock found now was set while this attempt was
    // being checked (BeginAsync took the attempt only while there was none) -- by a wrong password sent
    // at the same moment, counted with this attempt's own reservation. The right password ends it.
    // True when it ended such a lock (logged, as the lock was).
    public static async Task<bool> SucceededAsync(IPRODbContext db, SignInAccount account)
    {
        var t = Target.Of(account);
        var endedLock = await db.Database.ExecuteSqlRawAsync(
            $"UPDATE `{t.Table}` SET `FailedSignInCount` = 0, `SignInLockedUntil` = NULL WHERE `{t.KeyColumn}` = {{0}} AND `SignInLockedUntil` IS NOT NULL", t.Key) > 0;
        await db.Database.ExecuteSqlRawAsync(
            $"UPDATE `{t.Table}` SET `FailedSignInCount` = 0 WHERE `{t.KeyColumn}` = {{0}}", t.Key);
        return endedLock;
    }

    // The password was wrong. True when this call locked the account.
    public static Task<bool> FailedAsync(IPRODbContext db, SignInAccount account, DateTime now) =>
        FailedAsync(db, Target.Of(account), now);

    // A new password, or SuperAdmin's Unlock: no lock, no count. True when a lock was in force.
    public static async Task<bool> ClearAsync(IPRODbContext db, SignInAccount account, DateTime now)
    {
        var t = Target.Of(account);
        var wasLocked = await db.Database.ExecuteSqlRawAsync(
            $"UPDATE `{t.Table}` SET `FailedSignInCount` = 0, `SignInLockedUntil` = NULL WHERE `{t.KeyColumn}` = {{0}} AND `SignInLockedUntil` > {{1}}", t.Key, now);
        await db.Database.ExecuteSqlRawAsync(
            $"UPDATE `{t.Table}` SET `FailedSignInCount` = 0, `SignInLockedUntil` = NULL WHERE `{t.KeyColumn}` = {{0}}", t.Key);
        return wasLocked > 0;
    }

    // The lock email may go: true for one caller per NoticeAtMostEvery. `claimedAt` is what to hand to
    // ReleaseNoticeAsync when the send fails, so the next lock tries again.
    public static async Task<bool> ClaimNoticeAsync(IPRODbContext db, SignInAccount account, DateTime claimedAt)
    {
        if (account.Kind == SignInAccountKind.Admin) return false;   // no email address on an admin account
        claimedAt = WholeMicroseconds(claimedAt);
        var t = Target.Of(account);
        return await db.Database.ExecuteSqlRawAsync(
            $"UPDATE `{t.Table}` SET `SignInLockNoticeAt` = {{1}} WHERE `{t.KeyColumn}` = {{0}} AND (`SignInLockNoticeAt` IS NULL OR `SignInLockNoticeAt` <= {{2}})",
            t.Key, claimedAt, claimedAt - NoticeAtMostEvery) > 0;
    }

    public static Task ReleaseNoticeAsync(IPRODbContext db, SignInAccount account, DateTime claimedAt)
    {
        claimedAt = WholeMicroseconds(claimedAt);
        var t = Target.Of(account);
        return db.Database.ExecuteSqlRawAsync(
            $"UPDATE `{t.Table}` SET `SignInLockNoticeAt` = NULL WHERE `{t.KeyColumn}` = {{0}} AND `SignInLockNoticeAt` = {{1}}", t.Key, claimedAt);
    }

    // Locks reach the container log, which keeps Warning and up. The name typed is never logged (it is
    // sometimes a password typed into the wrong box); the account's id is.
    public static void LogLocks(ILogger logger, SignInAttempt attempt, SignInDoor door, string? visitor)
    {
        foreach (var account in attempt.LockedNow)
            logger.LogWarning("Sign-in locked for 15 minutes after 5 wrong passwords in a row: {Kind} {Id} on the {Door} sign-in, last try from {Visitor}",
                account.Kind, account.Id, door, visitor ?? "unknown");
        foreach (var account in attempt.LockEndedNow)
            LogCleared(logger, account, "the right password, checked at the same moment as the wrong one that locked it");
        if (attempt.UnknownNameLockedNow)
            logger.LogWarning("Sign-in locked for 15 minutes after 5 wrong passwords in a row: a name that is no account, on the {Door} sign-in, last try from {Visitor}",
                door, visitor ?? "unknown");
    }

    public static void LogCleared(ILogger logger, SignInAccount account, string how) =>
        logger.LogWarning("Sign-in lock cleared ({How}): {Kind} {Id}", how, account.Kind, account.Id);

    // datetime(6) keeps whole microseconds; a value compared with what was stored must be cut the same way.
    private static DateTime WholeMicroseconds(DateTime value) => new(value.Ticks - value.Ticks % 10, value.Kind);

    // The page and the name as typed, never the name itself: a hash of it, lower-cased and trimmed.
    public static string NameKey(SignInDoor door, string? name)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(name)))).ToLowerInvariant();
        return door switch
        {
            SignInDoor.Portal => "portal:",
            SignInDoor.ClientPortal => "client:",
            SignInDoor.Admin => "admin:",
            _ => throw new ArgumentOutOfRangeException(nameof(door))
        } + hash;
    }

    private static async Task<bool> BeginAsync(IPRODbContext db, Target t, DateTime now)
    {
        // A lock that has run out starts a new run of five.
        await db.Database.ExecuteSqlRawAsync(
            $"UPDATE `{t.Table}` SET `FailedSignInCount` = 0, `SignInLockedUntil` = NULL WHERE `{t.KeyColumn}` = {{0}} AND `SignInLockedUntil` <= {{1}}", t.Key, now);
        var lastFailed = t.IsName ? ", `LastFailedAt` = {1}" : string.Empty;
        return await db.Database.ExecuteSqlRawAsync(
            $"UPDATE `{t.Table}` SET `FailedSignInCount` = `FailedSignInCount` + 1{lastFailed} WHERE `{t.KeyColumn}` = {{0}} AND `SignInLockedUntil` IS NULL AND `FailedSignInCount` < {WrongPasswordsBeforeLock}",
            t.Key, now) > 0;
    }

    private static async Task<bool> FailedAsync(IPRODbContext db, Target t, DateTime now) =>
        await db.Database.ExecuteSqlRawAsync(
            $"UPDATE `{t.Table}` SET `SignInLockedUntil` = {{1}} WHERE `{t.KeyColumn}` = {{0}} AND `SignInLockedUntil` IS NULL AND `FailedSignInCount` >= {WrongPasswordsBeforeLock}",
            t.Key, now + LockFor) > 0;

    // A name tried once a month ago and never again is not worth keeping (the table would otherwise grow
    // with every name a scanner makes up). A little at a time, on the path that adds rows. Housekeeping
    // only: should it collide with the count of a month-old name tried at that moment (a deadlock), the
    // sign-in goes on and the next one tidies up.
    private static async Task ForgetOldNamesAsync(IPRODbContext db, DateTime now)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM `SignInNameLocks` WHERE `LastFailedAt` < {0} AND (`SignInLockedUntil` IS NULL OR `SignInLockedUntil` < {1}) LIMIT 100",
                now - ForgetNamesAfter, now);
        }
        catch (MySqlConnector.MySqlException ex)
            when (ex.ErrorCode is MySqlConnector.MySqlErrorCode.LockDeadlock or MySqlConnector.MySqlErrorCode.LockWaitTimeout)
        {
        }
    }

    // The table and key a statement names. Table names come from this switch only, never from input.
    private readonly record struct Target(string Table, string KeyColumn, object Key, bool IsName)
    {
        public static Target Of(SignInAccount account) => new(account.Kind switch
        {
            SignInAccountKind.Adviser => "AgentUsers",
            SignInAccountKind.TeamMember => "TeamMembers",
            SignInAccountKind.Client => "Clients",
            SignInAccountKind.Admin => "AdminUsers",
            _ => throw new ArgumentOutOfRangeException(nameof(account))
        }, "Id", account.Id, false);

        public static Target Name(string key) => new("SignInNameLocks", "NameKey", key, true);
    }
}

public enum SignInAccountKind { Adviser, TeamMember, Client, Admin }

// The sign-in page a name was typed on: a name means a different account on each.
public enum SignInDoor { Portal, ClientPortal, Admin }

public readonly record struct SignInAccount(SignInAccountKind Kind, int Id);

public sealed record SignInCandidate(SignInAccount Account, Func<bool> PasswordMatches);

public sealed class SignInAttempt
{
    // The accounts whose password this was (the count is back to 0). Empty = refused.
    public IReadOnlyList<SignInAccount> Matched { get; private init; } = Array.Empty<SignInAccount>();
    // Refused because of a lock (one in force, or one this attempt set): show the lock sentence.
    public bool Locked { get; private init; }
    // The accounts this attempt locked: one email and one log line each (on a sign-in that worked too:
    // the same email's row at another adviser that this password missed).
    public IReadOnlyList<SignInAccount> LockedNow { get; private init; } = Array.Empty<SignInAccount>();
    // Locks this attempt's right password ended (logged).
    public IReadOnlyList<SignInAccount> LockEndedNow { get; private init; } = Array.Empty<SignInAccount>();
    // This attempt locked a name that is no account (logged, nobody to email).
    public bool UnknownNameLockedNow { get; private init; }

    public bool Succeeded => Matched.Count > 0;

    // `lockedNow`: another account of the same name that this password missed, and locked. `lockEnded`:
    // a lock this right password ended (SucceededAsync).
    public static SignInAttempt Allowed(IReadOnlyList<SignInAccount> matched, IReadOnlyList<SignInAccount>? lockedNow = null,
        IReadOnlyList<SignInAccount>? lockEnded = null) =>
        new() { Matched = matched, LockedNow = lockedNow ?? Array.Empty<SignInAccount>(), LockEndedNow = lockEnded ?? Array.Empty<SignInAccount>() };

    public static SignInAttempt Refused(IReadOnlyList<SignInAccount> lockedNow, bool unknownLockedNow, bool locked = true) =>
        new() { LockedNow = lockedNow, UnknownNameLockedNow = unknownLockedNow, Locked = locked };
}
