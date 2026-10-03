using System.Globalization;
using System.Net;
using IPRO.Entities;

namespace IPRO.Scheduler;

// 542 (2026-10-03): the email that carries an invoice or an estimate to the client, as a letter from the
// adviser (ClientLetter): its number, amount and date, the due date, what it is for, the one button, and
// the adviser's sign-off. It was one sentence and a button; the owner's estimate in that shape landed in
// Yahoo's Spam. A paid invoice goes out as a copy and says so (A5-M-RESEND keeps it Paid).
public static class ClientInvoiceEmail
{
    private const int ItemsNamed = 3;
    private const int ItemLength = 60;

    public static string Html(ClientInvoice invoice, string url)
    {
        var estimate = invoice.DocumentType == ClientInvoiceDocumentType.Estimate;
        var kind = estimate ? "estimate" : "invoice";
        var number = WebUtility.HtmlEncode(invoice.DocumentNumber);
        var amount = WebUtility.HtmlEncode($"${invoice.Total.ToString("N2", CultureInfo.InvariantCulture)} {invoice.Currency}");
        var dated = Day(invoice.IssueDate);

        var paragraphs = new List<string>();
        if (!estimate && invoice.Status == ClientInvoiceStatus.Paid)
        {
            paragraphs.Add($"Here is a copy of invoice <strong>{number}</strong> for <strong>{amount}</strong>, dated {dated}. It has been paid in full. Thank you.");
        }
        else
        {
            var due = !estimate && invoice.DueDate.HasValue ? $", due {Day(invoice.DueDate.Value)}" : string.Empty;
            paragraphs.Add($"Here is {kind} <strong>{number}</strong> for <strong>{amount}</strong>, dated {dated}{due}.");
        }

        var items = Items(invoice);
        if (items.Length > 0) paragraphs.Add($"For: {items}.");
        if (estimate) paragraphs.Add("You can review the estimate and approve it online.");

        return ClientLetter.Html(invoice.AgentUser, invoice.Client?.FirstName, paragraphs,
            estimate ? "View estimate" : "View invoice", url, ClientLetter.QuestionsLine(invoice.AgentUser));
    }

    private static string Day(DateTime date) => date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);

    // "Bookkeeping, September; Year-end review; Tax filing; and 2 more": the first line of each item's
    // description, clipped, three at most.
    private static string Items(ClientInvoice invoice)
    {
        var names = (invoice.LineItems ?? new List<ClientInvoiceLineItem>())
            .OrderBy(i => i.SortOrder).ThenBy(i => i.Id)
            .Select(i => (i.Description ?? string.Empty).Replace("\r\n", "\n").Split('\n')[0].Trim())
            .Where(d => d.Length > 0)
            .ToList();
        if (names.Count == 0) return string.Empty;

        var named = names.Take(ItemsNamed).Select(d => WebUtility.HtmlEncode(d.Length <= ItemLength ? d : d[..(ItemLength - 3)].TrimEnd() + "...")).ToList();
        if (names.Count > ItemsNamed) named.Add($"and {names.Count - ItemsNamed} more");
        return string.Join("; ", named);
    }
}
