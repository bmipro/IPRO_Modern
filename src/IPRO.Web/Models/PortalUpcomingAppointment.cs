namespace IPRO.Web.Models;

// 597: one line of the client portal's "Upcoming" list -- an appointment the client asked for and the
// adviser scheduled. It carries only the time and the client's own words from the request, never the
// follow-up's title or notes, which are the adviser's.
public sealed record PortalUpcomingAppointment(DateTime When, string? ClientNotes);
