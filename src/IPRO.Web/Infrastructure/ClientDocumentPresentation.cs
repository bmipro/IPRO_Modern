using System.Globalization;
using IPRO.Entities;

namespace IPRO.Web.Infrastructure;

// Taxed (563): tax is charged on this line -- the document has tax and the line is ticked for it.
public sealed record ClientDocumentLineView(string Description, decimal Quantity, decimal UnitPrice, string TaxLabel, decimal Amount, bool Taxed = false);

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
            .Select(i => new ClientDocumentLineView(i.Description, i.Quantity, i.UnitPrice, hasTax && !i.IsTaxable ? "No tax" : taxLabel, i.Amount, hasTax && i.IsTaxable))
            .ToList();
        var taxableAmount = !hasTax ? 0m : items.Count == 0 ? document.SubTotal : items.Where(i => i.IsTaxable).Sum(i => i.Amount);

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
            Lines = lines,
            ShowsBalance = !isEstimate && (paid || owes),
            PaymentReceived = paid ? document.Total : 0m,
            BalanceDue = owes ? document.Total : 0m
        };
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
