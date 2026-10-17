namespace IPRO.Entities;

// 583 (2026-10-16): wrong passwords typed with a name that is no account, counted like an account's
// so the fifth one shows the same lock sentence. The name itself is not kept: NameKey is the sign-in
// page plus a SHA-256 of the name as typed (lower-cased, trimmed). Forgotten 30 days after the last one.
public class SignInNameLock
{
    public string NameKey { get; set; } = string.Empty;
    public int FailedSignInCount { get; set; }
    public DateTime? SignInLockedUntil { get; set; }
    public DateTime LastFailedAt { get; set; } = DateTime.UtcNow;
}
