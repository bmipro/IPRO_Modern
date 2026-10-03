using IPRO.DataAccess;
using IPRO.Entities;

namespace IPRO.Scheduler;

// 523 (2026-09-26), slices 2 and 3: the reminder email's words, shared by the daily
// OverdueInvoiceReminderJob and the "Send reminder" button on the adviser's aging page, so the
// client reads the same email whichever way it was sent. Slice 3 gave each stage its own wording,
// the adviser's own when they set one (ClientInvoiceReminderSchedule). 542: that wording sits in a
// letter from the adviser (ClientLetter) -- greeted, signed, with the phone to call.
public static class ClientInvoiceReminderEmail
{
    private static readonly string[] Greetings = { "hi ", "hi,", "hello", "dear ", "good morning", "good afternoon", "good evening" };

    public static (string Subject, string Html) Build(ClientInvoice invoice, string url, string stage, ClientInvoiceReminderSettings settings, DateTime today)
    {
        var daysFromDue = invoice.DueDate.HasValue ? (int)(today.Date - invoice.DueDate.Value.Date).TotalDays : 0;
        var wording = ClientInvoiceReminderSchedule.MessageFor(settings, stage);
        var paragraph = ClientInvoiceReminderSchedule.Fill(wording, invoice, daysFromDue);
        // An adviser whose own wording opens "Hi {client}," is not greeted twice.
        var greeted = Greetings.Any(g => wording.TrimStart().StartsWith(g, StringComparison.OrdinalIgnoreCase));
        var html = ClientLetter.Html(invoice.AgentUser, invoice.Client?.FirstName, new[] { paragraph },
            "View Invoice", url, $"If you have already paid, thank you. {ClientLetter.QuestionsLine(invoice.AgentUser)}", greet: !greeted);
        return (ClientInvoiceReminderSchedule.SubjectFor(stage, invoice.DocumentNumber, daysFromDue, AdviserSender.BusinessName(invoice.AgentUser)), html);
    }

    // The button's form: the overdue wording, in the adviser's day.
    public static (string Subject, string Html) Build(ClientInvoice invoice, string url, ClientInvoiceReminderSettings settings, DateTime today) =>
        Build(invoice, url, ClientInvoiceReminderStages.OverdueFirst, settings, today);
}
