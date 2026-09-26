using IPRO.DataAccess;
using IPRO.Entities;

namespace IPRO.Scheduler;

// 523 (2026-09-26), slices 2 and 3: the reminder email's words, shared by the daily
// OverdueInvoiceReminderJob and the "Send reminder" button on the adviser's aging page, so the
// client reads the same email whichever way it was sent. Slice 3 gave each stage its own wording,
// the adviser's own when they set one (ClientInvoiceReminderSchedule).
public static class ClientInvoiceReminderEmail
{
    public static (string Subject, string Html) Build(ClientInvoice invoice, string url, string stage, ClientInvoiceReminderSettings settings, DateTime today)
    {
        var daysFromDue = invoice.DueDate.HasValue ? (int)(today.Date - invoice.DueDate.Value.Date).TotalDays : 0;
        var companyName = invoice.AgentUser?.CompanyName ?? string.Empty;
        var paragraph = ClientInvoiceReminderSchedule.Fill(ClientInvoiceReminderSchedule.MessageFor(settings, stage), invoice, daysFromDue);
        var html = $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              <div style="padding:22px;background:#1457d9;color:white"><h1 style="margin:0;font-size:24px">{System.Net.WebUtility.HtmlEncode(companyName)}</h1></div>
              <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                <p>{paragraph}</p>
                <p><a href="{url}" style="display:inline-block;padding:11px 18px;background:#1457d9;color:white;text-decoration:none;border-radius:6px">View Invoice</a></p>
              </div>
            </div>
            """;
        return (ClientInvoiceReminderSchedule.SubjectFor(stage, invoice.DocumentNumber, daysFromDue), html);
    }

    // The button's form: the overdue wording, in the adviser's day.
    public static (string Subject, string Html) Build(ClientInvoice invoice, string url, ClientInvoiceReminderSettings settings, DateTime today) =>
        Build(invoice, url, ClientInvoiceReminderStages.OverdueFirst, settings, today);
}
