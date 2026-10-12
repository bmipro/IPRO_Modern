using System.Globalization;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// 568 (2026-10-11): the invoice editor remembers what a business sells. The bakery's owner, entering
// the same loaves on every invoice, asked whether he had to type each item again: "maybe the system
// intelligently remembers the items that had used for previous invoices". It does, from what is
// already stored -- the lines of the business's own invoices, estimates and recurring schedules --
// so there is nothing to set up and no list to keep.
//
// An item is a description (whatever its capitals or spacing), with the unit price and the Tax tick
// it had the LAST time it was used. The ones used most come first.
public sealed record RememberedInvoiceItem(string Description, decimal UnitPrice, bool IsTaxable, int Uses)
{
    // What the list shows beside the name: "$6.50".
    public string PriceText => (UnitPrice < 0 ? "-$" : "$") + Math.Abs(UnitPrice).ToString("N2", CultureInfo.InvariantCulture);
}

public static class RememberedInvoiceItems
{
    public const int Most = 300;          // offered in the editor
    public const int LinesRead = 4000;    // of the newest lines; a business's regular items are in them many times over

    public static async Task<List<RememberedInvoiceItem>> ForAgentAsync(IPRODbContext db, int agentId)
    {
        // A voided document is a mistake taken back; what was on it is not something the business sells.
        var invoiced = await db.ClientInvoiceLineItems.AsNoTracking()
            .Where(l => l.ClientInvoice.AgentUserId == agentId && l.ClientInvoice.Status != ClientInvoiceStatus.Void)
            .OrderByDescending(l => l.ClientInvoiceId).ThenBy(l => l.SortOrder)
            .Take(LinesRead)
            .Select(l => new Used(l.Description, l.UnitPrice, l.IsTaxable, l.ClientInvoice.CreatedAt))
            .ToListAsync();
        var scheduled = await db.RecurringInvoiceLineItems.AsNoTracking()
            .Where(l => l.RecurringInvoiceSchedule.AgentUserId == agentId)
            .OrderByDescending(l => l.RecurringInvoiceScheduleId).ThenBy(l => l.SortOrder)
            .Take(LinesRead)
            .Select(l => new Used(l.Description, l.UnitPrice, l.IsTaxable, l.RecurringInvoiceSchedule.UpdatedAt))
            .ToListAsync();
        return From(invoiced.Concat(scheduled));
    }

    public sealed record Used(string Description, decimal UnitPrice, bool IsTaxable, DateTime When);

    // Pure: the items among these uses, the most used first, then the most recent, then by name.
    public static List<RememberedInvoiceItem> From(IEnumerable<Used> uses) =>
        uses.Select(u => u with { Description = Clean(u.Description) })
            .Where(u => u.Description.Length > 0)
            .GroupBy(u => u.Description, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var latest = g.OrderByDescending(u => u.When).First();
                return (Item: new RememberedInvoiceItem(latest.Description, latest.UnitPrice, latest.IsTaxable, g.Count()), latest.When);
            })
            .OrderByDescending(x => x.Item.Uses).ThenByDescending(x => x.When).ThenBy(x => x.Item.Description, StringComparer.OrdinalIgnoreCase)
            .Take(Most)
            .Select(x => x.Item)
            .ToList();

    // One spelling for "Sourdough  loaf " and "sourdough loaf": trimmed, single spaces.
    public static string Clean(string? description) =>
        string.Join(' ', (description ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
