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
//
// Whether the adviser's draft is continued is the builder's to decide (it alone knows what it holds):
// iPro tells it where the site's newest hand-over stands (CurrentAsync), and the builder continues a
// draft of that hand-over while nothing of it has gone onto the website. Otherwise it opens the site
// afresh and keeps the draft as a version. A pass says only whether the adviser chose to start again.
public static class BuilderBeta
{
    public static readonly TimeSpan PassLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    // A draft the adviser has not opened for this long is deleted by the builder, which counts from
    // the last visit, as AgentBuilderBeta.LastOpenedAt does here.
    public const int KeepDays = 90;
    // What the hand-over record says made it, when the adviser opened the builder themselves.
    public const string CustomerIssuer = "the customer, in the builder (Beta)";
    public const string CustomerSender = "the customer, from the builder (Beta)";

    public static Task<AgentBuilderBeta?> FindAsync(IPRODbContext db, int agentId) =>
        db.AgentBuilderBetas.AsNoTracking().FirstOrDefaultAsync(b => b.AgentUserId == agentId);

    public static async Task<bool> IsOnAsync(IPRODbContext db, int agentId) =>
        await db.AgentBuilderBetas.AsNoTracking().AnyAsync(b => b.AgentUserId == agentId && b.Enabled);

    // A SuperAdmin's switch. The adviser's name for the builder is made the first time and kept. Turning
    // the Beta off ends every session the adviser has: the builder stops at its next check with iPro,
    // within five minutes. Their draft stays with the builder (it is erased after KeepDays unopened, or
    // with the account).
    public static async Task<AgentBuilderBeta> SetAsync(IPRODbContext db, int agentId, bool enabled, bool adviserPublishes, string changedBy)
    {
        for (var attempt = 1; ; attempt++)
        {
            var row = await db.AgentBuilderBetas.FirstOrDefaultAsync(b => b.AgentUserId == agentId);
            var created = row == null;
            if (row == null)
            {
                row = new AgentBuilderBeta { AgentUserId = agentId, Tenant = "t_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant() };
                db.AgentBuilderBetas.Add(row);
            }
            row.Enabled = enabled;
            row.AdviserPublishes = adviserPublishes;
            row.ChangedAt = DateTime.UtcNow;
            row.ChangedBy = changedBy.Length <= 100 ? changedBy : changedBy[..100];
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException) when (created && attempt == 1)
            {
                // Two SuperAdmins switching the same adviser on at the same moment: the other made the
                // row. It is read again and changed instead.
                db.Entry(row).State = EntityState.Detached;
                continue;
            }
            if (!enabled) await EndSessionsAsync(db, agentId, DateTime.UtcNow);
            return row;
        }
    }

