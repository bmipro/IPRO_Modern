using System.Security.Cryptography;
using System.Text;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Business.SiteImport;

// 585: the new website builder for iPro's customers, as a Beta (TODO 585; the owner's decisions of
// 2026-10-17: 2 yes, 3 C, 4 yes, 5 A, 6 yes). Everything here is about who may open the builder and
// with what; the package that goes to the builder and the one that comes back are the hand-over (572)
// and the importer (571) as they are.
//
// How an adviser gets in without a second password (CONTRACT.md section 11): My Website gives their
// browser a one-time pass, which works once and for ten minutes, to carry to the builder; the builder's
// server trades it here for a session of at most twelve hours. iPro keeps only the SHA-256 of each, and
// a session stops working the moment the Beta is switched off, the account is closed, or it is over.
public static class BuilderBeta
{
    public static readonly TimeSpan PassLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    // A draft the adviser has not opened for this long is deleted by the builder (and no longer offered
    // as "Continue my draft" here).
    public const int KeepDays = 90;
    // What the hand-over record says made it, when the adviser opened the builder themselves.
    public const string CustomerIssuer = "the customer, in the builder (Beta)";
    public const string CustomerSender = "the customer, from the builder (Beta)";

    public static Task<AgentBuilderBeta?> FindAsync(IPRODbContext db, int agentId) =>
        db.AgentBuilderBetas.AsNoTracking().FirstOrDefaultAsync(b => b.AgentUserId == agentId);

    public static async Task<bool> IsOnAsync(IPRODbContext db, int agentId) =>
        await db.AgentBuilderBetas.AsNoTracking().AnyAsync(b => b.AgentUserId == agentId && b.Enabled);

    // A SuperAdmin's switch. The adviser's name for the builder is made the first time and kept. Turning
    // the Beta off ends every session the adviser has, so the builder shuts within minutes; their draft
    // stays with the builder (it is erased after KeepDays unopened, or with the account).
    public static async Task<AgentBuilderBeta> SetAsync(IPRODbContext db, int agentId, bool enabled, bool adviserPublishes, string changedBy)
    {
        var row = await db.AgentBuilderBetas.FirstOrDefaultAsync(b => b.AgentUserId == agentId);
        if (row == null)
        {
            row = new AgentBuilderBeta { AgentUserId = agentId, Tenant = "t_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant() };
            db.AgentBuilderBetas.Add(row);
        }
        row.Enabled = enabled;
        row.AdviserPublishes = adviserPublishes;
        row.ChangedAt = DateTime.UtcNow;
        row.ChangedBy = changedBy.Length <= 100 ? changedBy : changedBy[..100];
        await db.SaveChangesAsync();
        if (!enabled) await EndSessionsAsync(db, agentId, DateTime.UtcNow);
        return row;
    }

    public static Task<int> EndSessionsAsync(IPRODbContext db, int agentId, DateTime now) =>
        db.WebsiteBuilderSessions
            .Where(s => s.AgentUserId == agentId && s.EndedAt == null && s.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.EndedAt, now));

    // ---- the pass and the session ----

    // A pass for this adviser's site. startFresh: they chose to start again from the live site;
    // continueHandoverId: the hand-over of the draft they continue ("" for a fresh one).
    public static async Task<string> IssuePassAsync(IPRODbContext db, AgentWebsite site, bool startFresh, string continueHandoverId, DateTime now)
    {
        var token = NewToken();
        db.WebsiteBuilderSessions.Add(new WebsiteBuilderSession
        {
            AgentUserId = site.AgentUserId,
            AgentWebsiteId = site.Id,
            Kind = WebsiteBuilderSessionKinds.Pass,
            TokenSha256 = Hash(token),
            StartFresh = startFresh || continueHandoverId.Length == 0,
            HandoverId = startFresh ? string.Empty : continueHandoverId,
            CreatedAt = now,
            ExpiresAt = now.Add(PassLifetime)
        });
        await db.SaveChangesAsync();
        return token;
    }

    public sealed record Granted(WebsiteBuilderSession Session, string Token, AgentBuilderBeta Beta, AgentWebsite Site, AgentUser Agent);

