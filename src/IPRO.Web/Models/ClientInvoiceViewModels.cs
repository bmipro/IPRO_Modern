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
    // 563: the Tax tick on the line. The form always posts it (a ticked box, then a hidden "false"),
    // so the default speaks only for a line the form never showed: taxed, as every line was before.
    public bool IsTaxable { get; set; } = true;
}
