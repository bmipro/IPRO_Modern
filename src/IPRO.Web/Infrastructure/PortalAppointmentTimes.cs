using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// 606 (2026-10-26): an appointment's time is its follow-up's DueAt -- what the Google sync moves when the
// adviser drags it, what iPro's calendar shows and what the client's Upcoming lists. The request's own
// ScheduledAt is the time first given and is never updated, so after a move the client saw two times
// (the owner's 601 test: Upcoming 9:00 PM, My Requests "Scheduled ... at 2:00 AM"). "Scheduled ... at" on
// My Requests and on Portal Requests reads the follow-up while it is there, and ScheduledAt otherwise.
public static class PortalAppointmentTimes
{
    // Request id -> the appointment's current time, for the scheduled requests whose follow-up still exists.
    public static async Task<Dictionary<int, DateTime>> CurrentAsync(IPRODbContext db, IReadOnlyCollection<PortalAppointmentRequest> requests)
    {
        var linked = requests.Where(r => r.ClientFollowUpId.HasValue).ToList();
        if (linked.Count == 0) return new Dictionary<int, DateTime>();

        // A List, not an array: C# 14 binds array.Contains to a span overload EF Core 8 cannot translate (596).
        var followUpIds = linked.Select(r => r.ClientFollowUpId!.Value).Distinct().ToList();
        var followUps = await db.ClientFollowUps.AsNoTracking()
            .Where(f => followUpIds.Contains(f.Id))
            .Select(f => new { f.Id, f.ClientId, f.DueAt })
            .ToDictionaryAsync(f => f.Id);

        var times = new Dictionary<int, DateTime>();
        foreach (var request in linked)
        {
            // Only the request's own client's follow-up counts.
            if (followUps.TryGetValue(request.ClientFollowUpId!.Value, out var followUp) && followUp.ClientId == request.ClientId)
                times[request.Id] = followUp.DueAt;
        }
        return times;
    }

    public static DateTime? For(PortalAppointmentRequest request, IReadOnlyDictionary<int, DateTime>? times) =>
        times != null && times.TryGetValue(request.Id, out var current) ? current : request.ScheduledAt;
}
