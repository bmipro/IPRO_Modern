using System.Net;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// 583 (2026-10-16): what the person sees when a sign-in is locked, and the one email the lock sends.
//
// On screen: one sentence per sign-in page, the same for every account on it and for a name that is
// no account at all (the lock must not tell a stranger which names exist).
public static class SignInMessages
{
    public const string PortalLocked =
        "Sign-in with this name is locked for 15 minutes after 5 wrong passwords in a row. To get in sooner, " +
        "reset your password with \"Forgot password?\" below. A team member asks the account owner to reset theirs.";

    public const string ClientPortalLocked =
        "Sign-in with this email is locked for 15 minutes after 5 wrong passwords in a row. If you have " +
        "forgotten your password, ask your advisor to send you a new invitation.";

    // Change Password checks the current password like a sign-in does, and counts the same way.
    public const string ChangePasswordLocked =
        "Your sign-in is locked for 15 minutes after 5 wrong passwords in a row, so your password cannot be " +
        "changed now. Try again after 15 minutes.";
}

public interface ISignInLockNotices
{
    // Sends the lock email after the sign-in has answered: the answer must not wait for it, or the
    // time it took would say the name was an account.
    void Queue(SignInAccount account);
}

public sealed class SignInLockNotices : ISignInLockNotices
{
    // Lock emails share the provider's sending limits (EmailSendGate: 100 an hour on the current
    // service) with password resets, invoices and portal invitations. Someone locking many accounts at
    // once must not use them up: past this many in an hour, a lock is logged but not emailed.
    public const int MaxPerHour = 20;

    private readonly IServiceScopeFactory _scopes;
    private readonly Func<DateTime> _now;
    private readonly Queue<DateTime> _sentLastHour = new();

    public SignInLockNotices(IServiceScopeFactory scopes, Func<DateTime>? now = null)
    {
        _scopes = scopes;
        _now = now ?? (() => DateTime.UtcNow);
    }

    // One of this hour's MaxPerHour, or false when they are used up.
    public bool TryTakeHourlySlot()
    {
        var now = _now();
        lock (_sentLastHour)
        {
            while (_sentLastHour.Count > 0 && _sentLastHour.Peek() <= now.AddHours(-1)) _sentLastHour.Dequeue();
            if (_sentLastHour.Count >= MaxPerHour) return false;
            _sentLastHour.Enqueue(now);
            return true;
        }
    }