    public static Task<int> EndSessionsAsync(IPRODbContext db, int agentId, DateTime now) =>
        db.WebsiteBuilderSessions
            .Where(s => s.AgentUserId == agentId && s.EndedAt == null && s.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.EndedAt, now));

    // ---- where the site's newest hand-over stands ----

    public static class HandoverStates
    {
        // A package of it can still come back.
        public const string Open = "open";
        // A package of it is on the website.
        public const string Published = "published";
        // A package of it went onto the website and was taken back off.
        public const string PutBack = "putBack";
    }

    // The site's newest hand-over ("" when there is none yet), whether a package of it can still be
    // sent, and when it went onto (or came off) the website.
    public sealed record CurrentHandover(string HandoverId, string State, DateTime? At);

    public static async Task<CurrentHandover> CurrentAsync(IPRODbContext db, int websiteId)
    {
        var latest = await SiteHandoverService.LatestAsync(db, websiteId);
        if (latest == null) return new CurrentHandover(string.Empty, HandoverStates.Open, null);
        var went = await db.WebsiteBuilderImports.AsNoTracking()
            .Where(i => i.AgentWebsiteId == websiteId && i.HandoverId == latest.HandoverId
                && (i.Status == WebsiteBuilderImportStatus.Applied || i.Status == WebsiteBuilderImportStatus.Restored))
            .OrderByDescending(i => i.AppliedAt).ThenByDescending(i => i.Id)
            .Select(i => new { i.Status, i.AppliedAt, i.ClosedAt })
            .ToListAsync();
        var on = went.FirstOrDefault(i => i.Status == WebsiteBuilderImportStatus.Applied);
        if (on != null) return new CurrentHandover(latest.HandoverId, HandoverStates.Published, on.AppliedAt);
        if (went.Count > 0) return new CurrentHandover(latest.HandoverId, HandoverStates.PutBack, went[0].ClosedAt ?? went[0].AppliedAt);
        return new CurrentHandover(latest.HandoverId, HandoverStates.Open, null);
    }

    // Whether a package of this hand-over already went onto the website (and is there, or was put
    // back): what a second one would replace can no longer be told apart from what the first wrote, so
    // the builder's doors refuse it and the builder opens the site afresh next time.
    public static Task<bool> IsSpentAsync(IPRODbContext db, int websiteId, string handoverId) =>
        db.WebsiteBuilderImports.AsNoTracking().AnyAsync(i => i.AgentWebsiteId == websiteId && i.HandoverId == handoverId
            && (i.Status == WebsiteBuilderImportStatus.Applied || i.Status == WebsiteBuilderImportStatus.Restored));

    // ---- the pass and the session ----

    // A pass for this adviser's site. startFresh: they chose "Start again from my live site".
    public static async Task<string> IssuePassAsync(IPRODbContext db, AgentWebsite site, bool startFresh, DateTime now)
    {
        var token = NewToken();
        db.WebsiteBuilderSessions.Add(new WebsiteBuilderSession
        {
            AgentUserId = site.AgentUserId,
            AgentWebsiteId = site.Id,
            Kind = WebsiteBuilderSessionKinds.Pass,
            TokenSha256 = Hash(token),
            StartFresh = startFresh,
            HandoverId = string.Empty,
            CreatedAt = now,
            ExpiresAt = now.Add(PassLifetime)
        });
        await db.SaveChangesAsync();
        return token;
    }

    public sealed record Granted(WebsiteBuilderSession Session, string Token, AgentBuilderBeta Beta, AgentWebsite Site, AgentUser Agent, CurrentHandover Current);

    // The pass traded for a session, or the sentence the builder shows instead. A pass works once: it
    // is claimed in the database, so two requests carrying it cannot both get a session. A pass whose
    // sessions were ended (the Beta switched off after it was issued) is spent too.
    //
    // The session starts bound to the site's newest hand-over while a package of it can still come back
    // (the draft the builder continues is of that one), and to none otherwise, until the builder fetches
    // a fresh hand-over.
    public static async Task<(Granted? Granted, string Refusal)> RedeemPassAsync(IPRODbContext db, string? pass, DateTime now)
    {
        const string spent = "This link to the builder was already used or has expired. Open the builder again from My Website.";
        if (!LooksLikeToken(pass)) return (null, spent);
        var hash = Hash(pass!);
        var row = await db.WebsiteBuilderSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TokenSha256 == hash && s.Kind == WebsiteBuilderSessionKinds.Pass);
        if (row == null || row.UsedAt != null || row.EndedAt != null || row.ExpiresAt <= now) return (null, spent);
        var claimed = await db.WebsiteBuilderSessions
            .Where(s => s.Id == row.Id && s.UsedAt == null && s.EndedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAt, now));
        if (claimed != 1) return (null, spent);

        var (agent, beta, site, refusal) = await StandingAsync(db, row.AgentUserId, row.AgentWebsiteId);
        if (refusal != null) return (null, refusal);

        var current = await CurrentAsync(db, row.AgentWebsiteId);
        var token = NewToken();
        var session = new WebsiteBuilderSession
        {
            AgentUserId = row.AgentUserId,
            AgentWebsiteId = row.AgentWebsiteId,
            Kind = WebsiteBuilderSessionKinds.Session,
            TokenSha256 = Hash(token),
            StartFresh = row.StartFresh,
            HandoverId = !row.StartFresh && current.State == HandoverStates.Open ? current.HandoverId : string.Empty,
            CreatedAt = now,
            ExpiresAt = now.Add(SessionLifetime)
        };
        db.WebsiteBuilderSessions.Add(session);
        await db.SaveChangesAsync();
        // The visit the builder's 90 days count from (and My Website's "Continue my draft").
        await db.AgentBuilderBetas.Where(b => b.AgentUserId == row.AgentUserId)
            .ExecuteUpdateAsync(b => b.SetProperty(x => x.LastOpenedAt, now));
        return (new Granted(session, token, beta!, site!, agent!, current), string.Empty);
    }

    // The session a bearer token names, while it still stands: not over, not ended, the Beta still on,
    // the account still open, and the site still the adviser's. Tracked, so the caller can bind it to
    // the hand-over it fetches. (Whether the adviser's package still includes their website, and their
    // billing, is asked by the caller, which has the entitlements: BuilderApiController.)
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

    // HasDraft: the builder holds a draft of this adviser's (they opened it within KeepDays), so the
    // card offers "Continue my draft" and "Start again from my live site". OpenDraft: the newest
    // hand-over of the site, when it was made for the adviser in the builder and nothing has gone onto
    // the website from it (the page editor's list then says changes made there meanwhile are kept).
    // Waiting: what the adviser sent and nobody has brought in. PutBack: the package on the site, when it
    // is the adviser's own and can still be taken back out (only the newest one can).
    // PutBackSinceDraft (612): the open draft was made while a package was on the website, and that
    // package has since been put back (when). The draft still has its changes, so the card says so.
    public sealed record State(AgentBuilderBeta? Beta, WebsiteBuilderHandover? OpenDraft, WebsiteBuilderImport? Waiting, WebsiteBuilderImport? PutBack)
    {
        public bool HasDraft { get; init; }
        public DateTime? PutBackSinceDraft { get; init; }
    }

    public static async Task<State> StateAsync(IPRODbContext db, AgentWebsite site, DateTime now)
    {
        var beta = await FindAsync(db, site.AgentUserId);
        var hasDraft = beta?.LastOpenedAt is { } opened && opened > now.AddDays(-KeepDays);
        var latest = await SiteHandoverService.LatestAsync(db, site.Id);
        WebsiteBuilderHandover? open = null;
        if (hasDraft && latest != null && latest.IssuedBy == CustomerIssuer && !await IsSpentAsync(db, site.Id, latest.HandoverId))
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
        // 612 (the owner's staging try, 2026-10-28): a package went in, the builder opened the site afresh
        // with it on, then the package was put back. "Continue my draft" carries on with those changes.
        var putBackSinceDraft = open == null ? null : await db.WebsiteBuilderImports.AsNoTracking()
            .Where(i => i.AgentWebsiteId == site.Id && i.Status == WebsiteBuilderImportStatus.Restored
                && i.AppliedAt < open.IssuedAt && i.ClosedAt > open.IssuedAt)
            .OrderByDescending(i => i.ClosedAt)
            .Select(i => i.ClosedAt)
            .FirstOrDefaultAsync();
        return new State(beta, open, waiting, putBack) { HasDraft = hasDraft, PutBackSinceDraft = putBackSinceDraft };
    }

    // The pages changed on the site since a package went in: what "Put my previous site back" would
    // lose. Read from the kept copy of the site against the site as it is now. (A section deleted or
    // moved in the page editor stamps nothing, so the card also says the whole site goes back.)
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
    // 614: and what the builder needs to draw the page as the site's own design does: which of the three
    // designs, the custom properties its shell sets, the classes on its body and header, and the words
    // the design writes itself (the business name over the hero, its first letter where the hero has
    // no picture, the contact's name and phone, the footer's name). Every one optional to the builder.
    public static Dictionary<string, object> Look(AgentWebsite site, AgentUser? agent = null)
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
        var look = new Dictionary<string, object>
        {
            ["accent"] = design.AccentColor ?? string.Empty,
            ["background"] = design.BackgroundColor ?? string.Empty,
            ["font"] = design.FontFamily ?? string.Empty,
            ["headingSize"] = design.HeadingFontSize,
            ["bodySize"] = design.BodyFontSize,
            ["buttons"] = design.ButtonStyle switch { "square" => "square", "pill" => "pill", _ => "rounded" }
        };

        var shell = ShellFor(design.Renderer);
        var accent = AccentContrast.For(design.AccentColor ?? string.Empty, shell.Tint, design.BackgroundColor);
        var vars = new Dictionary<string, string>
        {
            ["--site-theme"] = accent.Fill,
            ["--site-theme-on"] = accent.OnFill,
            ["--site-theme-band"] = accent.Band,
            ["--site-theme-ink"] = accent.Ink,
            ["--site-theme-deep"] = shell.Deep,
            ["--site-theme-soft"] = shell.Tint,
            ["--section-space"] = design.SectionSpacing == "compact" ? "44px" : design.SectionSpacing == "comfortable" ? "62px" : "82px",
            ["--button-radius"] = design.ButtonStyle == "square" ? "2px" : design.ButtonStyle == "pill" ? "999px" : "7px",
            ["--heading-font-size"] = design.HeadingFontSize + "px",
            ["--body-font-size"] = design.BodyFontSize + "px"
        };
        foreach (var (name, value) in shell.Extra) vars[name] = value;
        var header = WebsiteHeaderSettings.FromJson(site.HeaderSettingsJson);
        look["design"] = shell.Name;
        // The font the live site really draws with: the shells write the font list into a <style> through
        // Razor, which encodes its quote marks, and the browser drops a declaration so written; the page
        // then falls back to Bootstrap's own font (TODO 618). Empty says "the browser's default".
        var font = design.FontFamily ?? string.Empty;
        look["siteFont"] = font.Contains('\'') || font.Contains('"') ? string.Empty : font;
        look["vars"] = vars;
        look["bodyClass"] = $"header-{design.HeaderStyle} hero-{design.HeroLayout} hero-style-{design.HeroStyle} buttons-{design.ButtonStyle}";
        look["headerClass"] = $"style-{header.Style} logo-{header.LogoPosition} logo-size-{header.LogoSize}" + (header.Sticky ? " is-sticky" : string.Empty);
        if (agent != null)
        {
            var company = (agent.CompanyName ?? string.Empty).Trim();
            var person = $"{agent.FirstName} {agent.LastName}".Trim();
            var footer = WebsiteFooterSettings.FromJson(site.FooterSettingsJson);
            var siteName = string.IsNullOrWhiteSpace(site.SiteTitle) ? (company.Length == 0 ? person : company) : site.SiteTitle.Trim();
            look["company"] = Clip(company, 120);
            look["monogram"] = Monogram(agent);
            look["contact"] = new Dictionary<string, string> { ["name"] = Clip(person, 120), ["phone"] = Clip((agent.Phone ?? string.Empty).Trim(), 40) };
            look["copyright"] = Clip(string.IsNullOrWhiteSpace(footer.CopyrightText) ? siteName : footer.CopyrightText.Trim(), 120);
            // What the header shows after the menu (_PublicNavigation): the phone, an "Email" link, a button.
            look["headerActions"] = new Dictionary<string, object>
            {
                ["phone"] = header.ShowPhone ? Clip((agent.Phone ?? string.Empty).Trim(), 40) : string.Empty,
                ["email"] = header.ShowEmail && !string.IsNullOrWhiteSpace(agent.Email),
                ["button"] = Clip((header.ButtonText ?? string.Empty).Trim(), 60)
            };
        }
        return look;
    }

    // 614: what each of the three shells (_ModernProfessional, _ClassicSidebar, _EditorialVisual) sets in
    // its :root besides the colour roles: its palest tint, its deep colour, and its own text colours.
    // BuilderSiteLook614Tests holds these to the shells' own lines.
    public sealed record Shell(string Name, string Tint, string Deep, IReadOnlyList<(string Name, string Value)> Extra);

    public static Shell ShellFor(string? renderer) => renderer switch
    {
        "classic-sidebar" => new("classic-sidebar", "#f2f6fc", "#111827", [("--ink", "#162033"), ("--muted", "#607089"), ("--line", "#d8e1ee")]),
        "editorial-visual" => new("editorial-visual", "#f1ece3", "#241f1a", [("--ink", "#211e1a"), ("--muted", "#6f685f")]),
        _ => new("modern-professional", "#eef4ff", "#07142f", [])
    };

    // The letter a hero without a picture shows (each managed page's HeroMonogram).
    public static string Monogram(AgentUser agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.FirstName)) return agent.FirstName.Substring(0, 1).ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(agent.CompanyName)) return agent.CompanyName.Substring(0, 1).ToUpperInvariant();
        return ((char)0x2022).ToString();
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];

    // ---- tokens ----

    // 32 random bytes, base64url without padding: 43 characters.
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool LooksLikeToken(string? token) =>
        token is { Length: 43 } && token.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
