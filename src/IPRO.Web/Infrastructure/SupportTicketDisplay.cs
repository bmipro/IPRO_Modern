namespace IPRO.Web.Infrastructure;

// 502 (2026-09-19): the owner wrote a support ticket at 12:38 p.m. Eastern and both apps stamped it
// "4:38 PM": the support screens printed the stored UTC value as it stood. The portal shows the
// adviser's own time zone (their profile's; Eastern when unset), like invoices and Email Activity do.
// SuperAdmin's screens use AdminClock.Format, the same zone and label as its header clock.
public static class SupportTicketDisplay
{
    public static string Time(DateTime utc, string? agentTimeZone) =>
        AgentTimeZoneHelper.FromUtc(utc, agentTimeZone).ToString("MMM d, yyyy h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
}
