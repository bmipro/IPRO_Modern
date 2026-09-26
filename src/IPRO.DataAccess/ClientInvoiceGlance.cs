using System;
using System.Collections.Generic;
using System.Linq;
using IPRO.Entities;

namespace IPRO.DataAccess;

// 523 (2026-09-26), slice 1: the adviser's money at a glance -- what clients still owe, what is
// overdue, what was paid this month against last month, and how long clients take to pay -- from
// the invoices already there. Pure: the caller hands over the agent's invoices and the agent's own
// "today" (INVARIANTS rule 10: whose day it is); nothing here reads a clock or a database, so a
// test pins every number.
//
// Only invoices count as money. Estimates are offers, drafts are not yet issued, and a void one
// never was. Paid dates are UTC instants and are read in the adviser's zone, so a payment at
// 11:30 p.m. on the 31st in Toronto belongs to that month, not the next. Issue dates are calendar
// dates the adviser chose and are used as they are.
public sealed class ClientInvoiceGlance
{
    public string Currency { get; init; } = "CAD";
    public decimal Outstanding { get; init; }
    public int OutstandingCount { get; init; }
    public decimal Overdue { get; init; }
    public int OverdueCount { get; init; }
    public decimal PaidThisMonth { get; init; }
    public int PaidThisMonthCount { get; init; }
    public decimal PaidLastMonth { get; init; }
    public int PaidLastMonthCount { get; init; }
    public int? AverageDaysToPay { get; init; }
    public int PaidInLastYearCount { get; init; }
    public int InvoiceCount { get; init; }

    public bool HasInvoices => InvoiceCount > 0;

    public static ClientInvoiceGlance From(IEnumerable<ClientInvoice> invoices, DateTime today, string? agentTimeZone)
    {
        var day = today.Date;
        var money = invoices
            .Where(i => i.DocumentType == ClientInvoiceDocumentType.Invoice
                        && i.Status != ClientInvoiceStatus.Void
                        && i.Status != ClientInvoiceStatus.Draft)
            .ToList();
        var unpaid = money.Where(i => i.Status is ClientInvoiceStatus.Sent or ClientInvoiceStatus.Approved).ToList();
        var overdue = unpaid.Where(i => i.DueDate.HasValue && i.DueDate.Value.Date < day).ToList();

        var paid = money
            .Where(i => i.Status == ClientInvoiceStatus.Paid && i.PaidAt.HasValue)
            .Select(i => (i.Total, IssuedOn: i.IssueDate.Date, PaidOn: AgentLocalTime.FromUtc(i.PaidAt!.Value, agentTimeZone).Date))
            .ToList();
        var monthStart = new DateTime(day.Year, day.Month, 1);
        var nextMonthStart = monthStart.AddMonths(1);
        var lastMonthStart = monthStart.AddMonths(-1);
        var yearAgo = day.AddYears(-1);
        var thisMonth = paid.Where(p => p.PaidOn >= monthStart && p.PaidOn < nextMonthStart).ToList();
        var lastMonth = paid.Where(p => p.PaidOn >= lastMonthStart && p.PaidOn < monthStart).ToList();
        var lastYear = paid.Where(p => p.PaidOn >= yearAgo).ToList();

        var currency = money
            .GroupBy(i => string.IsNullOrWhiteSpace(i.Currency) ? "CAD" : i.Currency.Trim())
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? "CAD";

        return new ClientInvoiceGlance
        {
            Currency = currency,
            Outstanding = unpaid.Sum(i => i.Total),
            OutstandingCount = unpaid.Count,
            Overdue = overdue.Sum(i => i.Total),
            OverdueCount = overdue.Count,
            PaidThisMonth = thisMonth.Sum(p => p.Total),
            PaidThisMonthCount = thisMonth.Count,
            PaidLastMonth = lastMonth.Sum(p => p.Total),
            PaidLastMonthCount = lastMonth.Count,
            AverageDaysToPay = lastYear.Count == 0
                ? null
                : (int)Math.Round(lastYear.Average(p => Math.Max(0, (p.PaidOn - p.IssuedOn).TotalDays)), MidpointRounding.AwayFromZero),
            PaidInLastYearCount = lastYear.Count,
            InvoiceCount = money.Count
        };
    }
}
