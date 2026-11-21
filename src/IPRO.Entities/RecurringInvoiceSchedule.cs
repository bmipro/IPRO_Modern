namespace IPRO.Entities;

public enum RecurringInvoiceFrequency { Monthly, Quarterly, Annually }

public class RecurringInvoiceSchedule
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public int ClientId { get; set; }
    public RecurringInvoiceFrequency Frequency { get; set; } = RecurringInvoiceFrequency.Monthly;
    public DateTime NextRunDate { get; set; } = DateTime.UtcNow.Date;
    // 670 (2026-11-21): the day of the month the adviser chose (1-31). Each next run is counted from it, so a
    // schedule on the 31st runs on Feb 28 and then on Mar 31 again. Null on a schedule saved before 670 until
    // its next run gives it one.
    public int? AnchorDay { get; set; }
    public int DueInDays { get; set; } = 15;
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; } = string.Empty;
    // 566: one discount on the whole of each invoice the schedule makes.
    public InvoiceDiscountType DiscountType { get; set; }
    public decimal DiscountValue { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public AgentUser AgentUser { get; set; } = null!;
    public Client Client { get; set; } = null!;
    public ICollection<RecurringInvoiceLineItem> LineItems { get; set; } = new List<RecurringInvoiceLineItem>();
}
