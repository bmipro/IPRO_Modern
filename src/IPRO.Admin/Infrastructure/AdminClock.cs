using System;
using IPRO.DataAccess;
using Microsoft.Extensions.Configuration;

namespace IPRO.Admin.Infrastructure;

// 432 (2026-09-09): the Admin header clock. The app runs on Linux App Service in UTC, so the old
// DateTime.Now read "8:42 PM" at 4:42 in the afternoon in Toronto. The clock now shows the
// platform's own time zone -- Admin:TimeZone in configuration, one of the AgentLocalTime names,
// Eastern when unset -- with a short label so the reader knows which clock they are looking at.
public static class AdminClock
{
    public const string ConfigKey = "Admin:TimeZone";

    public static string Zone(IConfiguration configuration) => AgentLocalTime.Normalize(configuration[ConfigKey]);

    public static string Format(DateTime utcNow, string? zone) =>
        $"{AgentLocalTime.FromUtc(utcNow, zone):MMM d, yyyy h:mm tt} {Label(zone)}";

    public static string Label(string? zone) => AgentLocalTime.Normalize(zone) switch
    {
        "(GMT-06:00) Central Time (US & Canada)" => "CT",
        "(GMT-07:00) Mountain Time (US & Canada)" => "MT",
        "(GMT-08:00) Pacific Time (US & Canada)" => "PT",
        "(GMT-04:00) Atlantic Time (Canada)" => "AT",
        "(GMT-03:30) Newfoundland" => "NT",
        _ => "ET"
    };
}
