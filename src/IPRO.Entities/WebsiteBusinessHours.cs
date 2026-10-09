using System.Globalization;

namespace IPRO.Entities;

// 556 (2026-10-07). The bakery's site showed no hours anywhere, and a bakery's first question from a
// phone is "are they open?". Hours are entered once (My Website > Footer, beside the phone and the
// address; stored inside FooterSettingsJson, so no column) and shown in the footer, the contact card,
// the "about us" card, under the first button of the page, and in the details search engines read.
// One opening and one closing time a day; a closing time at or before the opening time runs past
// midnight. Anything else (a lunch break, a holiday week) goes in the note.
public class WebsiteBusinessHours
{
    public const int NoteMaxLength = 200;
    public const char Dash = (char)0x2013;   // en dash
    public const char Dot = (char)0xB7;      // middle dot

    // Monday first: the order the week is written in.
    public static readonly DayOfWeek[] WeekOrder =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    };

    public List<WebsiteDayHours> Days { get; set; } = new();
    public string Note { get; set; } = string.Empty;
    // 557: the footer line is a choice, off unless ticked (the owner, of the bakery's footer: "It is ugly").
    public bool ShowInFooter { get; set; }
    // 559: each other place is a choice too, on unless unticked (hours saved before 559 keep showing).
    public bool ShowOnContact { get; set; } = true;
    public bool ShowOnAbout { get; set; } = true;
    public bool ShowOpenNow { get; set; } = true;

    // Nothing shows anywhere until at least one day has hours.
    public bool IsSet => Days.Any(d => d.IsOpen);

    public WebsiteDayHours For(DayOfWeek day) =>
        Days.FirstOrDefault(d => d.Day == (int)day) ?? new WebsiteDayHours { Day = (int)day };

    // Seven days in week order, times as HH:mm or empty, the note trimmed. Idempotent.
    public WebsiteBusinessHours Normalized()
    {
        var note = (Note ?? string.Empty).Trim();
        return new WebsiteBusinessHours
        {
            Note = note.Length > NoteMaxLength ? note[..NoteMaxLength].TrimEnd() : note,
            ShowInFooter = ShowInFooter,
            ShowOnContact = ShowOnContact,
            ShowOnAbout = ShowOnAbout,
            ShowOpenNow = ShowOpenNow,
            Days = WeekOrder.Select(day =>
            {
                var source = (Days ?? new()).FirstOrDefault(d => d.Day == (int)day);
                var opens = WebsiteDayHours.Clean(source?.Opens);
                var closes = WebsiteDayHours.Clean(source?.Closes);
                var open = opens.Length > 0 && closes.Length > 0 && opens != closes;
                return new WebsiteDayHours { Day = (int)day, Opens = open ? opens : string.Empty, Closes = open ? closes : string.Empty };
            }).ToList()
        };
    }

    // 557: the times the form offers, every quarter hour, so a time cannot be left half entered (a
    // browser's time box could be: "08:30 --" with no AM/PM, and the page then refused to save). A
    // stored time that is not on a quarter hour is offered too, in its place, so it is seen and kept.
    public const string DefaultOpens = "09:00";
    public const string DefaultCloses = "17:00";

    public static IReadOnlyList<(string Value, string Label)> TimeChoices(string? current = null)
    {
        var values = Enumerable.Range(0, 96).Select(i => TimeSpan.FromMinutes(i * 15).ToString(@"hh\:mm", CultureInfo.InvariantCulture)).ToList();
        var own = WebsiteDayHours.Clean(current);
        if (own.Length > 0 && !values.Contains(own)) values.Add(own);
        return values.OrderBy(v => v, StringComparer.Ordinal).Select(v => (v, Clock(v))).ToList();
    }

    // What the hours form posts: a row a day. A day ticked open without both times (or with the
    // two the same) is closed, and named in halfSet so the owner is told rather than shown wrong hours.
    public static WebsiteBusinessHours FromEntries(
        IEnumerable<(DayOfWeek Day, bool Open, string? Opens, string? Closes)> entries, string? note, out List<string> halfSet)
    {
        halfSet = new List<string>();
        var hours = new WebsiteBusinessHours { Note = note ?? string.Empty };
        foreach (var entry in entries)
        {
            var opens = WebsiteDayHours.Clean(entry.Opens);
            var closes = WebsiteDayHours.Clean(entry.Closes);
            var whole = opens.Length > 0 && closes.Length > 0 && opens != closes;
            if (entry.Open && !whole) halfSet.Add(entry.Day.ToString());
            hours.Days.Add(new WebsiteDayHours { Day = (int)entry.Day, Opens = entry.Open && whole ? opens : string.Empty, Closes = entry.Open && whole ? closes : string.Empty });
        }
        return hours.Normalized();
    }

    // "7:30 a.m.", "6 p.m.", "noon", "midnight" -- the way a shop door has it.
    public static string Clock(string hhmm)
    {
        if (!WebsiteDayHours.TryParse(hhmm, out var time)) return string.Empty;
        if (time == TimeSpan.Zero) return "midnight";
        if (time == TimeSpan.FromHours(12)) return "noon";
        var hour = time.Hours % 12 == 0 ? 12 : time.Hours % 12;
        var minutes = time.Minutes == 0 ? string.Empty : ":" + time.Minutes.ToString("00", CultureInfo.InvariantCulture);
        return $"{hour}{minutes} {(time.Hours < 12 ? "a.m." : "p.m.")}";
    }

    public static string Range(WebsiteDayHours day) => day.IsOpen ? $"{Clock(day.Opens)} {Dash} {Clock(day.Closes)}" : "Closed";

    // The week as seven rows, for the cards.
    public IReadOnlyList<(DayOfWeek Day, string Name, string Text, bool Open)> Week() =>
        WeekOrder.Select(day => (day, day.ToString(), Range(For(day)), For(day).IsOpen)).ToList();

    // The week in as few lines as it takes, for the footer: neighbouring days with the same hours
    // share a line ("Tue-Sat 7:30 a.m. - 6 p.m.", "Sun 8 a.m. - 4 p.m.", "Mon Closed"; the dashes are en dashes).
    public IReadOnlyList<string> Lines()
    {
        var lines = new List<string>();
        var index = 0;
        while (index < WeekOrder.Length)
        {
            var text = Range(For(WeekOrder[index]));
            var last = index;
            while (last + 1 < WeekOrder.Length && Range(For(WeekOrder[last + 1])) == text) last++;
            var days = last == index
                ? Short(WeekOrder[index])
                : $"{Short(WeekOrder[index])}{Dash}{Short(WeekOrder[last])}";
            lines.Add($"{days} {text}");
            index = last + 1;
        }
        return lines;
    }

    private static string Short(DayOfWeek day) => day.ToString()[..3];

    // Whether the door is open at this moment of the business's own clock.
    public bool IsOpenAt(DateTime local)
    {
        var now = local.TimeOfDay;
        var today = For(local.DayOfWeek);
        if (today.IsOpen)
        {
            if (!today.RunsPastMidnight && now >= today.OpensAt && now < today.ClosesAt) return true;
            if (today.RunsPastMidnight && now >= today.OpensAt) return true;
        }
        // Last night's hours, when they run past midnight.
        var yesterday = For(local.AddDays(-1).DayOfWeek);
        return yesterday.IsOpen && yesterday.RunsPastMidnight && now < yesterday.ClosesAt;
    }

    // "Open now (dot) closes 6 p.m." / "Closed now (dot) opens 7:30 a.m." / "... opens tomorrow 8 a.m." /
    // "... opens Tuesday 7:30 a.m."; empty when no hours are set.
    public (bool Open, string Text) StatusAt(DateTime local)
    {
        if (!IsSet) return (false, string.Empty);
        var now = local.TimeOfDay;
        if (IsOpenAt(local))
        {
            var today = For(local.DayOfWeek);
            var yesterday = For(local.AddDays(-1).DayOfWeek);
            var fromLastNight = yesterday.IsOpen && yesterday.RunsPastMidnight && now < yesterday.ClosesAt;
            var closes = fromLastNight ? yesterday.Closes : today.Closes;
            return (true, $"Open now {Dot} closes {Clock(closes)}");
        }
        for (var ahead = 0; ahead <= 7; ahead++)
        {
            var day = For(local.AddDays(ahead).DayOfWeek);
            if (!day.IsOpen) continue;
            if (ahead == 0 && now >= day.OpensAt) continue;   // today's hours are already over
            var when = ahead == 0 ? string.Empty : ahead == 1 ? "tomorrow " : local.AddDays(ahead).DayOfWeek + " ";
            return (false, $"Closed now {Dot} opens {when}{Clock(day.Opens)}");
        }
        return (false, string.Empty);
    }

    // schema.org OpeningHoursSpecification: one entry per set of days with the same hours; a closed
    // day is left out, which is how search engines read "closed".
    public List<Dictionary<string, object?>> ToSchema() =>
        WeekOrder.Select(For).Where(d => d.IsOpen)
            .GroupBy(d => d.Opens + "|" + d.Closes)
            .Select(group => new Dictionary<string, object?>
            {
                ["@type"] = "OpeningHoursSpecification",
                ["dayOfWeek"] = group.Select(d => ((DayOfWeek)d.Day).ToString()).ToArray(),
                ["opens"] = group.First().Opens,
                ["closes"] = group.First().Closes
            }).ToList();
}

