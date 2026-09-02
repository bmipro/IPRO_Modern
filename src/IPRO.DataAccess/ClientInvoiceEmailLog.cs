using System;
using System.Threading.Tasks;
using IPRO.Entities;

namespace IPRO.DataAccess;

// 452 (2026-09-02): the one way an invoice email is recorded -- the portal's Send (and resend) and
// the overdue reminder job both come through here, so the invoice's email history and the delivery
// pipeline's correlation (ProviderMessageId) can never drift apart between the two senders.
public static class ClientInvoiceEmailLog
{
    public static async Task<ClientInvoiceEmail> RecordAsync(
        IPRODbContext db, ClientInvoice invoice, ClientInvoiceEmailKind kind,
        string toEmail, string subject, bool success, string? providerMessageId, string? failureMessage)
    {
        var now = DateTime.UtcNow;
        var row = new ClientInvoiceEmail
        {
            ClientInvoiceId = invoice.Id,
            AgentUserId = invoice.AgentUserId,
            ClientId = invoice.ClientId,
            Kind = kind,
            ToEmail = Clip(toEmail, 200),
            Subject = Clip(subject, 300),
            ProviderMessageId = Clip(providerMessageId, 200),
            Status = success ? ClientInvoiceEmailStatus.Sent : ClientInvoiceEmailStatus.Failed,
            LastEvent = success ? "sent" : "failed",
            SentAt = success ? now : null,
            FailedAt = success ? null : now,
            FailureReason = success ? string.Empty : Clip(failureMessage, 500),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ClientInvoiceEmails.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    private static string Clip(string? value, int max)
    {
        value ??= string.Empty;
        return value.Length <= max ? value : value[..max];
    }
}
