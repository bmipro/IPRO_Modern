using System;

namespace IPRO.DataAccess;

// 518 (2026-09-23): whose day it is (INVARIANTS rule 10). The server runs in UTC; a person's
// "today" -- for follow-ups due today, for the last day a code works -- is the day in their zone.
public static class PlatformDay
{
    // The calendar day a clock in `zone` shows at `nowUtc` (Eastern when the zone is unset).
    public static DateTime Today(DateTime nowUtc, string? zone) =>
        AgentLocalTime.FromUtc(nowUtc, AgentLocalTime.Normalize(zone)).Date;

    // A code's Expires date is the LAST day it works: it has expired only once that day has ended
    // in the platform's zone. Before 518 the date was read as its first moment in UTC, so a code
    // "expiring 30 September" stopped on the evening of the 29th in Toronto (TODO 504 item 13).
    public static bool HasExpired(DateTime? expiresAt, DateTime nowUtc, string? zone) =>
        expiresAt.HasValue && Today(nowUtc, zone) > expiresAt.Value.Date;
}
