namespace IPRO.Web.Infrastructure;

public static class AgentTimeZoneHelper
{
    public const string DefaultTimeZone = "(GMT-05:00) Eastern Time (US & Canada)";

    public static IReadOnlyList<string> Options { get; } =
    [
        DefaultTimeZone,
        "(GMT-06:00) Central Time (US & Canada)",
        "(GMT-07:00) Mountain Time (US & Canada)",
        "(GMT-08:00) Pacific Time (US & Canada)",
        "(GMT-04:00) Atlantic Time (Canada)",
        "(GMT-03:30) Newfoundland"
    ];

    public static DateTime ToUtc(DateTime localDateTime, string? agentTimeZone)
    {
        var zone = FindTimeZone(agentTimeZone);
        var unspecified = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }

    public static DateTime FromUtc(DateTime utcDateTime, string? agentTimeZone)
    {
        var zone = FindTimeZone(agentTimeZone);
        var utc = utcDateTime.Kind == DateTimeKind.Utc
            ? utcDateTime
            : DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
    }

    public static DateTime? FromUtc(DateTime? utcDateTime, string? agentTimeZone) =>
        utcDateTime.HasValue ? FromUtc(utcDateTime.Value, agentTimeZone) : null;

    public static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DefaultTimeZone : value.Trim();

    private static TimeZoneInfo FindTimeZone(string? agentTimeZone)
    {
        var id = ToSystemTimeZoneId(agentTimeZone);
        foreach (var candidate in CandidateIds(id))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(candidate);
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return TimeZoneInfo.Utc;
    }

    private static IEnumerable<string> CandidateIds(string id)
    {
        yield return id;
        yield return id switch
        {
            "America/Toronto" => "Eastern Standard Time",
            "America/Chicago" => "Central Standard Time",
            "America/Denver" => "Mountain Standard Time",
            "America/Los_Angeles" => "Pacific Standard Time",
            "America/Halifax" => "Atlantic Standard Time",
            "America/St_Johns" => "Newfoundland Standard Time",
            "Eastern Standard Time" => "America/Toronto",
            "Central Standard Time" => "America/Chicago",
            "Mountain Standard Time" => "America/Denver",
            "Pacific Standard Time" => "America/Los_Angeles",
            "Atlantic Standard Time" => "America/Halifax",
            "Newfoundland Standard Time" => "America/St_Johns",
            _ => "America/Toronto"
        };
    }

    private static string ToSystemTimeZoneId(string? agentTimeZone)
    {
        var value = Normalize(agentTimeZone);
        return value switch
        {
            "(GMT-05:00) Eastern Time (US & Canada)" => "America/Toronto",
            "(GMT-06:00) Central Time (US & Canada)" => "America/Chicago",
            "(GMT-07:00) Mountain Time (US & Canada)" => "America/Denver",
            "(GMT-08:00) Pacific Time (US & Canada)" => "America/Los_Angeles",
            "(GMT-04:00) Atlantic Time (Canada)" => "America/Halifax",
            "(GMT-03:30) Newfoundland" => "America/St_Johns",
            _ => value
        };
    }
}
