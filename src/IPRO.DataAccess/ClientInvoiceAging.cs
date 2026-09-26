using System;
using System.Collections.Generic;
using System.Linq;
using IPRO.Entities;

namespace IPRO.DataAccess;

// 523 (2026-09-26), slice 2: who owes what, and for how long -- the unpaid invoices grouped by
// client and bucketed by days past due, in the adviser's own day. Pure, like ClientInvoiceGlance:
// the caller hands over the invoices (with their clients loaded) and "today"; a test pins every
// bucket. Unpaid means sent or approved; estimates, drafts, void and paid documents are not owed.
public sealed class ClientInvoiceAgingItem
{
    public int InvoiceId { get; init; }
    public string DocumentNumber { get; init; } = string.Empty;
    public decimal Total { get; init; }
    public DateTime? DueDate { get; init; }
    public int DaysPastDue { get; init; }              // 0 when not yet due, or when there is no due date
    public DateTime? LastReminderSentAt { get; init; }
    public bool IsOverdue => DaysPastDue > 0;
}

public sealed class ClientInvoiceAgingRow
{
    public int ClientId { get; init; }
    public string ClientName { get; init; } = string.Empty;
    public string? ClientEmail { get; init; }
    public decimal Current { get; init; }
    public decimal Days1To30 { get; init; }
    public decimal Days31To60 { get; init; }
    public decimal Days61To90 { get; init; }
    public decimal Over90 { get; init; }
    public decimal Total { get; init; }
    public IReadOnlyList<ClientInvoiceAgingItem> Invoices { get; init; } = Array.Empty<ClientInvoiceAgingItem>();
    public decimal PastDue => Days1To30 + Days31To60 + Days61To90 + Over90;
    public int OverdueCount => Invoices.Count(i => i.IsOverdue);
}

public sealed class ClientInvoiceAging
{
    public IReadOnlyList<ClientInvoiceAgingRow> Rows { get; init; } = Array.Empty<ClientInvoiceAgingRow>();
    public decimal Current { get; init; }
    public decimal Days1To30 { get; init; }
    public decimal Days31To60 { get; init; }
    public decimal Days61To90 { get; init; }
    public decimal Over90 { get; init; }
    public decimal Total { get; init; }
    public int InvoiceCount { get; init; }
    public string Currency { get; init; } = "CAD";

    // 0 current, 1 for 1-30 days past due, 2 for 31-60, 3 for 61-90, 4 for over 90.
    public static int Bucket(int daysPastDue) =>
        daysPastDue <= 0 ? 0 : daysPastDue <= 30 ? 1 : daysPastDue <= 60 ? 2 : daysPastDue <= 90 ? 3 : 4;

    public static ClientInvoiceAging From(IEnumerable<ClientInvoice> invoices, DateTime today)
    {
        var day = today.Date;
        var unpaid = invoices
            .Where(i => i.DocumentType == ClientInvoiceDocumentType.Invoice
                        && i.Status is ClientInvoiceStatus.Sent or ClientInvoiceStatus.Approved)
            .ToList();

        var rows = unpaid
            .GroupBy(i => i.ClientId)
            .Select(g =>
            {
                var items = g
                    .Select(i => new ClientInvoiceAgingItem
                    {
                        InvoiceId = i.Id,
                        DocumentNumber = i.DocumentNumber,
                        Total = i.Total,
                        DueDate = i.DueDate,
                        DaysPastDue = i.DueDate.HasValue && i.DueDate.Value.Date < day ? (int)(day - i.DueDate.Value.Date).TotalDays : 0,
                        LastReminderSentAt = i.LastReminderSentAt
                    })
                    .OrderByDescending(i => i.DaysPastDue)
                    .ThenBy(i => i.DocumentNumber, StringComparer.Ordinal)
                    .ToList();
                var client = g.First().Client;
                var name = $"{client?.FirstName} {client?.LastName}".Trim();
                decimal Sum(int bucket) => items.Where(i => Bucket(i.DaysPastDue) == bucket).Sum(i => i.Total);
                return new ClientInvoiceAgingRow
                {
                    ClientId = g.Key,
                    ClientName = string.IsNullOrWhiteSpace(name) ? $"Client #{g.Key}" : name,
                    ClientEmail = client?.Email,
                    Current = Sum(0),
                    Days1To30 = Sum(1),
                    Days31To60 = Sum(2),
                    Days61To90 = Sum(3),
                    Over90 = Sum(4),
                    Total = items.Sum(i => i.Total),
                    Invoices = items
                };
            })
            .OrderByDescending(r => r.PastDue)      // the longest-owed money first
            .ThenByDescending(r => r.Total)
            .ThenBy(r => r.ClientName, StringComparer.Ordinal)
            .ToList();

        var currency = unpaid
            .GroupBy(i => string.IsNullOrWhiteSpace(i.Currency) ? "CAD" : i.Currency.Trim())
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? "CAD";

        return new ClientInvoiceAging
        {
            Rows = rows,
            Current = rows.Sum(r => r.Current),
            Days1To30 = rows.Sum(r => r.Days1To30),
            Days31To60 = rows.Sum(r => r.Days31To60),
            Days61To90 = rows.Sum(r => r.Days61To90),
            Over90 = rows.Sum(r => r.Over90),
            Total = rows.Sum(r => r.Total),
            InvoiceCount = unpaid.Count,
            Currency = currency
        };
    }
}
