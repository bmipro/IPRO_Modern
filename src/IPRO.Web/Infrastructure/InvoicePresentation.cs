using System.Globalization;
using IPRO.Entities;

namespace IPRO.Web.Infrastructure;

public sealed record InvoiceLineView(string Description, string TaxLabel, decimal Amount);

// 514 (2026-09-22): what the customer invoice page says, worked out once here so the page only
// binds. Built from the SAME data the old page showed -- nothing the system does not track is
// invented: no quantity or rate (every line is one item), no service dates, no "paid on" (no such
// date is stored). The bill-to is the snapshot frozen on the invoice at issue time (it outlives the
// agent); the live agent is the fallback for invoices older than the snapshot.
public sealed class InvoicePresentation
{
    public string StatusText { get; init; } = string.Empty;
    public string StatusClass { get; init; } = string.Empty;
    public string Eyebrow { get; init; } = string.Empty;
    public string InvoiceDate { get; init; } = string.Empty;
    public string BillToName { get; init; } = string.Empty;
    public string BillToCompany { get; init; } = string.Empty;
    public string BillToEmail { get; init; } = string.Empty;
    public IReadOnlyList<string> BillToAddressLines { get; init; } = Array.Empty<string>();
    public string PackageName { get; init; } = string.Empty;
    public string BillingCycle { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public string SubscriptionId { get; init; } = string.Empty;
    public string TransactionId { get; init; } = string.Empty;
    public string TaxLabel { get; init; } = string.Empty;
    public bool HasTax { get; init; }
    public IReadOnlyList<InvoiceLineView> Lines { get; init; } = Array.Empty<InvoiceLineView>();
    public decimal PaymentReceived { get; init; }
    public decimal BalanceDue { get; init; }

    public static InvoicePresentation From(Invoice invoice, AgentUser? agent, BillingRule? package)
    {
        // 516: the stored reference is the subscription id, the settling sale id(s) and any failed-attempt
        // markers, comma-joined; the page shows the subscription and the transaction as two rows.
        var reference = PayPalReference.Split(invoice.PayPalTransactionId);
        var failed = !invoice.IsPaid && reference.FailedIds.Count > 0;
        var settled = invoice.IsPaid && !failed;
        var subscription = settled ? reference.SubscriptionId : string.Empty;
        var transaction = settled ? string.Join(", ", reference.TransactionIds) : string.Empty;
        var taxLabel = InvoiceText.TaxLabel(invoice.TaxRegion, invoice.TaxRate, invoice.TaxAmount);

        // 516: the stored tax line is not a charge; it is shown once, in the tax summary and the totals.
        var charges = InvoiceLines.Charges(invoice.LineItems ?? new List<InvoiceLineItem>(), invoice.TaxAmount).OrderBy(i => i.SortOrder).ToList();
        var lines = charges.Count > 0
            ? charges.Select(i => new InvoiceLineView(i.Description, taxLabel, i.Amount)).ToList()
            : new List<InvoiceLineView> { new("IPRO billing charge", taxLabel, invoice.SubTotal) };

        var billToName = FirstNonBlank(invoice.BillToName, agent == null ? null : $"{agent.FirstName} {agent.LastName}".Trim(), agent?.UserName, "IPRO Agent");
        // 516: the snapshot is the record; a city line frozen as "City" / "Province PostalCode" is joined on display.
        var addressLines = !string.IsNullOrWhiteSpace(invoice.BillToAddress)
            ? AddressText.JoinCityAndProvinceLines(invoice.BillToAddress.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList())
            : InvoiceText.WithoutCountryOnly(InvoiceText.AgentAddressLines(agent), agent?.Country);

        var period = invoice.Billing?.Period;
        var cycle = period switch
        {
            BillingPeriod.Annually => "Annual",
            BillingPeriod.Quarterly => "Quarterly",
            BillingPeriod.Monthly => "Monthly",
            _ => string.Empty
        };

        return new InvoicePresentation
        {
            StatusText = invoice.IsPaid ? "Paid" : failed ? "Payment failed" : "Unpaid",
            StatusClass = invoice.IsPaid ? "paid" : failed ? "failed" : "open",
            Eyebrow = cycle.Length == 0 ? "Subscription" : cycle + " subscription",
            InvoiceDate = AgentTimeZoneHelper.FromUtc(invoice.IssuedAt, agent?.TimeZone).ToString("MMMM d, yyyy", CultureInfo.InvariantCulture),
            BillToName = billToName,
            BillToCompany = FirstNonBlank(invoice.BillToCompany, agent?.CompanyName, string.Empty),
            BillToEmail = FirstNonBlank(invoice.BillToEmail, agent?.Email, string.Empty),
            BillToAddressLines = addressLines,
            PackageName = string.IsNullOrWhiteSpace(package?.PackageName) ? "IPRO package" : package!.PackageName,
            BillingCycle = cycle,
            Method = invoice.Total <= 0 && transaction.Length == 0 ? "No charge" : "PayPal",
            SubscriptionId = subscription,
            TransactionId = transaction,
            TaxLabel = taxLabel,
            HasTax = invoice.TaxAmount > 0,
            Lines = lines,
            PaymentReceived = invoice.IsPaid ? invoice.Total : 0m,
            BalanceDue = invoice.IsPaid ? 0m : invoice.Total
        };
    }

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;
}

public static class InvoiceText
{
    // An address that is only a country is no address (AgentUser and Client default Country to "Canada"):
    // an adviser or client with nothing else filled in gets no address block, not a lone "Canada".
    // The agent's address as invoices and estimates print it: street, "City, Province PostalCode", country.
    public static IReadOnlyList<string> AgentAddressLines(AgentUser? agent) =>
        agent == null
            ? Array.Empty<string>()
            : new[] { agent.CompanyAddress, AddressText.CityLine(agent.City, agent.Province, agent.PostalCode), agent.Country }
                .Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();

    public static IReadOnlyList<string> WithoutCountryOnly(IReadOnlyList<string> lines, string? country) =>
        lines.Count == 1 && string.Equals(lines[0].Trim(), (country ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
            ? Array.Empty<string>()
            : lines;

    // "ON HST 13%": the region as the invoice carries it plus the rate, unless the region already
    // names the rate (older invoices carry "ON 13% HST"). "No tax" when nothing was charged.
    public static string TaxLabel(string? region, decimal rate, decimal taxAmount)
    {
        var name = (region ?? string.Empty).Trim();
        if (taxAmount <= 0 && rate <= 0) return "No tax";
        if (name.Length == 0) return "Tax";
        if (name.Contains('%')) return name;
        var pct = (rate * 100m).ToString("0.###", CultureInfo.InvariantCulture);
        return $"{name} {pct}%";
    }
}
