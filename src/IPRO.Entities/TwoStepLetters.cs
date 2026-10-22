using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace IPRO.Entities;

// 593 (2026-10-20): iPro's own emails about two-step sign-in, to the person who signs in (an adviser or a
// team member), in the house style of the password-reset and sign-in-lock emails. Shared by the portal
// (codes, turned on/off, a team member reset by their adviser, a recovery code used) and SuperAdmin (a
// reset by iPro support), so both say the same thing the same way.
public static class TwoStepLetters
{
    // The number on every public page (IPRO.Web PlatformContact.SupportPhone; a test keeps them equal).
    public const string SupportPhone = "1-416-363-2220";

    public sealed record Letter(string To, string ToName, string Subject, string Html);

    // Who the email is to, and whose account it is about.
    public sealed record Person(string Email, string FullName, string? FirstName, bool IsTeamMember, string OwnerName, string BusinessName);

    public static Person ForAdviser(AgentUser agent) =>
        new(agent.Email.Trim(), $"{agent.FirstName} {agent.LastName}".Trim(), agent.FirstName, false,
            $"{agent.FirstName} {agent.LastName}".Trim(), AdviserSender.BusinessName(agent));

    public static Person ForTeamMember(TeamMember member, AgentUser? owner) =>
        new(member.Email.Trim(), member.FullName?.Trim() ?? string.Empty, FirstWord(member.FullName), true,
            owner == null ? string.Empty : $"{owner.FirstName} {owner.LastName}".Trim(), AdviserSender.BusinessName(owner));

    public const string CodeSubject = "Your iPro sign-in code";
    public const string SetupCodeSubject = "Your code to turn on two-step sign-in";

    // The emailed code at sign-in (the email method), and at set-up.
    public static Letter Code(Person person, string code, bool forSetup)
    {
        var paragraphs = forSetup
            ? new[]
            {
                "Here is the code to finish turning on two-step sign-in for your iPro account:",
                Big(code),
                "It works once, for the next 10 minutes. If you did not ask for it, you can ignore this email: nothing changes."
            }
            : new[]
            {
                "Here is your code to finish signing in to iPro:",
                Big(code),
                "It works once, for the next 10 minutes. Never share it: iPro will never ask you for it.",
                person.IsTeamMember
                    ? $"If you are not signing in right now, someone has your password. Ask {Owner(person)} to reset it on the My Team page."
                    : "If you are not signing in right now, someone has your password: reset it now with \"Forgot password?\" on the sign-in page, and choose one you don't use anywhere else."
            };
        return Make(person, forSetup ? SetupCodeSubject : CodeSubject, paragraphs);
    }

    public const string TurnedOnSubject = "Two-step sign-in is now on for your iPro account";

    public static Letter TurnedOn(Person person, string method) => Make(person, TurnedOnSubject, new[]
    {
        method == TwoStepMethods.Email
            ? "Two-step sign-in is now on for your iPro sign-in: after your password, iPro will email you a code."
            : "Two-step sign-in is now on for your iPro sign-in: after your password, you will type the code your authenticator app shows.",
        "Keep your recovery codes somewhere safe. Each one gets you in once if you cannot get a code.",
        // Turning it on takes a browser where they were signed in and a code from their inbox, not the password.
        person.IsTeamMember
            ? $"If you did not turn it on, someone used a browser where you were signed in, and your email: ask {Owner(person)} to reset your two-step sign-in and your password on the My Team page right away, and change your email's password."
            : $"If you did not turn it on, someone used a browser where you were signed in, and your email: call iPro support at {SupportPhone} right away (they can reset it), and change your email's password."
    });

    public const string MemberTurnedOnSubject = "A team member turned on two-step sign-in";

    // To the adviser: one of their team members turned it on (a session and the member's inbox can do that).
    public static Letter MemberTurnedOn(Person adviser, string memberName, string memberEmail) => Make(adviser, MemberTurnedOnSubject, new[]
    {
        $"{WebUtility.HtmlEncode(memberName)} ({WebUtility.HtmlEncode(memberEmail)}) turned on two-step sign-in for their sign-in to {Business(adviser)}. Nothing for you to do.",
        "If they did not, reset their two-step sign-in and their password on the My Team page (the shield and key icons)."
    });

    public const string TurnedOffSubject = "Two-step sign-in was turned off for your iPro account";

