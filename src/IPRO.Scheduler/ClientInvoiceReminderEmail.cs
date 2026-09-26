using IPRO.Entities;

namespace IPRO.Scheduler;

// 523 (2026-09-26), slice 2: the overdue reminder's words, shared by the nightly
// OverdueInvoiceReminderJob and the "Send reminder" button on the adviser's aging page, so the
// client reads the same email whichever way it was sent.
public static class ClientInvoiceReminderEmail
{
    public static (string Subject, string Html) Build(ClientInvoice invoice, string url)
    {
        var companyName = invoice.AgentUser?.CompanyName ?? string.Empty;
        var html = $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              <div style="padding:22px;background:#1457d9;color:white"><h1 style="margin:0;font-size:24px">{System.Net.WebUtility.HtmlEncode(companyName)}</h1></div>
              <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                <p>This is a reminder that invoice <strong>{System.Net.WebUtility.HtmlEncode(invoice.DocumentNumber)}</strong> for <strong>${invoice.Total:N2} {invoice.Currency}</strong> is now overdue.</p>
                <p><a href="{url}" style="display:inline-block;padding:11px 18px;background:#1457d9;color:white;text-decoration:none;border-radius:6px">View Invoice</a></p>
              </div>
            </div>
            """;
        var subject = $"Reminder: Invoice {invoice.DocumentNumber} is overdue";
        return (subject, html);
    }
}
