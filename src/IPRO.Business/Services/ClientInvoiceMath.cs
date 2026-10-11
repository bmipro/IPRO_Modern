using System.Globalization;
using IPRO.Entities;

namespace IPRO.Business.Services;

// 566 (2026-10-11): the arithmetic of an invoice to a client, in one place -- the editor (which shows
// it while the invoice is written, in the browser, by the same steps), the save, a duplicate and the
// recurring job.
//
// The owner's first customer outside the advice business, a bakery, wanted one discount on the whole
// invoice; the owner wanted "both ... apply discount to whole or per item" for other businesses. So:
//   - a discount on an ITEM comes off that line; the line's amount is what is left;
//   - a discount on the WHOLE invoice comes off the sum of the lines, and is shared between the taxed
//     and the untaxed lines in proportion, because tax is charged on the price after a discount.
// Every figure is rounded to the cent, halves away from zero, as every invoice in the product is.
public sealed record ClientInvoiceTotals(decimal ItemsTotal, decimal InvoiceDiscount, decimal SubTotal, decimal TaxableAmount);

public static class ClientInvoiceMath
{
    public const string ModeNone = "none";
    public const string ModeInvoice = "invoice";
    public const string ModeLines = "lines";

    public static string CleanMode(string? mode)
    {
        var wanted = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return wanted is ModeInvoice or ModeLines ? wanted : ModeNone;
    }

    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    // What a line comes to before any discount. A quantity of nothing is one, as the editor has always read it.
    public static decimal LineGross(decimal quantity, decimal unitPrice) => Round((quantity <= 0 ? 1 : quantity) * unitPrice);

    // The dollars a discount takes off an amount: never more than the amount, never from an amount
    // that is nothing or a credit, and a percent is never more than a hundred.
    public static decimal Discount(decimal amount, InvoiceDiscountType type, decimal value)
    {
        if (amount <= 0 || value <= 0) return 0m;
        return type switch
        {
            InvoiceDiscountType.Percent => Round(amount * Math.Min(value, 100m) / 100m),
            InvoiceDiscountType.Amount => Math.Min(Round(value), amount),
            _ => 0m
        };
    }

    // lines: each line's amount after its own discount, and whether it is taxed.
    public static ClientInvoiceTotals Totals(IEnumerable<(decimal Amount, bool IsTaxable)> lines, InvoiceDiscountType discountType, decimal discountValue)
    {
        var all = lines.ToList();
        var itemsTotal = all.Sum(l => l.Amount);
        var invoiceDiscount = Discount(itemsTotal, discountType, discountValue);
        var taxable = TaxableAfterDiscount(itemsTotal, all.Where(l => l.IsTaxable).Sum(l => l.Amount), invoiceDiscount);
        return new ClientInvoiceTotals(itemsTotal, invoiceDiscount, itemsTotal - invoiceDiscount, taxable);
    }

    // The taxed lines' share of a discount on the whole invoice comes off what tax is charged on.
    public static decimal TaxableAfterDiscount(decimal itemsTotal, decimal taxableItems, decimal invoiceDiscount) =>
        invoiceDiscount <= 0 || itemsTotal <= 0 ? taxableItems : taxableItems - Round(invoiceDiscount * taxableItems / itemsTotal);

    // "10%" or "$10.00": how a discount reads on the invoice. Nothing when there is none.
    public static string Words(InvoiceDiscountType type, decimal value) => type switch
    {
        InvoiceDiscountType.Percent when value > 0 => Math.Min(value, 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%",
        InvoiceDiscountType.Amount when value > 0 => "$" + Round(value).ToString("N2", CultureInfo.InvariantCulture),
        _ => string.Empty
    };
}
