namespace IPRO.Entities;

public class ClientInvoiceLineItem
{
    public int Id { get; set; }
    public int ClientInvoiceId { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
    // 563 (2026-10-11): tax is charged on this line. A bakery sells plain bread (zero-rated) and
    // cakes (taxed) on one invoice, so the choice is the line's. Every line before this was taxed
    // whenever the invoice was, hence the default.
    public bool IsTaxable { get; set; } = true;
    public int SortOrder { get; set; }

    public ClientInvoice ClientInvoice { get; set; } = null!;
}
