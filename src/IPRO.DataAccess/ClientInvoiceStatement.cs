using System;
using System.Collections.Generic;
using System.Linq;
using IPRO.Entities;

namespace IPRO.DataAccess;

// 523 (2026-09-26), slice 4: the statement for the adviser's accountant -- what was invoiced in a
// period, the tax collected by rate, what was received, and what was still owed at the period's
// end. Pure, like the glance and the aging: the caller hands over the invoices and the period; a
// test pins every number to the cent.
//
// "Invoiced" goes by the issue date (sent, approved or paid; drafts, void and declined documents
// are not revenue). "Received" goes by the day the payment was recorded, in the adviser's own
// zone. "Owed at the end" is what was issued by the period's last day and not paid by it.
public sealed class ClientInvoiceStatementRow
{
    public int InvoiceId { get; init; }
    public string DocumentNumber { get; init; } = string.Empty;
    public string ClientName { get; init; } = string.Empty;
    public DateTime IssueDate { get; init; }
    public DateTime? DueDate { get; init; }
    public ClientInvoiceStatus Status { get; init; }
    public decimal Subtotal { get; init; }
    public string TaxRegion { get; init; } = string.Empty;
    public decimal TaxRate { get; init; }
    public decimal Tax { get; init; }
    public decimal Total { get; init; }
    public DateTime? PaidOn { get; init; }              // the adviser's own day
    public ClientInvoicePaymentMethod? PaidMethod { get; init; }
}

public sealed class ClientInvoiceTaxLine
{
    public string Region { get; init; } = string.Empty;
    public decimal Rate { get; init; }
    public int Count { get; init; }
    public decimal Subtotal { get; init; }
    public decimal Tax { get; init; }
    public decimal Total { get; init; }
    public string Label => Rate == 0 ? "No tax" : $"{(string.IsNullOrWhiteSpace(Region) ? "Tax" : Region)} {Rate * 100:0.###}%";
}

public sealed class ClientInvoiceStatement
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public string Currency { get; init; } = "CAD";

    public IReadOnlyList<ClientInvoiceStatementRow> Invoiced { get; init; } = Array.Empty<ClientInvoiceStatementRow>();
    public decimal InvoicedSubtotal { get; init; }
    public decimal InvoicedTax { get; init; }
    public decimal InvoicedTotal { get; init; }
    public IReadOnlyList<ClientInvoiceTaxLine> TaxByRate { get; init; } = Array.Empty<ClientInvoiceTaxLine>();

    public IReadOnlyList<ClientInvoiceStatementRow> Received { get; init; } = Array.Empty<ClientInvoiceStatementRow>();
    public decimal ReceivedTotal { get; init; }

    public decimal OwedAtEnd { get; init; }
    public int OwedAtEndCount { get; init; }

    public static (DateTime From, DateTime To) Month(int year, int month)
    {
        var from = new DateTime(year, month, 1);
        return (from, from.AddMonths(1).AddDays(-1));
    }

    public static (DateTime From, DateTime To) Quarter(int year, int quarter)
    {
        quarter = Math.Clamp(quarter, 1, 4);
        var from = new DateTime(year, (quarter - 1) * 3 + 1, 1);
        return (from, from.AddMonths(3).AddDays(-1));
    }

    public static ClientInvoiceStatement From(IEnumerable<ClientInvoice> invoices, DateTime from, DateTime to, string? agentTimeZone)
    {
        var start = from.Date;
        var end = to.Date;
        var money = invoices
            .Where(i => i.DocumentType == ClientInvoiceDocumentType.Invoice
                        && i.Status is ClientInvoiceStatus.Sent or ClientInvoiceStatus.Approved or ClientInvoiceStatus.Paid)
            .Select(i => new ClientInvoiceStatementRow
            {
                InvoiceId = i.Id,
                DocumentNumber = i.DocumentNumber,
                ClientName = $"{i.Client?.FirstName} {i.Client?.LastName}".Trim(),
                IssueDate = i.IssueDate.Date,
                DueDate = i.DueDate?.Date,
                Status = i.Status,
                Subtotal = i.SubTotal,
                TaxRegion = (i.TaxRegion ?? string.Empty).Trim(),
                TaxRate = i.TaxRate,
                Tax = i.TaxAmount,
                Total = i.Total,
                PaidOn = i.Status == ClientInvoiceStatus.Paid && i.PaidAt.HasValue ? AgentLocalTime.FromUtc(i.PaidAt.Value, agentTimeZone).Date : null,
                PaidMethod = i.PaidMethod
            })
            .ToList();

        var invoiced = money.Where(r => r.IssueDate >= start && r.IssueDate <= end)
            .OrderBy(r => r.IssueDate).ThenBy(r => r.DocumentNumber, StringComparer.Ordinal).ToList();
        var received = money.Where(r => r.PaidOn.HasValue && r.PaidOn.Value >= start && r.PaidOn.Value <= end)
            .OrderBy(r => r.PaidOn).ThenBy(r => r.DocumentNumber, StringComparer.Ordinal).ToList();
        var owed = money.Where(r => r.IssueDate <= end && (!r.PaidOn.HasValue || r.PaidOn.Value > end)).ToList();

        var taxByRate = invoiced
            .GroupBy(r => (Region: r.TaxRate == 0 ? string.Empty : r.TaxRegion, r.TaxRate))
            .Select(g => new ClientInvoiceTaxLine
            {
                Region = g.Key.Region,
                Rate = g.Key.TaxRate,
                Count = g.Count(),
                Subtotal = g.Sum(r => r.Subtotal),
                Tax = g.Sum(r => r.Tax),
                Total = g.Sum(r => r.Total)
            })
            .OrderByDescending(l => l.Rate).ThenBy(l => l.Region, StringComparer.Ordinal)
            .ToList();

        var currency = invoices
            .Where(i => i.DocumentType == ClientInvoiceDocumentType.Invoice)
            .GroupBy(i => string.IsNullOrWhiteSpace(i.Currency) ? "CAD" : i.Currency.Trim())
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? "CAD";

        return new ClientInvoiceStatement
        {
            Start = start,
            End = end,
            Currency = currency,
            Invoiced = invoiced,
            InvoicedSubtotal = invoiced.Sum(r => r.Subtotal),
            InvoicedTax = invoiced.Sum(r => r.Tax),
            InvoicedTotal = invoiced.Sum(r => r.Total),
            TaxByRate = taxByRate,
            Received = received,
            ReceivedTotal = received.Sum(r => r.Total),
            OwedAtEnd = owed.Sum(r => r.Total),
            OwedAtEndCount = owed.Count
        };
    }
}
