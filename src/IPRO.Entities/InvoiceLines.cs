using System;
using System.Collections.Generic;
using System.Linq;

namespace IPRO.Entities;

// 516 (2026-09-22): the tax line CreateInvoiceAsync stores beside the charges ("{region} tax
// ({rate:P3})", the invoice's tax amount) is shown once -- in the tax summary and the totals --
// never as a charge. The owner, on the first real invoice of the 514 design: "remove the
// description for HST (nothing there)".
public static class InvoiceLines
{
    public static bool IsTaxLine(string? description, decimal amount, decimal taxAmount)
    {
        var text = (description ?? string.Empty).Trim();
        return text.EndsWith("%)", StringComparison.Ordinal)
               && text.Contains(" tax (", StringComparison.OrdinalIgnoreCase)
               && Math.Abs(amount - taxAmount) < 0.005m;
    }

    public static IEnumerable<InvoiceLineItem> Charges(IEnumerable<InvoiceLineItem> lines, decimal taxAmount) =>
        lines.Where(line => !IsTaxLine(line.Description, line.Amount, taxAmount));
}
