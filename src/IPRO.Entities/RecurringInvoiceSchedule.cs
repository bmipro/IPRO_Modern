namespace IPRO.Entities;

public enum RecurringInvoiceFrequency { Monthly, Quarterly, Annually }

public class RecurringInvoiceSchedule
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public int ClientId { get; set; }
    public RecurringInvoiceFrequency Frequency { get; set; } = RecurringInvoiceFrequency.Monthly;
    public DateTime NextRunDate { get; set; } = DateTime.UtcNow.Date;
    public int DueInDays { get; set; } = 15;
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public AgentUser AgentUser { get; set; } = null!;
    public Client Client { get; set; } = null!;
    public ICollection<RecurringInvoiceLineItem> LineItems { get; set; } = new List<RecurringInvoiceLineItem>();
}
