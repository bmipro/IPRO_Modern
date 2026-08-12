using IPRO.DataAccess;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// Thin facade over AgentLocalTime (IPRO.DataAccess), which holds the actual conversion logic so
// IPRO.Billing can use it too (invoice emails). This class keeps the portal-facing extras: the
// options list for the Profile dropdown and the EF-based per-agent lookup.
public static class AgentTimeZoneHelper
{
    public const string DefaultTimeZone = AgentLocalTime.DefaultTimeZone;

    public static IReadOnlyList<string> Options { get; } =
    [
        DefaultTimeZone,
        "(GMT-06:00) Central Time (US & Canada)",
        "(GMT-07:00) Mountain Time (US & Canada)",
        "(GMT-08:00) Pacific Time (US & Canada)",
        "(GMT-04:00) Atlantic Time (Canada)",
        "(GMT-03:30) Newfoundland"
    ];

    public static DateTime ToUtc(DateTime localDateTime, string? agentTimeZone) =>
        AgentLocalTime.ToUtc(localDateTime, agentTimeZone);

    public static DateTime FromUtc(DateTime utcDateTime, string? agentTimeZone) =>
        AgentLocalTime.FromUtc(utcDateTime, agentTimeZone);

    public static DateTime? FromUtc(DateTime? utcDateTime, string? agentTimeZone) =>
        AgentLocalTime.FromUtc(utcDateTime, agentTimeZone);

    public static string Normalize(string? value) => AgentLocalTime.Normalize(value);

    public static async Task<string> ResolveForAgentAsync(IPRODbContext db, int agentId)
    {
        var timeZone = await db.AgentUsers
            .AsNoTracking()
            .Where(a => a.Id == agentId)
            .Select(a => a.TimeZone)
            .FirstOrDefaultAsync();
        return Normalize(timeZone);
    }
}