    public static Letter TurnedOff(Person person) => Make(person, TurnedOffSubject, new[]
    {
        "Two-step sign-in was just turned off for your iPro sign-in. From now on your password alone signs you in.",
        "You can turn it on again at any time under Two-step sign-in in the portal.",
        Unexpected(person)
    });

    public const string ResetSubject = "Two-step sign-in was reset for your iPro account";

    // `byOwner`: an adviser reset their team member's; otherwise iPro support reset it.
    public static Letter Reset(Person person, bool byOwner) => Make(person, ResetSubject, new[]
    {
        byOwner
            ? $"{Owner(person)} reset two-step sign-in for your sign-in to {Business(person)}. The next time you sign in, your password is all you need."
            : "iPro support reset two-step sign-in for your iPro sign-in. The next time you sign in, your password is all you need.",
        "Set two-step sign-in up again under Two-step sign-in in the portal. If it is required for your account, you will be asked to straight away.",
        byOwner
            ? $"If you did not ask for this, tell {Owner(person)} right away."
            : $"If you did not ask for this, call iPro support right away at {SupportPhone}."
    });

    public const string RecoveryCodeUsedSubject = "A recovery code was used to sign in to iPro";

    public static Letter RecoveryCodeUsed(Person person, int left) => Make(person, RecoveryCodeUsedSubject, new[]
    {
        $"One of your recovery codes was just used to sign in to your iPro account. Each code works once; you have {left} left.",
        left <= 3
            ? "You are running low: make new ones under Two-step sign-in in the portal (the old ones then stop working)."
            : "You can make new ones at any time under Two-step sign-in in the portal (the old ones then stop working).",
        Unexpected(person)
    });

    public const string EmailChangedSubject = "The login email of your iPro account was changed";

    // To the OLD address: from now on reset links and emailed codes go to the new one.
    public static Letter EmailChanged(Person person, string oldEmail)
    {
        var letter = Make(person, EmailChangedSubject, new[]
        {
            $"The login email of your iPro account was just changed from this address to {WebUtility.HtmlEncode(MaskedEmail(person.Email))}. Password resets and emailed sign-in codes now go there.",
            $"If you did not do this, call iPro support right away at {SupportPhone}."
        });
        return letter with { To = oldEmail.Trim() };
    }

    private static string MaskedEmail(string email)
    {
        var at = email.IndexOf('@');
        return at <= 0 ? "a new address" : email[0] + "***" + email[at..];
    }

    // ---- the letter ------------------------------------------------------------------------------------

    private static string Unexpected(Person person) => person.IsTeamMember
        ? $"If this was not you, someone has your password: ask {Owner(person)} to reset it on the My Team page right away."
        : $"If this was not you, someone has your password: reset it now with \"Forgot password?\" on the sign-in page, and call iPro support at {SupportPhone}.";

    private static string Owner(Person person) =>
        person.OwnerName.Length == 0 ? "the account owner" : WebUtility.HtmlEncode(person.OwnerName);

    private static string Business(Person person) =>
        person.BusinessName.Length == 0 ? "the iPro account" : $"{WebUtility.HtmlEncode(person.BusinessName)}'s iPro account";

    private static string Big(string code) =>
        $"""<span style="display:inline-block;font-size:28px;font-weight:bold;letter-spacing:6px;padding:8px 14px;border:1px solid #dce4ef;border-radius:6px">{WebUtility.HtmlEncode(code)}</span>""";

    private static Letter Make(Person person, string subject, IEnumerable<string> paragraphs)
    {
        var greeting = string.IsNullOrWhiteSpace(person.FirstName) ? "Hello," : $"Hi {WebUtility.HtmlEncode(person.FirstName.Trim())},";
        var body = string.Concat(paragraphs.Select(p => $"<p>{p}</p>"));
        var html = $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              <div style="padding:22px;background:#193f82;color:white"><h1 style="margin:0;font-size:24px">{PoweredBy.Label}</h1></div>
              <div style="padding:24px;border:1px solid #dce4ef;border-top:0;line-height:1.5">
                <p>{greeting}</p>
                {body}
              </div>
            </div>
            """;
        return new Letter(person.Email, person.FullName.Length == 0 ? person.Email : person.FullName, subject, html);
    }

    private static string? FirstWord(string? fullName)
    {
        var name = fullName?.Trim() ?? string.Empty;
        if (name.Length == 0) return null;
        var space = name.IndexOf(' ');
        return space < 0 ? name : name[..space];
    }
}
