using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

public sealed record PlatformVisitorBreakdown(string Label, int Views, int Visitors);
public sealed record PlatformVisitorDay(DateTime Date, int Views, int Visitors);
public sealed record PlatformSignupCount(string Label, int SignUps);

public sealed class PlatformVisitorReport
{
    public int PeriodDays { get; init; }
    public int TotalViews { get; init; }
    public int UniqueVisitors { get; init; }
    public int PreviousViews { get; init; }
    public int SignUps { get; init; }
    public int SignUpsWithOrigin { get; init; }
    public IReadOnlyList<PlatformVisitorDay> Daily { get; init; } = Array.Empty<PlatformVisitorDay>();
    public IReadOnlyList<PlatformVisitorBreakdown> ByPage { get; init; } = Array.Empty<PlatformVisitorBreakdown>();
    public IReadOnlyList<PlatformVisitorBreakdown> ByHost { get; init; } = Array.Empty<PlatformVisitorBreakdown>();
    public IReadOnlyList<PlatformVisitorBreakdown> ByReferrer { get; init; } = Array.Empty<PlatformVisitorBreakdown>();
    public IReadOnlyList<PlatformVisitorBreakdown> ByCampaign { get; init; } = Array.Empty<PlatformVisitorBreakdown>();
    public IReadOnlyList<PlatformSignupCount> SignUpsByOrigin { get; init; } = Array.Empty<PlatformSignupCount>();
}

// 512 (2026-09-22): visits to the PLATFORM's own public pages, counted the way adviser sites' visits
// are (PublicWebsiteController.TrackPageViewAsync): no cookie, a one-way hashed visitor that changes
// monthly, DNT honoured, crawlers and link previews skipped -- plus the campaign tags a link carries,
// and, for a self-registered adviser, the origin of the most recent view by the same hashed visitor.
// Set-based writes, like 498 and 508: nothing is tracked, and recording twice is not an error.
public static class PlatformVisits
{
    public const string DirectLabel = "Direct / unknown";
    private const int MatchWindowDays = 31;

    // The same person for a month, nobody after: the hash changes when the month does, so nothing
    // here can follow a visitor across months, and the raw address is never stored.
    public static string VisitorHash(string? ipAddress, string? userAgent, DateTime nowUtc)
    {
        var input = $"platform|{nowUtc:yyyy-MM}|{ipAddress ?? string.Empty}|{userAgent ?? string.Empty}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    // The per-site list plus the tools that fetch our pages without a person behind them: our own
    // checks (curl), monitors, and the libraries crawlers are written with.
    private static readonly string[] BotMarkers =
    {
        "bot", "crawler", "spider", "slurp", "preview", "facebookexternalhit", "whatsapp", "headless",
        "curl/", "wget/", "python-requests", "python-urllib", "go-http-client", "okhttp", "java/",
        "httpclient", "libwww", "scrapy", "monitor", "uptime", "pingdom", "postman", "insomnia"
    };

    public static bool IsLikelyBot(string? userAgent) =>
        string.IsNullOrWhiteSpace(userAgent) ||
        BotMarkers.Any(marker => userAgent.Contains(marker, StringComparison.OrdinalIgnoreCase));

    // Null means "not a visit": Do Not Track, or no browser behind the request.
    public static PlatformPageView? Build(string? host, string? path, string? source, string? medium, string? campaign,
        string? referrerHost, string? userAgent, bool doNotTrack, string? ipAddress, DateTime nowUtc)
    {
        if (doNotTrack || IsLikelyBot(userAgent)) return null;

        var cleanPath = (path ?? string.Empty).Trim();
        return new PlatformPageView
        {
            Host = Host(host, 255),
            Path = Cut(cleanPath.Length == 0 ? "/" : cleanPath, 200),
            ReferrerHost = Host(referrerHost, 255),
            Source = Tag(source),
            Medium = Tag(medium),
            Campaign = Tag(campaign),
            VisitorHash = VisitorHash(ipAddress, userAgent, nowUtc),
            CreatedAt = nowUtc
        };
    }

    // How an origin reads on the report: the campaign tags when the link carried any, else the
    // referring site, else direct.
    public static string OriginLabel(string? source, string? medium, string? campaign, string? referrerHost)
    {
        var tags = new[] { source, medium, campaign }.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim()).ToList();
        if (tags.Count > 0) return string.Join(" / ", tags);
        return string.IsNullOrWhiteSpace(referrerHost) ? DirectLabel : referrerHost.Trim();
    }

    public static async Task RecordAsync(IPRODbContext db, PlatformPageView view)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO `PlatformPageViews` (`Host`, `Path`, `ReferrerHost`, `Source`, `Medium`, `Campaign`, `VisitorHash`, `CreatedAt`)
VALUES ({view.Host}, {view.Path}, {view.ReferrerHost}, {view.Source}, {view.Medium}, {view.Campaign}, {view.VisitorHash}, {view.CreatedAt})");
    }

    // The origin of a sign-up: the most recent view by the same hashed visitor that CAME from
    // somewhere (a campaign tag or a referring site) within the month, else their most recent view,
    // else nothing known -- a row is written either way, so the report can say how many sign-ups
    // it could and could not place.
    public static async Task RecordSignupOriginAsync(IPRODbContext db, int agentUserId, string visitorHash, DateTime nowUtc)
    {
        var since = nowUtc.AddDays(-MatchWindowDays);
        var views = await db.PlatformPageViews.AsNoTracking()
            .Where(v => v.VisitorHash == visitorHash && v.CreatedAt >= since && v.CreatedAt <= nowUtc)
            .OrderByDescending(v => v.CreatedAt)
            .ToListAsync();
        var chosen = views.FirstOrDefault(v => v.Source.Length > 0 || v.Medium.Length > 0 || v.Campaign.Length > 0 || v.ReferrerHost.Length > 0)
                     ?? views.FirstOrDefault();

        var host = chosen?.Host ?? string.Empty;
        var path = chosen?.Path ?? string.Empty;
        var referrer = chosen?.ReferrerHost ?? string.Empty;
        var source = chosen?.Source ?? string.Empty;
        var medium = chosen?.Medium ?? string.Empty;
        var campaign = chosen?.Campaign ?? string.Empty;
        var firstSeen = chosen?.CreatedAt;

        var updated = await db.PlatformSignupOrigins
            .Where(o => o.AgentUserId == agentUserId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Host, host)
                .SetProperty(o => o.Path, path)
                .SetProperty(o => o.ReferrerHost, referrer)
                .SetProperty(o => o.Source, source)
                .SetProperty(o => o.Medium, medium)
                .SetProperty(o => o.Campaign, campaign)
                .SetProperty(o => o.FirstSeenAt, firstSeen)
                .SetProperty(o => o.RecordedAt, nowUtc));
        if (updated == 0)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO `PlatformSignupOrigins` (`AgentUserId`, `Host`, `Path`, `ReferrerHost`, `Source`, `Medium`, `Campaign`, `FirstSeenAt`, `RecordedAt`)
