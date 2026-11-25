using System;

namespace IPRO.Entities;

// 678 (audit 2026-11-24 D-4, the owner 2026-11-25 "Double opt-in YES"): a website sign-up waits for the person to
// confirm it from their own inbox. Until then a newsletter sign-up does not subscribe them, and the Did You Know
// articles they asked for are held. Anyone could type anyone's address into a form; only the address's owner can
// press the button in the email.
public class EmailSignupConfirmation
{
    public const int TokenHashLength = 64;

    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public int ClientId { get; set; }
    public string Kind { get; set; } = EmailSignupKinds.Newsletter;
    // SHA-256 of the token in the email, hex. The token itself is never stored.
    public string TokenHash { get; set; } = string.Empty;
    // The website the form was on, for the email's wording.
    public string SourceDomain { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
}

public static class EmailSignupKinds
{
    public const string Newsletter = "newsletter";
    public const string DidYouKnow = "didyouknow";
}
