using IPRO.Entities;

namespace IPRO.Web.Models;

public class RecurringInvoiceEditViewModel
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public RecurringInvoiceFrequency Frequency { get; set; } = RecurringInvoiceFrequency.Monthly;
    public DateTime NextRunDate { get; set; } = DateTime.UtcNow.Date.AddMonths(1);
    public int DueInDays { get; set; } = 15;
    public string? Notes { get; set; } = string.Empty;
    public List<ClientInvoiceLineItemInputModel> LineItems { get; set; } = new();
}