public class WebsiteDayHours
{
    public int Day { get; set; }                       // System.DayOfWeek: 0 = Sunday
    public string Opens { get; set; } = string.Empty;  // "07:30"; empty = closed that day
    public string Closes { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsOpen => TryParse(Opens, out var opens) && TryParse(Closes, out var closes) && opens != closes;
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan OpensAt => TryParse(Opens, out var time) ? time : TimeSpan.Zero;
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan ClosesAt => TryParse(Closes, out var time) ? time : TimeSpan.Zero;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool RunsPastMidnight => IsOpen && ClosesAt <= OpensAt;

    public static bool TryParse(string? value, out TimeSpan time) =>
        TimeSpan.TryParseExact((value ?? string.Empty).Trim(), new[] { @"hh\:mm", @"h\:mm" }, CultureInfo.InvariantCulture, out time)
        && time < TimeSpan.FromHours(24);

    public static string Clean(string? value) =>
        TryParse(value, out var time) ? time.ToString(@"hh\:mm", CultureInfo.InvariantCulture) : string.Empty;
}

// What kind of business the site tells search engines it is (schema.org's own names). Empty keeps
// "ProfessionalService", what every site said before 556.
public static class WebsiteBusinessKinds
{
    public const string Default = "ProfessionalService";

    public static readonly IReadOnlyList<(string Value, string Label)> Options = new List<(string, string)>
    {
        ("", "Professional service (adviser, consultant)"),
        ("AccountingService", "Accounting or bookkeeping"),
        ("InsuranceAgency", "Insurance"),
        ("FinancialService", "Financial or mortgage services"),
        ("RealEstateAgent", "Real estate"),
        ("LegalService", "Legal services"),
        ("Bakery", "Bakery"),
        ("CafeOrCoffeeShop", "Caf" + (char)0xE9 + " or coffee shop"),
        ("Restaurant", "Restaurant"),
        ("Store", "Shop or store"),
        ("HealthAndBeautyBusiness", "Salon, spa or wellness"),
        ("SportsActivityLocation", "Gym, studio or sports school"),
        ("LocalBusiness", "Another local business")
    };

    public static string Normalize(string? value) =>
        Options.Select(o => o.Value).FirstOrDefault(v => v.Length > 0 && string.Equals(v, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

    public static string SchemaType(string? value)
    {
        var kind = Normalize(value);
        return kind.Length == 0 ? Default : kind;
    }
}
