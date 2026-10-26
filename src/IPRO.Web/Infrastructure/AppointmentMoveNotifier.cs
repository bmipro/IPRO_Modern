using System.Net;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Scheduler;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// 608 (2026-10-26): the owner moved his test appointment in Google from 2:00 AM to 9:00 PM; iPro and the
// client's portal followed, but nobody told the client. "Yes, email the client when an appointment moves."
// GoogleCalendarSyncJob reports each follow-up it moved; this writes to the client when the follow-up is a
// client's appointment (a Scheduled portal request points at it), in the same letter as the "scheduled" one
// (PortalRequestsController.Schedule: 542's letter, 530's sender and reply-to, 460's portal link). An adviser's
// own follow-up, a finished appointment, a client with no email, and a move into the past send nothing.
public sealed class AppointmentMoveNotifier : IAppointmentMoveNotifier
{
    private readonly IPRODbContext _db;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AppointmentMoveNotifier> _logger;

    public AppointmentMoveNotifier(IPRODbContext db, IEmailService email, IConfiguration configuration, ILogger<AppointmentMoveNotifier> logger)
    {
        _db = db;
        _email = email;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task NotifyAsync(int followUpId, DateTime movedFrom, DateTime movedTo)
    {
        var request = await _db.PortalAppointmentRequests.AsNoTracking()
            .Include(r => r.Client)
            .Where(r => r.ClientFollowUpId == followUpId && r.Status == PortalAppointmentRequestStatus.Scheduled)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync();
        if (request == null) return;   // the adviser's own follow-up, not a client's appointment

        var followUp = await _db.ClientFollowUps.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == followUpId && f.ClientId == request.ClientId);
        if (followUp == null || followUp.IsCompleted || string.IsNullOrWhiteSpace(request.Client.Email)) return;

        var agentId = request.Client.AgentUserId;
        var adviser = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId);
        // Tidying an old appointment in Google is not news to the client.
        if (movedTo < AgentTimeZoneHelper.FromUtc(DateTime.UtcNow, adviser?.TimeZone)) return;

        var loginUrl = ClientPortalUrls.LoginUrl(await ClientPortalUrls.GetBaseUrlAsync(_db, agentId, _configuration), agentId, _configuration);
        var html = ClientLetter.Html(adviser, request.Client.FirstName, new[]
        {
            $"Your appointment has moved to <strong>{When(movedTo)}</strong>. It was {When(movedFrom)}.",
            string.IsNullOrWhiteSpace(request.Notes) ? "" : $"Notes: {WebUtility.HtmlEncode(request.Notes)}",
            $"You can review this anytime from your client portal: <a href=\"{loginUrl}\">{loginUrl}</a>"
        }, closing: ClientLetter.QuestionsLine(adviser));

        var business = AdviserSender.BusinessName(adviser);
        var subject = business.Length == 0 ? "Your appointment has moved" : $"Your appointment with {business} has moved";
        var clientName = $"{request.Client.FirstName} {request.Client.LastName}".Trim();
        var result = await _email.SendDetailedAsync(request.Client.Email, clientName, subject, html,
            customArgs: AdviserSender.Tags(adviser, EmailStreams.Notify),
            replyToEmail: AdviserSender.ReplyToEmail(adviser), replyToName: AdviserSender.ReplyToName(adviser));
        if (!result.Success)
        {
            _logger.LogWarning("The appointment-moved email to client {ClientId} (follow-up {FollowUpId}) was not sent: {Message}",
                request.ClientId, followUpId, result.Message);
        }
    }

    // The adviser's wall clock, as the "scheduled" email words it; a bare date (moved to an all-day event in
    // Google) has no hour to give.
    internal static string When(DateTime time) => time.TimeOfDay == TimeSpan.Zero
        ? time.ToString("dddd, MMMM d, yyyy")
        : time.ToString("dddd, MMMM d, yyyy 'at' h:mm tt");
}
