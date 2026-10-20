namespace IPRO.Entities;

// 593 (2026-10-20): two-step sign-in for the adviser portal. After the password, a second proof: a code
// from an authenticator app, or a code emailed to the person. One row per person who has it on -- the
// adviser (TeamMemberId 0) or one of their team members -- and no row for anyone who does not.
//
// Its own tables, not columns on AgentUsers or TeamMembers, for AgentFollowUpReminder's reason: a column
// the model expects and the table lacks fails every sign-in. Written only by TwoStepStore's statements
// (each one WHERE-guarded, as SignInLockout's are); nothing loads a row and saves it back.
public class TwoStepSignIn
{
    public int Id { get; set; }
    // The adviser's account the person signs in to; for a team member, their adviser.
    public int AgentUserId { get; set; }
    // 0 = the adviser themself.
    public int TeamMemberId { get; set; }
    public string Method { get; set; } = TwoStepMethods.App;
    // The authenticator app's secret, encrypted (ASP.NET Data Protection); null for the email method.
    public string? ProtectedSecret { get; set; }
    // New random letters each time two-step is turned on. A signed-in session carries the stamp it was
    // made with; a different one here (turned off and on again, reset by SuperAdmin) ends that session.
    public string Stamp { get; set; } = string.Empty;
    public DateTime EnabledAt { get; set; }
    // The authenticator step last accepted: an app code at or before it is refused (no code works twice).
    public long LastUsedStep { get; set; }
    // Wrong codes in a row, and the lock they set (5 lock it for 15 minutes, as 583 does for passwords).
    public int FailedCodeCount { get; set; }
    public DateTime? CodeLockedUntil { get; set; }
    // Wrong codes since the last right one: at TwoStepStore.WrongCodesBeforeHold the second step stays
    // locked until SuperAdmin unlocks it. And the code-lock email's own once-a-day.
    public int WrongCodes { get; set; }
    public DateTime? CodeLockNoticeAt { get; set; }
    // The emailed code: its hash (never the code), when it stops working, and how many went out this hour.
    public string? EmailCodeHash { get; set; }
    public DateTime? EmailCodeExpiresAt { get; set; }
    public DateTime? EmailCodeSentAt { get; set; }
    public int EmailCodesSent { get; set; }
    public DateTime? EmailCodesSince { get; set; }
}

// The code emailed while two-step is being set up (either way): proof that the person setting it up can
// read the account's inbox, not only knows the password. One row per person, kept until it is on.
public class TwoStepSetupCode
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public int TeamMemberId { get; set; }
    public string? CodeHash { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? SentAt { get; set; }
    public int SentCount { get; set; }
    public DateTime? SentSince { get; set; }
    public int WrongCount { get; set; }
}

// Ten one-time codes for when the phone is lost, shown once at set-up. Stored as a salted, slow hash.
public class TwoStepRecoveryCode
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public int TeamMemberId { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? UsedAt { get; set; }
}

// SuperAdmin requires two-step for one adviser's account: the adviser and their team members must set
// it up before using the portal. (A package-wide rule can join this later: TwoStepStore.IsRequiredAsync
// is the one place that decides.)
public class TwoStepRequirement
{
    public int AgentUserId { get; set; }
    public DateTime RequiredAt { get; set; }
    public string RequiredBy { get; set; } = string.Empty;
}

public static class TwoStepMethods
{
    public const string App = "App";
    public const string Email = "Email";

    public static bool IsKnown(string? method) => method is App or Email;
}