    // The pass traded for a session, or the sentence the builder shows instead. A pass works once: it
    // is claimed in the database, so two requests carrying it cannot both get a session.
    public static async Task<(Granted? Granted, string Refusal)> RedeemPassAsync(IPRODbContext db, string? pass, DateTime now)
    {
        const string spent = "This link to the builder was already used or has expired. Open the builder again from My Website.";
        if (!LooksLikeToken(pass)) return (null, spent);
        var hash = Hash(pass!);
        var row = await db.WebsiteBuilderSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TokenSha256 == hash && s.Kind == WebsiteBuilderSessionKinds.Pass);
        if (row == null || row.UsedAt != null || row.ExpiresAt <= now) return (null, spent);
        var claimed = await db.WebsiteBuilderSessions
            .Where(s => s.Id == row.Id && s.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAt, now));
        if (claimed != 1) return (null, spent);

        var (agent, beta, site, refusal) = await StandingAsync(db, row.AgentUserId, row.AgentWebsiteId);
        if (refusal != null) return (null, refusal);

        var token = NewToken();
        var session = new WebsiteBuilderSession
        {
            AgentUserId = row.AgentUserId,
            AgentWebsiteId = row.AgentWebsiteId,
            Kind = WebsiteBuilderSessionKinds.Session,
            TokenSha256 = Hash(token),
            StartFresh = row.StartFresh,
            HandoverId = row.HandoverId,
            CreatedAt = now,
            ExpiresAt = now.Add(SessionLifetime)
        };
        db.WebsiteBuilderSessions.Add(session);
        await db.SaveChangesAsync();
        return (new Granted(session, token, beta!, site!, agent!), string.Empty);
    }

    // The session a bearer token names, while it still stands: not over, not ended, the Beta still on,
    // the account still open, and the site still the adviser's. Tracked, so the caller can bind it to
    // the hand-over it fetches.
    public static async Task<(WebsiteBuilderSession? Session, AgentWebsite? Site, AgentBuilderBeta? Beta)> FindSessionAsync(IPRODbContext db, string? token, DateTime now)
    {
        if (!LooksLikeToken(token)) return (null, null, null);
        var hash = Hash(token!);
        var session = await db.WebsiteBuilderSessions
            .FirstOrDefaultAsync(s => s.TokenSha256 == hash && s.Kind == WebsiteBuilderSessionKinds.Session);
        if (session == null || session.EndedAt != null || session.ExpiresAt <= now) return (null, null, null);
        var (_, beta, site, refusal) = await StandingAsync(db, session.AgentUserId, session.AgentWebsiteId);
        return refusal == null ? (session, site, beta) : (null, null, null);
    }

    private static async Task<(AgentUser? Agent, AgentBuilderBeta? Beta, AgentWebsite? Site, string? Refusal)> StandingAsync(IPRODbContext db, int agentId, int websiteId)
    {
        var agent = await db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId);
        if (agent == null || !agent.IsActive) return (null, null, null, "This account is not active. Sign in to iPro for more.");
        var beta = await FindAsync(db, agentId);
        if (beta is not { Enabled: true }) return (null, null, null, "The new builder is not switched on for this account any more. Your website has not changed.");
        var site = await db.AgentWebsites.AsNoTracking().Include(w => w.Template)
            .FirstOrDefaultAsync(w => w.Id == websiteId && w.AgentUserId == agentId);
        if (site == null) return (null, null, null, "This website no longer exists.");
        return (agent, beta, site, null);
    }

    // ---- what My Website shows ----

    // OpenDraft: the newest hand-over of the site was made for the adviser in the builder and nothing has
    // been brought in from it yet, so "Continue my draft" can open it again. Waiting: what the adviser
    // sent and nobody has brought in. PutBack: the package on the site, when it is the adviser's own and
    // can still be taken back out (only the newest one can).
    public sealed record State(AgentBuilderBeta? Beta, WebsiteBuilderHandover? OpenDraft, WebsiteBuilderImport? Waiting, WebsiteBuilderImport? PutBack);

    public static async Task<State> StateAsync(IPRODbContext db, AgentWebsite site, DateTime now)
    {
        var beta = await FindAsync(db, site.AgentUserId);
        var latest = await SiteHandoverService.LatestAsync(db, site.Id);
        WebsiteBuilderHandover? open = null;
        if (latest != null && latest.IssuedBy == CustomerIssuer && latest.IssuedAt > now.AddDays(-KeepDays)
            && !await db.WebsiteBuilderImports.AnyAsync(i => i.AgentWebsiteId == site.Id && i.HandoverId == latest.HandoverId
                && (i.Status == WebsiteBuilderImportStatus.Applied || i.Status == WebsiteBuilderImportStatus.Restored)))
        {
            open = latest;
        }
        var waiting = await db.WebsiteBuilderImports.AsNoTracking()
            .Where(i => i.AgentWebsiteId == site.Id && i.Status == WebsiteBuilderImportStatus.Pending && i.Source == WebsiteBuilderImportSources.Customer)
            .OrderByDescending(i => i.Id)
            .Select(i => new WebsiteBuilderImport { Id = i.Id, AgentUserId = i.AgentUserId, AgentWebsiteId = i.AgentWebsiteId, Status = i.Status, HandoverId = i.HandoverId, CreatedAt = i.CreatedAt, Source = i.Source })
            .FirstOrDefaultAsync();
        var appliedId = await BuilderImportService.LatestAppliedIdAsync(db, site.Id);
        var putBack = appliedId == null ? null : await db.WebsiteBuilderImports.AsNoTracking()
            .Where(i => i.Id == appliedId && i.Source == WebsiteBuilderImportSources.Customer)
            .Select(i => new WebsiteBuilderImport { Id = i.Id, AgentUserId = i.AgentUserId, AgentWebsiteId = i.AgentWebsiteId, Status = i.Status, AppliedAt = i.AppliedAt, AppliedBy = i.AppliedBy, Source = i.Source })
            .FirstOrDefaultAsync();
        return new State(beta, open, waiting, putBack);
    }

    // The pages changed on the site since a package went in: what "Put my previous site back" would
    // lose. Read from the kept copy of the site against the site as it is now.
    public static async Task<IReadOnlyList<string>> ChangedSinceAsync(IPRODbContext db, WebsiteBuilderImport applied)
    {
        if (applied.AppliedAt == null) return Array.Empty<string>();
        return await db.WebsitePages.AsNoTracking()
            .Where(p => p.AgentWebsiteId == applied.AgentWebsiteId
                && (p.UpdatedAt > applied.AppliedAt || p.CreatedAt > applied.AppliedAt || p.Blocks.Any(b => b.UpdatedAt > applied.AppliedAt)))
            .OrderBy(p => p.SortOrder)
            .Select(p => p.Title)
            .ToListAsync();
    }

    // ---- the site's look, for the builder's canvas (decision 5 A) ----

    // The colours, font and sizes the public site really uses: the template's own, with the adviser's
    // own choices from My Website over them (the same resolution the public site's shells make).
    public static Dictionary<string, object> Look(AgentWebsite site)
    {
        var design = WebsiteTemplateDesign.FromTemplate(site.Template, new AgentDesignOverrides
        {
            ThemeColor = site.ThemeColor,
            FontFamily = site.FontFamilyOverride,
            HeadingFontSize = site.HeadingFontSizeOverride,
            BodyFontSize = site.BodyFontSizeOverride,
            BackgroundColor = site.BackgroundColorOverride,
            ButtonStyle = site.ButtonStyleOverride,
            SectionSpacing = site.SectionSpacingOverride,
            HeroStyle = site.HeroStyleOverride
        });
        return new Dictionary<string, object>
        {
            ["accent"] = design.AccentColor ?? string.Empty,
            ["background"] = design.BackgroundColor ?? string.Empty,
            ["font"] = design.FontFamily ?? string.Empty,
            ["headingSize"] = design.HeadingFontSize,
            ["bodySize"] = design.BodyFontSize,
            ["buttons"] = design.ButtonStyle switch { "square" => "square", "pill" => "pill", _ => "rounded" }
        };
    }

    // ---- tokens ----

    // 32 random bytes, base64url without padding: 43 characters.
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool LooksLikeToken(string? token) =>
        token is { Length: 43 } && token.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