    public void Queue(SignInAccount account)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var services = scope.ServiceProvider;
                var logger = services.GetRequiredService<ILogger<SignInLockNotices>>();
                try
                {
                    await SendAsync(services.GetRequiredService<IPRODbContext>(), services.GetRequiredService<IEmailService>(),
                        services.GetRequiredService<IConfiguration>(), logger, account, DateTime.UtcNow, TryTakeHourlySlot);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "The sign-in lock email for {Kind} {Id} failed", account.Kind, account.Id);
                }
            }
            catch
            {
                // As AccountController.QueuePasswordResetEmail: the scope or the logger itself could not be
                // had, so there is nothing to log with.
            }
        });
    }

    // One email when a sign-in locks, and no more than one a day however often it locks again
    // (SignInLockout.ClaimNoticeAsync). Null = nothing sent: one already went out today, the lock is
    // already over (the right password sent at the same moment ended it, or a new password did), or
    // there is no address to send to. A send that fails gives the day back, so the next lock tries again.
    // `hourlySlot`: one of MaxPerHour (the background sender passes TryTakeHourlySlot).
    public static async Task<EmailSendResult?> SendAsync(IPRODbContext db, IEmailService email, IConfiguration configuration,
        ILogger logger, SignInAccount account, DateTime now, Func<bool>? hourlySlot = null)
    {
        var message = await ComposeAsync(db, configuration, account);
        if (message == null) return null;
        if (!await SignInLockout.ClaimNoticeAsync(db, account, now)) return null;
        if (hourlySlot != null && !hourlySlot())
        {
            await SignInLockout.ReleaseNoticeAsync(db, account, now);
            logger.LogWarning("Sign-in lock email to {Kind} {Id} not sent: {Max} lock emails went out in the last hour (many sign-ins locked at once is someone guessing)",
                account.Kind, account.Id, MaxPerHour);
            return null;
        }

        var result = await email.SendDetailedAsync(message.To, message.ToName, message.Subject, message.Html,
            customArgs: message.Tags, replyToEmail: message.ReplyTo, replyToName: message.ReplyToName);
        if (result.Success)
        {
            logger.LogInformation("Sign-in lock email sent to {Kind} {Id}", account.Kind, account.Id);
        }
        else
        {
            await SignInLockout.ReleaseNoticeAsync(db, account, now);
            logger.LogWarning("Sign-in lock email to {Kind} {Id} was not sent: {Message}", account.Kind, account.Id, result.Message);
        }
        return result;
    }

    public sealed record LockEmail(string To, string ToName, string Subject, string Html,
        Dictionary<string, string>? Tags = null, string? ReplyTo = null, string? ReplyToName = null);

    public static async Task<LockEmail?> ComposeAsync(IPRODbContext db, IConfiguration configuration, SignInAccount account)
    {
        switch (account.Kind)
        {
            case SignInAccountKind.Adviser:
            {
                var agent = await db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == account.Id);
                if (agent == null || string.IsNullOrWhiteSpace(agent.Email) || !StillLocked(agent.SignInLockedUntil)) return null;
                return ForAdviser(agent, PortalUrlHelper.GetAgentPortalBaseUrl(configuration));
            }
            case SignInAccountKind.TeamMember:
            {
                var member = await db.TeamMembers.AsNoTracking().Include(t => t.AgentUser).FirstOrDefaultAsync(t => t.Id == account.Id);
                if (member == null || string.IsNullOrWhiteSpace(member.Email) || !StillLocked(member.SignInLockedUntil)) return null;
                return ForTeamMember(member, member.AgentUser);
            }
            case SignInAccountKind.Client:
            {
                var client = await db.Clients.AsNoTracking().Include(c => c.AgentUser).FirstOrDefaultAsync(c => c.Id == account.Id);
                if (client == null || string.IsNullOrWhiteSpace(client.Email) || !StillLocked(client.SignInLockedUntil)) return null;
                return ForClient(client, client.AgentUser);
            }
            default:
                return null;   // an admin account has no email address
        }
    }

    private static bool StillLocked(DateTime? lockedUntil) => SignInLockout.IsLocked(lockedUntil, DateTime.UtcNow);

    public const string Subject = "Your iPro sign-in was locked for 15 minutes";
    private const string OncePerDay = "You get this email at most once a day, however many times sign-in is locked.";

    public static LockEmail ForAdviser(AgentUser agent, string portalBaseUrl)
    {
        var resetUrl = $"{portalBaseUrl.TrimEnd('/')}/Account/ForgotPassword";
        var name = $"{agent.FirstName} {agent.LastName}".Trim();
        var html = Letter(agent.FirstName, new[]
            {
                "Someone typed the wrong password for your iPro account 5 times in a row, so we locked sign-in to your account for 15 minutes. Wrong passwords don't get anyone in.",
                "If it was you, you can sign in again after 15 minutes, or reset your password now to sign in straight away.",
                "If it wasn't you, reset your password to be safe, and choose one you don't use anywhere else."
            },
            "Reset my password", resetUrl);
        return new LockEmail(agent.Email.Trim(), name.Length == 0 ? agent.Email.Trim() : name, Subject, html);
    }

    public static LockEmail ForTeamMember(TeamMember member, AgentUser? owner)
    {
        var ownerName = owner == null ? "the account owner" : WebUtility.HtmlEncode($"{owner.FirstName} {owner.LastName}".Trim());
        if (ownerName.Length == 0) ownerName = "the account owner";
        var business = WebUtility.HtmlEncode(AdviserSender.BusinessName(owner));
        var account = business.Length == 0 ? "the iPro account" : $"{business}'s iPro account";
        var html = Letter(member.FullName, new[]
            {
                $"Someone typed the wrong password for your sign-in ({WebUtility.HtmlEncode(member.Email)}) to {account} 5 times in a row, so we locked it for 15 minutes. Wrong passwords don't get anyone in.",
                $"If it was you, you can sign in again after 15 minutes. If you have forgotten your password, ask {ownerName} to reset it on the My Team page; that unlocks it straight away.",
                $"If it wasn't you, ask {ownerName} to reset your password to be safe."
            },
            null, null);
        return new LockEmail(member.Email.Trim(), string.IsNullOrWhiteSpace(member.FullName) ? member.Email.Trim() : member.FullName.Trim(), Subject, html);
    }

    // A letter from the adviser, as every email to their client is (542); replies reach the adviser.
    public static LockEmail ForClient(Client client, AgentUser? agent)
    {
        var companyName = AdviserSender.BusinessName(agent);
        var html = ClientLetter.Html(agent, client.FirstName,
            new[]
            {
                $"Someone typed the wrong password for your client portal sign-in ({WebUtility.HtmlEncode(client.Email)}) 5 times in a row, so sign-in was locked for 15 minutes. Wrong passwords don't get anyone in.",
                "If it was you, you can sign in again after 15 minutes. If you have forgotten your password, reply to this email and we will send you a new invitation to choose one.",
                "If it wasn't you, reply to this email and we will help you choose a new password."
            },
            heading: companyName.Length == 0 ? "Client Portal" : $"{companyName} Client Portal", accent: ClientLetter.Green);
        return new LockEmail(client.Email.Trim(), $"{client.FirstName} {client.LastName}".Trim(),
            AdviserSender.Subject(agent, "Your client portal sign-in was locked for 15 minutes"), html,
            AdviserSender.Tags(agent, EmailStreams.Notify), AdviserSender.ReplyToEmail(agent), AdviserSender.ReplyToName(agent));
    }

    // iPro's own mail to the people who sign in to the portal, in the house style of the reset email.
    private static string Letter(string? firstName, IEnumerable<string> paragraphs, string? buttonLabel, string? buttonUrl)
    {
        var greeting = string.IsNullOrWhiteSpace(firstName) ? "Hello," : $"Hi {WebUtility.HtmlEncode(firstName.Trim())},";
        var body = string.Concat(paragraphs.Select(p => $"<p>{p}</p>"));
        var button = buttonLabel == null || buttonUrl == null ? string.Empty
            : $"""<p><a href="{buttonUrl}" style="display:inline-block;padding:11px 18px;background:#193f82;color:white;text-decoration:none;border-radius:6px">{WebUtility.HtmlEncode(buttonLabel)}</a></p>""";
        return $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              <div style="padding:22px;background:#193f82;color:white"><h1 style="margin:0;font-size:24px">{PoweredBy.Label}</h1></div>
              <div style="padding:24px;border:1px solid #dce4ef;border-top:0;line-height:1.5">
                <p>{greeting}</p>
                {body}
                {button}
                <p style="color:#64748b;font-size:13px;margin-top:24px">{OncePerDay}</p>
              </div>
            </div>
            """;
    }
}
