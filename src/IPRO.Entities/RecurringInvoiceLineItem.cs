namespace IPRO.Entities;

public class RecurringInvoiceLineItem
{
    public int Id { get; set; }
    public int RecurringInvoiceScheduleId { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public bool IsTaxable { get; set; } = true;   // 563: carried onto each invoice the schedule makes
    public InvoiceDiscountType DiscountType { get; set; }   // 566: as on an invoice line; worked out on each invoice made
    public decimal DiscountValue { get; set; }
    public int SortOrder { get; set; }

    public RecurringInvoiceSchedule RecurringInvoiceSchedule { get; set; } = null!;
}
