using IPRO.Business.Services;
using System.Globalization;
using IPRO.Entities;

namespace IPRO.Web.Infrastructure;

// Taxed (563): tax is charged on this line -- the document has tax and the line is ticked for it.
// Amount is the line at its full price (quantity x unit price). Discount (566) is what came off it:
// "-$8.00 (10%)" or "-$5.00"; empty when nothing did. So the Amount column adds up to the Subtotal
// and the Discount column to the Discounts line of the totals.
public sealed record ClientDocumentLineView(string Description, decimal Quantity, decimal UnitPrice, string TaxLabel, decimal Amount, bool Taxed = false, string Discount = "");

// 515 (2026-09-22): what an adviser's invoice or estimate to a client says, on the same design as
// the platform's own invoice (514) but branded for the ADVISER: their company name, address, phone
// and email, and their website logo when they have one. Everything shown is what the document
// already tracked: quantity and unit price, due date, paid on and how, the tax on the document.
public sealed class ClientDocumentPresentation
{
    public string DocumentLabel { get; init; } = string.Empty;
    public bool IsEstimate { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public string StatusClass { get; init; } = string.Empty;
    public string SupplierName { get; init; } = string.Empty;
    public IReadOnlyList<string> SupplierAddressLines { get; init; } = Array.Empty<string>();
    public string SupplierPhone { get; init; } = string.Empty;
    public string SupplierEmail { get; init; } = string.Empty;
    public string SupplierTaxNumber { get; init; } = string.Empty;   // 570: the business's GST/HST registration number; empty prints nothing
    public string LogoUrl { get; init; } = string.Empty;
    public string IssueDate { get; init; } = string.Empty;
    public string DueDate { get; init; } = string.Empty;
    public string PaidOn { get; init; } = string.Empty;
    public string PaidMethod { get; init; } = string.Empty;
    public string BillToName { get; init; } = string.Empty;
    public string BillToCompany { get; init; } = string.Empty;
    public string BillToEmail { get; init; } = string.Empty;
    public IReadOnlyList<string> BillToAddressLines { get; init; } = Array.Empty<string>();
    public string TaxLabel { get; init; } = string.Empty;
    public bool HasTax { get; init; }
    public decimal TaxableAmount { get; init; }   // 563: the ticked lines, which is what the tax is charged on
    // 566: either kind of discount reads the same way in the totals -- Subtotal (the lines at their
    // full prices, ItemsTotal), then the discount (DiscountTotal), then the tax. A discount on the
    // items also shows as a column beside each line.
    public bool HasLineDiscounts { get; init; }
    public decimal LineDiscountsTotal { get; init; }
    public bool HasInvoiceDiscount { get; init; }
    public bool HasDiscount => HasLineDiscounts || HasInvoiceDiscount;
    public decimal DiscountTotal { get; init; }
    public decimal ItemsTotal { get; init; }
    public string DiscountLabel { get; init; } = string.Empty;
    public IReadOnlyList<ClientDocumentLineView> Lines { get; init; } = Array.Empty<ClientDocumentLineView>();
    public bool ShowsBalance { get; init; }
    public decimal PaymentReceived { get; init; }
    public decimal BalanceDue { get; init; }

    public static ClientDocumentPresentation From(ClientInvoice document, AgentUser? agent, string? logoUrl)
    {
        var isEstimate = document.DocumentType == ClientInvoiceDocumentType.Estimate;
        var client = document.Client;
        var zone = agent?.TimeZone;
        string Day(DateTime utc) => AgentTimeZoneHelper.FromUtc(utc, zone).ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
        // 564 (2026-10-11): the issue date and the due date are calendar dates, typed in the editor's date
        // boxes and stored at midnight. Read as moments in UTC and moved to the adviser's zone, they showed
        // the day BEFORE to everyone west of Greenwich, while the email, the list and the statement said
        // the day itself. A payment is a moment, so the paid date keeps its conversion.
        string Date(DateTime date) => date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);

        var (statusText, statusClass) = document.Status switch
        {
            ClientInvoiceStatus.Paid => ("Paid", "paid"),
            ClientInvoiceStatus.Approved => ("Approved", "paid"),
            ClientInvoiceStatus.Sent => (isEstimate ? "Awaiting your reply" : "Awaiting payment", "open"),
            ClientInvoiceStatus.Draft => ("Draft", "open"),
            ClientInvoiceStatus.Declined => ("Declined", "failed"),
            ClientInvoiceStatus.Void => ("Void", "void"),
            _ => (document.Status.ToString(), "open")
        };

        var taxLabel = InvoiceText.TaxLabel(document.TaxRegion, document.TaxRate, document.TaxAmount);
        // 563: each line says whether it is taxed. A line left unticked reads "No tax"; on a document
        // with no tax at all, no line is taxed whatever its tick says.
        var hasTax = document.TaxAmount > 0;
        var items = (document.LineItems ?? new List<ClientInvoiceLineItem>()).OrderBy(i => i.SortOrder).ToList();
        var lines = items
            .Select(i => new ClientDocumentLineView(i.Description, i.Quantity, i.UnitPrice, hasTax && !i.IsTaxable ? "No tax" : taxLabel, i.Amount + i.DiscountAmount, hasTax && i.IsTaxable,
                i.DiscountAmount > 0 ? TakenOff(i) : string.Empty))
            .ToList();
        // 566: tax is charged on the ticked lines after any discount; a discount on the whole invoice
        // comes off them in proportion. What is stored is after the discounts (a line's Amount after
        // its own, SubTotal after the invoice's); the document shows the full prices and the discount.
        var lineDiscounts = items.Sum(i => i.DiscountAmount);
        var afterLineDiscounts = document.SubTotal + document.DiscountAmount;
        var itemsTotal = afterLineDiscounts + lineDiscounts;
        var taxableAmount = !hasTax ? 0m
            : items.Count == 0 ? document.SubTotal
            : ClientInvoiceMath.TaxableAfterDiscount(afterLineDiscounts, items.Where(i => i.IsTaxable).Sum(i => i.Amount), document.DiscountAmount);
        var discountWords = ClientInvoiceMath.Words(document.DiscountType, document.DiscountValue);

        var billToName = $"{client?.FirstName} {client?.LastName}".Trim();
        var cityLine = AddressText.CityLine(client?.City, client?.Province, client?.PostalCode);   // 516: one way everywhere
        var billToLines = InvoiceText.WithoutCountryOnly(
            new[] { client?.Address?.Trim(), cityLine, client?.Country?.Trim() }.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!).ToList(),
            client?.Country);

