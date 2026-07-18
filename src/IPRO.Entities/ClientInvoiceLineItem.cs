namespace IPRO.Entities;

public class ClientInvoiceLineItem
{
    public int Id { get; set; }
    public int ClientInvoiceId { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public int SortOrder { get; set; }

    public ClientInvoice ClientInvoice { get; set; } = null!;
}
