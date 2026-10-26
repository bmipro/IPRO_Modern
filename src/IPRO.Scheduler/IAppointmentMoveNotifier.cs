namespace IPRO.Scheduler;

// 608 (2026-10-26): the owner, after dragging his test appointment in Google: "yes, email the client when an
// appointment moves". A move arrives through the Google sync (iPro itself has no way to change an appointment's
// time), but the letter needs the client's portal address, which only the web app works out (ClientPortalUrls).
// So the sync reports each follow-up it moved, once the move is saved, and the web app's AppointmentMoveNotifier
// decides whether it is a client's appointment and writes to them.
public interface IAppointmentMoveNotifier
{
    Task NotifyAsync(int followUpId, DateTime movedFrom, DateTime movedTo);
}
