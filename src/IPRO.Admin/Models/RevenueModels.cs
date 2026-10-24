namespace IPRO.Admin.Models;

// 600 (2026-10-24): one line of SuperAdmin -> Revenue's ledger and of its CSV -- an invoice, or a credit note (a refund
// marked Refunded, or a Refer a Friend reward paid) with its amounts NEGATIVE, so every total, month, region and
// spreadsheet sum nets what was credited against what was invoiced. The owner: "matters for tax filing".
// BillToName is as stored (an invoice's snapshot may be blank; the screen falls back to the account, the CSV does not).
public sealed record RevenueLedgerRow(
    bool IsCreditNote, string Number, DateTime IssuedAt, int AgentUserId, string BillToName, string BillToCompany, string BillToEmail,
    string Package, decimal SubTotal, string TaxRegion, decimal TaxRate, decimal Tax, decimal Total, string Currency, bool Paid,
    string PayPalReference, string CreditsInvoice, string ViewUrl, string AdviserTimeZone, string CreditKind = "")
{
    public string Type => IsCreditNote ? "Credit note" : "Invoice";
}

// A month of the chart and its table: paid invoices, credit notes (negative) and what is left.
public sealed record RevenueMonth(string Label, decimal Invoiced, decimal CreditNotes, decimal Net);

// Tax by region, the remittance view: the taxable amount and the tax on paid invoices, less what credit notes gave back.
public sealed record RevenueTaxRegion(string Region, decimal Taxable, decimal Collected, decimal Credited, decimal NetTax);
