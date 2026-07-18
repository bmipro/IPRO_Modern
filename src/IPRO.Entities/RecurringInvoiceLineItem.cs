namespace IPRO.Entities;

public class RecurringInvoiceLineItem
{
    public int Id { get; set; }
    public int RecurringInvoiceScheduleId { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public int SortOrder { get; set; }

    public RecurringInvoiceSchedule RecurringInvoiceSchedule { get; set; } = null!;
}
