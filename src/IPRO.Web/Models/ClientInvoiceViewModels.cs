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
    // 566: where the discount is -- "none", "invoice" (one discount on the whole document; the two
    // values below) or "lines" (a discount on each item; the values on the lines).
    public string DiscountMode { get; set; } = "none";
    public InvoiceDiscountType DiscountType { get; set; } = InvoiceDiscountType.Percent;
    public decimal? DiscountValue { get; set; }   // empty when nothing was typed
    public List<ClientInvoiceLineItemInputModel> LineItems { get; set; } = new();
}

// 566: what the shared lines editor (_InvoiceLinesEditor) is handed by the invoice editor and by the
// recurring-schedule editor: the lines, the discount, and the word for the document being written.
public class InvoiceLinesEditorModel
{
    public List<ClientInvoiceLineItemInputModel> Lines { get; set; } = new();
    public string DiscountMode { get; set; } = "none";
    public InvoiceDiscountType DiscountType { get; set; } = InvoiceDiscountType.Percent;
    public decimal? DiscountValue { get; set; }   // empty when nothing was typed
    public string DocWord { get; set; } = "invoice";
}

public class ClientInvoiceLineItemInputModel
{
    public string? Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    // 563: the Tax tick on the line. The form always posts it (a ticked box, then a hidden "false"),
    // so the default speaks only for a line the form never showed: taxed, as every line was before.
    public bool IsTaxable { get; set; } = true;
    // 566: a discount on this item, read only when the document's discount is "lines".
    public InvoiceDiscountType DiscountType { get; set; } = InvoiceDiscountType.Percent;
    public decimal? DiscountValue { get; set; }   // empty when nothing was typed
}