VALUES ({agentUserId}, {host}, {path}, {referrer}, {source}, {medium}, {campaign}, {firstSeen}, {nowUtc})
ON DUPLICATE KEY UPDATE `Host` = VALUES(`Host`), `Path` = VALUES(`Path`), `ReferrerHost` = VALUES(`ReferrerHost`), `Source` = VALUES(`Source`),
    `Medium` = VALUES(`Medium`), `Campaign` = VALUES(`Campaign`), `FirstSeenAt` = VALUES(`FirstSeenAt`), `RecordedAt` = VALUES(`RecordedAt`)");
        }
    }

    public static async Task<PlatformVisitorReport> ReportAsync(IPRODbContext db, int days, DateTime nowUtc)
    {
        var cutoff = nowUtc.AddDays(-days);
        var previousCutoff = cutoff.AddDays(-days);
        var period = db.PlatformPageViews.AsNoTracking().Where(v => v.CreatedAt >= cutoff && v.CreatedAt <= nowUtc);

        var totalViews = await period.CountAsync();
        var uniqueVisitors = await period.Select(v => v.VisitorHash).Distinct().CountAsync();
        var previousViews = await db.PlatformPageViews.AsNoTracking()
            .CountAsync(v => v.CreatedAt >= previousCutoff && v.CreatedAt < cutoff);

        var daily = (await period
                .GroupBy(v => v.CreatedAt.Date)
                .Select(g => new { Date = g.Key, Views = g.Count(), Visitors = g.Select(v => v.VisitorHash).Distinct().Count() })
                .OrderBy(d => d.Date)
                .ToListAsync())
            .Select(d => new PlatformVisitorDay(d.Date, d.Views, d.Visitors))
            .ToList();

        // One projection for the three origin-shaped breakdowns: the campaign label is computed, so
        // it is grouped here rather than in SQL.
        var rows = await period
            .Select(v => new { v.Path, v.Host, v.ReferrerHost, v.Source, v.Medium, v.Campaign, v.VisitorHash })
            .ToListAsync();

        static List<PlatformVisitorBreakdown> Group(IEnumerable<(string Label, string Visitor)> items) =>
            items.GroupBy(i => i.Label)
                .Select(g => new PlatformVisitorBreakdown(g.Key, g.Count(), g.Select(i => i.Visitor).Distinct().Count()))
                .OrderByDescending(b => b.Views).ThenBy(b => b.Label, StringComparer.Ordinal)
                .Take(25)
                .ToList();

        var byPage = Group(rows.Select(r => (r.Path, r.VisitorHash)));
        var byHost = Group(rows.Select(r => (r.Host, r.VisitorHash)));
        var byReferrer = Group(rows.Select(r => (r.ReferrerHost.Length == 0 ? DirectLabel : r.ReferrerHost, r.VisitorHash)));
        var byCampaign = Group(rows.Select(r => (OriginLabel(r.Source, r.Medium, r.Campaign, r.ReferrerHost), r.VisitorHash)));

        var signups = await db.PlatformSignupOrigins.AsNoTracking()
            .Where(o => o.RecordedAt >= cutoff && o.RecordedAt <= nowUtc)
            .Select(o => new { o.Source, o.Medium, o.Campaign, o.ReferrerHost })
            .ToListAsync();
        var signupsByOrigin = signups
            .GroupBy(o => OriginLabel(o.Source, o.Medium, o.Campaign, o.ReferrerHost))
            .Select(g => new PlatformSignupCount(g.Key, g.Count()))
            .OrderByDescending(s => s.SignUps).ThenBy(s => s.Label, StringComparer.Ordinal)
            .ToList();

        return new PlatformVisitorReport
        {
            PeriodDays = days,
            TotalViews = totalViews,
            UniqueVisitors = uniqueVisitors,
            PreviousViews = previousViews,
            SignUps = signups.Count,
            SignUpsWithOrigin = signups.Count(o => o.Source.Length > 0 || o.Medium.Length > 0 || o.Campaign.Length > 0 || o.ReferrerHost.Length > 0),
            Daily = daily,
            ByPage = byPage,
            ByHost = byHost,
            ByReferrer = byReferrer,
            ByCampaign = byCampaign,
            SignUpsByOrigin = signupsByOrigin
        };
    }

    private static string Host(string? value, int max) => Cut((value ?? string.Empty).Trim().Trim('.').ToLowerInvariant(), max);
    private static string Tag(string? value) => Cut((value ?? string.Empty).Trim().ToLowerInvariant(), 100);
    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
