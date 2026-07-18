using IPRO.Entities;

namespace IPRO.Web.Models;

public class ClientInvoiceEditViewModel
{
    public int Id { get; set; }
    public ClientInvoiceDocumentType DocumentType { get; set; } = ClientInvoiceDocumentType.Invoice;
    public int ClientId { get; set; }
    public DateTime IssueDate { get; set; } = DateTime.UtcNow.Date;
    public DateTime? DueDate { get; set; } = DateTime.UtcNow.Date.AddDays(15);
    public string? Notes { get; set; } = string.Empty;
    public List<ClientInvoiceLineItemInputModel> LineItems { get; set; } = new();
}

public class ClientInvoiceLineItemInputModel
{
    public string? Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
}
