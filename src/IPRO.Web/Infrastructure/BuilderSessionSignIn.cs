using System.Collections.Concurrent;
using System.Security.Claims;
using IPRO.Business.SiteImport;
using IPRO.DataAccess;
using IPRO.Entities;

namespace IPRO.Web.Infrastructure;

// iPro TODO 680 (audit 2026-11-24 G-1): a builder session stood for 12 hours whatever became of the iPro sign-in that
// opened it: a team member removed, a password changed or two-step sign-in reset ended their portal session at once,
// but their builder tab went on reading the draft and sending packages. Now the session remembers that sign-in
// (BuilderBeta.SignedIn) and is checked against it exactly as the portal cookie is (AgentCookieRevalidator) each time
// the builder asks iPro (every 5 minutes, and on every hand-over and package): when it would end, the session ends.
public static class BuilderSessionSignIn
{
    // What the opening request's portal cookie says about who is signed in.
    public static BuilderBeta.SignedIn From(ClaimsPrincipal user) => new(
        int.TryParse(user.FindFirstValue("TeamMemberId"), out var member) ? member : 0,
        user.FindFirstValue(PortalSignIn.PasswordClaimType),
        user.FindFirstValue(TwoStepGate.ClaimType));

    // True while the sign-in that opened the session would still stand. A session from before this was kept (no
    // fingerprint) stands until it runs out, as before.
    public static async Task<bool> StandsAsync(IPRODbContext db, WebsiteBuilderSession session)
    {
        if (session.PasswordStamp == null) return true;
        var agent = session.AgentUserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var member = session.TeamMemberId == 0 ? null : session.TeamMemberId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (await AgentCookieRevalidator.EvaluateAsync(db, agent, member) == AgentCookieRevalidator.Verdict.Reject) return false;
        return await AgentCookieRevalidator.EvaluateSignInAsync(db, agent, member, session.TwoStepStamp, session.PasswordStamp)
               != AgentCookieRevalidator.TwoStepVerdict.Reject;
    }

    // iPro TODO 680 (audit G-4): "See it on my site" builds a look of up to 40 MB each time; at most LooksPerMinute a
    // site in any minute (this app runs as one instance, so the memory here is the whole count). The key names the
    // database too, so sites of different databases (test databases) are counted apart.
    public const int LooksPerMinute = 6;
    private static readonly ConcurrentDictionary<string, Queue<DateTime>> Looks = new(StringComparer.Ordinal);

    public static bool TryTakeLook(string siteKey, DateTime now)
    {
        var times = Looks.GetOrAdd(siteKey, _ => new Queue<DateTime>());
        lock (times)
        {
            while (times.Count > 0 && times.Peek() <= now.AddMinutes(-1)) times.Dequeue();
            if (times.Count >= LooksPerMinute) return false;
            times.Enqueue(now);
            return true;
        }
    }
}