        var paid = document.Status == ClientInvoiceStatus.Paid;
        var owes = !isEstimate && document.Status is ClientInvoiceStatus.Sent or ClientInvoiceStatus.Draft;

        return new ClientDocumentPresentation
        {
            DocumentLabel = isEstimate ? "Estimate" : "Invoice",
            IsEstimate = isEstimate,
            StatusText = statusText,
            StatusClass = statusClass,
            SupplierName = string.IsNullOrWhiteSpace(agent?.CompanyName) ? "Your Business" : agent!.CompanyName.Trim(),
            SupplierAddressLines = InvoiceText.WithoutCountryOnly(InvoiceText.AgentAddressLines(agent), agent?.Country),
            SupplierPhone = agent?.Phone?.Trim() ?? string.Empty,
            SupplierEmail = agent?.Email?.Trim() ?? string.Empty,
            SupplierTaxNumber = agent?.TaxRegistrationNumber?.Trim() ?? string.Empty,
            LogoUrl = logoUrl?.Trim() ?? string.Empty,
            IssueDate = Date(document.IssueDate),
            DueDate = document.DueDate.HasValue ? Date(document.DueDate.Value) : string.Empty,
            PaidOn = paid && document.PaidAt.HasValue ? Day(document.PaidAt.Value) : string.Empty,
            PaidMethod = paid ? MethodWords(document.PaidMethod) : string.Empty,
            BillToName = billToName.Length == 0 ? (client?.CompanyName ?? string.Empty) : billToName,
            BillToCompany = billToName.Length == 0 ? string.Empty : (client?.CompanyName?.Trim() ?? string.Empty),
            BillToEmail = client?.Email?.Trim() ?? string.Empty,
            BillToAddressLines = billToLines,
            TaxLabel = taxLabel,
            HasTax = hasTax,
            TaxableAmount = taxableAmount,
            HasLineDiscounts = lineDiscounts > 0,
            LineDiscountsTotal = lineDiscounts,
            HasInvoiceDiscount = document.DiscountAmount > 0,
            DiscountTotal = document.DiscountAmount + lineDiscounts,
            ItemsTotal = itemsTotal,
            DiscountLabel = lineDiscounts > 0 ? "Discounts"
                : document.DiscountType == InvoiceDiscountType.Percent && discountWords.Length > 0 ? $"Discount ({discountWords})" : "Discount",
            Lines = lines,
            ShowsBalance = !isEstimate && (paid || owes),
            PaymentReceived = paid ? document.Total : 0m,
            BalanceDue = owes ? document.Total : 0m
        };
    }

    // What came off a line, in dollars, with the percent beside it when that is how it was given.
    private static string TakenOff(ClientInvoiceLineItem line)
    {
        var dollars = "-$" + line.DiscountAmount.ToString("N2", CultureInfo.InvariantCulture);
        return line.DiscountType == InvoiceDiscountType.Percent ? $"{dollars} ({ClientInvoiceMath.Words(line.DiscountType, line.DiscountValue)})" : dollars;
    }

    private static string MethodWords(ClientInvoicePaymentMethod? method) => method switch
    {
        ClientInvoicePaymentMethod.Online => "Online",
        ClientInvoicePaymentMethod.Cheque => "Cheque",
        ClientInvoicePaymentMethod.Cash => "Cash",
        ClientInvoicePaymentMethod.EFT => "EFT",
        ClientInvoicePaymentMethod.Other => "Other",
        _ => string.Empty
    };
}
