using System.Globalization;
using IPRO.Web.Controllers;

namespace IPRO.Web.Infrastructure;

// 565 (2026-10-11): the Email Activity list, cut down to what a person can use. The screen showed
// every email ever sent on one page (the owner, two months in: "it will be pages and pages and hard to
// manage"). Now it opens on the last 30 days, 25 to a page, with a tab for what needs attention and
// a search by subject or recipient. Pure: the controller hands over the rows; a test pins the rest.
public sealed record EmailActivityPeriod(string Key, string Label, string Words, int? Days);

public sealed class EmailActivityListing
{
    public const int PageSize = 25;
    public const string Attention = "attention";
    public const string DefaultPeriod = "30";

    public static readonly IReadOnlyList<EmailActivityPeriod> Periods = new[]
    {
        new EmailActivityPeriod("7", "7 days", "the last 7 days", 7),
        new EmailActivityPeriod("30", "30 days", "the last 30 days", 30),
        new EmailActivityPeriod("90", "90 days", "the last 90 days", 90),
        new EmailActivityPeriod("365", "12 months", "the last 12 months", 365),
        new EmailActivityPeriod("all", "All", "all time", null),
    };

    public static readonly IReadOnlyList<string> TypeKeys = new[] { "all", Attention, "newsletter", "drip", "ecard", "eletter", "poll", "didyouknow", "invoice" };

    public IReadOnlyList<EmailActivityRow> Rows { get; private init; } = Array.Empty<EmailActivityRow>();
    public IReadOnlyDictionary<string, int> Counts { get; private init; } = new Dictionary<string, int>();
    public int AllCount { get; private init; }
    public int AttentionCount { get; private init; }
    public int Total { get; private init; }          // in the chosen tab, before paging
    public int Page { get; private init; } = 1;
    public int PageCount { get; private init; } = 1;
    public int From => Total == 0 ? 0 : (Page - 1) * PageSize + 1;
    public int To => Math.Min(Page * PageSize, Total);
    public string Type { get; private init; } = "all";
    public EmailActivityPeriod Period { get; private init; } = Periods[1];
    public string Query { get; private init; } = string.Empty;

    public static EmailActivityPeriod PeriodFor(string? key)
    {
        var wanted = (key ?? string.Empty).Trim().ToLowerInvariant();
        return Periods.FirstOrDefault(p => p.Key == wanted) ?? Periods.First(p => p.Key == DefaultPeriod);
    }

    // The moment the period starts, or null for all time.
    public static DateTime? Since(EmailActivityPeriod period, DateTime utcNow) =>
        period.Days is int days ? utcNow.AddDays(-days) : null;

    public static string CleanType(string? type)
    {
        var wanted = (type ?? string.Empty).Trim().ToLowerInvariant();
        return TypeKeys.Contains(wanted) ? wanted : "all";
    }

    // What was typed in the search box: trimmed, at most 80 characters, and nothing at all when it is
    // a single character (which would match almost every row).
    public static string CleanQuery(string? query)
    {
        var text = string.Join(' ', (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > 80) text = text[..80].TrimEnd();
        return text.Length < 2 ? string.Empty : text;
    }

    // A row belongs to a period by its date alone. One with no date yet (not scheduled) is not old,
    // so it shows in every period; so does one scheduled for later.
    public static List<EmailActivityRow> InPeriod(IEnumerable<EmailActivityRow> rows, DateTime? since) =>
        rows.Where(r => since == null || r.When == null || r.When >= since).ToList();

    public static bool NeedsAttention(EmailActivityRow row) => row.Failed > 0 || row.DisplayStatus == "Failed";

    public static bool TextMatches(EmailActivityRow row, string query) =>
        (row.Subject ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (row.Detail ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase);

    // rowsInPeriod: the adviser's sends, already cut to the period. recipientMatches: the sends that
    // reached someone whose name or address holds the search text (found by the caller, in the database).
    public static EmailActivityListing Build(IEnumerable<EmailActivityRow> rowsInPeriod, string? type, EmailActivityPeriod period, string? query, int page,
        ISet<(string TypeKey, int Id)>? recipientMatches = null)
    {
        var text = CleanQuery(query);
        var rows = rowsInPeriod.OrderByDescending(r => r.When ?? DateTime.MinValue).ToList();
        if (text.Length > 0)
        {
            rows = rows.Where(r => TextMatches(r, text) || (recipientMatches?.Contains((r.TypeKey, r.Id)) ?? false)).ToList();
        }

        var chosen = CleanType(type);
        var inTab = chosen switch
        {
            "all" => rows,
            Attention => rows.Where(NeedsAttention).ToList(),
            _ => rows.Where(r => r.TypeKey == chosen).ToList()
        };

        var pageCount = Math.Max(1, (int)Math.Ceiling(inTab.Count / (double)PageSize));
        var current = Math.Clamp(page, 1, pageCount);
        return new EmailActivityListing
        {
            Rows = inTab.Skip((current - 1) * PageSize).Take(PageSize).ToList(),
            Counts = rows.GroupBy(r => r.TypeKey).ToDictionary(g => g.Key, g => g.Count()),
            AllCount = rows.Count,
            AttentionCount = rows.Count(NeedsAttention),
            Total = inTab.Count,
            Page = current,
            PageCount = pageCount,
            Type = chosen,
            Period = period,
            Query = text
        };
    }

    public int CountFor(string typeKey) => typeKey switch
    {
        "all" => AllCount,
        Attention => AttentionCount,
        _ => Counts.TryGetValue(typeKey, out var count) ? count : 0
    };

    // The list's address with one thing changed. What is left at its default is left out, so the
    // plain address is the plain screen; a change of tab, period or search starts again at page 1.
    public string Url(string? type = null, string? period = null, string? query = null, int page = 1) =>
        Address(type ?? Type, period ?? Period.Key, query ?? Query, page);

    public static string Address(string type, string period, string query, int page)
    {
        var parts = new List<string>();
        if (CleanType(type) != "all") parts.Add("type=" + CleanType(type));
        if (PeriodFor(period).Key != DefaultPeriod) parts.Add("period=" + PeriodFor(period).Key);
        if (CleanQuery(query).Length > 0) parts.Add("q=" + Uri.EscapeDataString(CleanQuery(query)));
        if (page > 1) parts.Add("page=" + page.ToString(CultureInfo.InvariantCulture));
        return "/portal/EmailActivity" + (parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts));
    }

    // The list a reader came from, rebuilt from the address their browser sent, so "Back" on the detail
    // page returns to the same period, tab, search and page. Only the four known values are read, and
    // each is cleaned: nothing from the address is echoed as it came.
    public static string BackAddress(string? referer, string host)
    {
        if (!Uri.TryCreate(referer, UriKind.Absolute, out var from)) return Address("all", DefaultPeriod, string.Empty, 1);
        if (!string.Equals(from.Host, host, StringComparison.OrdinalIgnoreCase)) return Address("all", DefaultPeriod, string.Empty, 1);
        if (!from.AbsolutePath.TrimEnd('/').EndsWith("/EmailActivity", StringComparison.OrdinalIgnoreCase)) return Address("all", DefaultPeriod, string.Empty, 1);

        var values = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(from.Query);
        string One(string key) => values.TryGetValue(key, out var v) ? v.ToString() : string.Empty;
        var page = int.TryParse(One("page"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 1, 100000) : 1;
        return Address(One("type"), One("period"), One("q"), page);
    }
}
