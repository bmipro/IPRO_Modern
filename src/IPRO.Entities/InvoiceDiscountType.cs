namespace IPRO.Entities;

// 566 (2026-10-11): a discount on an invoice to a client -- on the whole invoice, or on each item.
// The value is what the adviser typed: 10 for "10 %" or 10 for "$10". Stored as its number, so None
// must stay 0: every row written before this has no discount.
public enum InvoiceDiscountType
{
    None = 0,
    Percent = 1,
    Amount = 2
}
