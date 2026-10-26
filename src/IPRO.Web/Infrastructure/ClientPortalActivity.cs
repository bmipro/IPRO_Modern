using System.Net;
using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// 598 (the owner's decisions of 2026-10-24, from reading the portal for the System Book).
//
// Read-only: when the adviser's plan has no client portal -- a downgrade, or a subscription that lapsed --
// the portal stays open for reading (documents, messages, invoices, appointments) but takes nothing new
// the adviser could neither see nor answer: no messages, uploads, deletes or appointment requests. My
// Information and email preferences stay open; the first feeds the client record every plan has, the
// second is the client's right to stop email.
//
// Notice: a client's message, upload or appointment request emails the adviser, at most once an hour
// per adviser -- only the first item of a quiet hour sends, so a busy client can't fill the adviser's
// inbox or the platform's hourly email allowance.
public static class ClientPortalActivity
{
    public const string ReadOnlyMessage =
        "This portal is read-only at the moment, so new messages, documents and appointment requests can't be sent here. Please contact your advisor directly.";

    public static int? AdviserId(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue("AgentUserId"), out var id) ? id : null;

    public static async Task<bool> IsReadOnlyAsync(IPackageEntitlementService entitlements, ClaimsPrincipal user)
    {
        var adviserId = AdviserId(user);
        if (adviserId is null) return true;
        return !await entitlements.HasAccessAsync(adviserId.Value, PackageFeatureCodes.ClientPortal);
    }

    public enum Kind { Message, Document, AppointmentRequest }

    // True when this item is the only client activity for the adviser in the last hour, i.e. the one
    // that should send. Call after the item is saved.
    public static async Task<bool> IsFirstInQuietHourAsync(IPRODbContext db, int adviserId, DateTime nowUtc)
    {
        var since = nowUtc.AddHours(-1);
        var messages = await db.PortalMessages.CountAsync(m => m.IsFromClient && m.CreatedAt >= since && m.Client.AgentUserId == adviserId);
        var uploads = await db.PortalDocuments.CountAsync(d => d.UploadedByClient && d.UploadedAt >= since && d.Client.AgentUserId == adviserId);
        var requests = await db.PortalAppointmentRequests.CountAsync(r => r.CreatedAt >= since && r.Client.AgentUserId == adviserId);
        return messages + uploads + requests <= 1;
    }

    public static async Task NotifyAdviserAsync(IPRODbContext db, IEmailService email, IConfiguration configuration, int clientId, Kind kind, ILogger logger)
    {
        try
        {
            var client = await db.Clients.AsNoTracking().Include(c => c.AgentUser).FirstOrDefaultAsync(c => c.Id == clientId);
            if (client?.AgentUser == null || string.IsNullOrWhiteSpace(client.AgentUser.Email)) return;
            if (!await IsFirstInQuietHourAsync(db, client.AgentUserId, DateTime.UtcNow)) return;

            var name = $"{client.FirstName} {client.LastName}".Trim();
            var portal = PortalUrlHelper.GetAgentPortalBaseUrl(configuration) + "/portal";
            var (what, link, button) = kind switch
            {
                Kind.Message => ("sent you a message", $"{portal}/PortalMessages", "Open Portal Messages"),
                Kind.Document => ("uploaded a document", $"{portal}/Clients/Details/{client.Id}", "Open their client record"),
                _ => ("asked for an appointment", $"{portal}/PortalRequests", "Open Portal Requests")
            };
            var html = $"""
                <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
                  <div style="padding:22px;background:#1457d9;color:white"><h1 style="margin:0;font-size:22px">New in your client portal</h1></div>
                  <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                    <p><strong>{WebUtility.HtmlEncode(name)}</strong> {what} in your client portal.</p>
                    <p><a href="{link}" style="display:inline-block;padding:11px 18px;background:#1457d9;color:white;text-decoration:none;border-radius:6px">{button}</a></p>
                    <p style="color:#5b6b7a;font-size:13px">You get at most one of these emails an hour; anything else that arrives meanwhile is waiting in the portal.</p>
                  </div>
                </div>
                """;
            var adviser = client.AgentUser;
            var result = await email.SendDetailedAsync(adviser.Email, $"{adviser.FirstName} {adviser.LastName}".Trim(), $"New in your client portal: {name}", html);
            if (!result.Success)
                logger.LogWarning("Client portal activity notice to adviser {AdviserId} was not sent: {Message}", adviser.Id, result.Message);
        }
        catch (Exception ex)
        {
            // The client's message, upload or request is saved either way; only the email is lost.
            logger.LogWarning(ex, "Client portal activity notice for client {ClientId} failed.", clientId);
        }
    }
}
