using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// 593 (2026-10-20): the emails two-step sign-in sends (wording: TwoStepLetters). A code is sent while the
// person waits (they need it, and must be told if it did not go); the notices -- turned on, turned off,
// reset by the account owner, a recovery code used -- after the answer, as the sign-in lock email is.
public interface ITwoStepNotices
{
    // The emailed code; false when it could not be sent (the page says so).
    Task<bool> SendCodeAsync(TwoStepAccount account, string code, bool forSetup);

    void Queue(TwoStepAccount account, TwoStepNotice notice, string? detail = null);
}

// EmailChanged: to the OLD address (`detail`), when the login email changes.
public enum TwoStepNotice { TurnedOn, TurnedOff, ResetByOwner, RecoveryCodeUsed, EmailChanged }

public sealed class TwoStepNotices : ITwoStepNotices
{
    private readonly IServiceScopeFactory _scopes;

    public TwoStepNotices(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task<bool> SendCodeAsync(TwoStepAccount account, string code, bool forSetup)
    {
        using var scope = _scopes.CreateScope();
        var services = scope.ServiceProvider;
        var logger = services.GetRequiredService<ILogger<TwoStepNotices>>();
        try
        {
            var person = await PersonAsync(services.GetRequiredService<IPRODbContext>(), account);
            if (person == null || string.IsNullOrWhiteSpace(person.Email)) return false;
            var letter = TwoStepLetters.Code(person, code, forSetup);
            var result = await services.GetRequiredService<IEmailService>().SendDetailedAsync(letter.To, letter.ToName, letter.Subject, letter.Html);
            if (!result.Success)
                logger.LogWarning("Two-step sign-in code email to {Account} was not sent: {Message}", Describe(account), result.Message);
            return result.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Two-step sign-in code email to {Account} failed", Describe(account));
            return false;
        }
    }

    public void Queue(TwoStepAccount account, TwoStepNotice notice, string? detail = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var services = scope.ServiceProvider;
                var logger = services.GetRequiredService<ILogger<TwoStepNotices>>();
                try
                {
                    var person = await PersonAsync(services.GetRequiredService<IPRODbContext>(), account);
                    if (person == null || string.IsNullOrWhiteSpace(person.Email)) return;
                    if (notice == TwoStepNotice.TurnedOn && account.IsTeamMember)
                    {
                        // 593: the adviser hears too -- a team member's set-up needs their session and inbox only.
                        var adviser = await PersonAsync(services.GetRequiredService<IPRODbContext>(), TwoStepAccount.Adviser(account.AgentUserId));
                        if (adviser != null && !string.IsNullOrWhiteSpace(adviser.Email))
                        {
                            var told = TwoStepLetters.MemberTurnedOn(adviser, person.FullName, person.Email);
                            var toldResult = await services.GetRequiredService<IEmailService>().SendDetailedAsync(told.To, told.ToName, told.Subject, told.Html);
                            if (!toldResult.Success)
                                logger.LogWarning("Two-step sign-in notice to the adviser of {Account} was not sent: {Message}", Describe(account), toldResult.Message);
                        }
                    }
                    var letter = notice switch
                    {
                        TwoStepNotice.TurnedOn => TwoStepLetters.TurnedOn(person, detail ?? TwoStepMethods.App),
                        TwoStepNotice.TurnedOff => TwoStepLetters.TurnedOff(person),
                        TwoStepNotice.ResetByOwner => TwoStepLetters.Reset(person, byOwner: true),
                        TwoStepNotice.RecoveryCodeUsed => TwoStepLetters.RecoveryCodeUsed(person, int.TryParse(detail, out var left) ? left : 0),
                        TwoStepNotice.EmailChanged when !string.IsNullOrWhiteSpace(detail) => TwoStepLetters.EmailChanged(person, detail),
                        _ => null
                    };
                    if (letter == null) return;
                    var result = await services.GetRequiredService<IEmailService>().SendDetailedAsync(letter.To, letter.ToName, letter.Subject, letter.Html);
                    if (!result.Success)
                        logger.LogWarning("Two-step sign-in notice {Notice} to {Account} was not sent: {Message}", notice, Describe(account), result.Message);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Two-step sign-in notice {Notice} to {Account} failed", notice, Describe(account));
                }
            }
            catch
            {
                // As SignInLockNotices.Queue: the scope or the logger itself could not be had.
            }
        });
    }

    public static async Task<TwoStepLetters.Person?> PersonAsync(IPRODbContext db, TwoStepAccount account)
    {
        if (account.IsTeamMember)
        {
            var member = await db.TeamMembers.AsNoTracking().Include(t => t.AgentUser)
                .FirstOrDefaultAsync(t => t.Id == account.TeamMemberId && t.AgentUserId == account.AgentUserId);
            return member == null ? null : TwoStepLetters.ForTeamMember(member, member.AgentUser);
        }
        var agent = await db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == account.AgentUserId);
        return agent == null ? null : TwoStepLetters.ForAdviser(agent);
    }

    // For the log: never an email address, only the account.
    public static string Describe(TwoStepAccount account) =>
        account.IsTeamMember ? $"TeamMember {account.TeamMemberId}" : $"Adviser {account.AgentUserId}";
}
