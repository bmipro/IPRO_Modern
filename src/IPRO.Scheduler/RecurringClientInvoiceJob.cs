using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

public class RecurringClientInvoiceJob
{
    private readonly IPRODbContext _db;
    private readonly IClientInvoiceService _invoiceService;
    private readonly ILogger<RecurringClientInvoiceJob> _logger;

    public RecurringClientInvoiceJob(IPRODbContext db, IClientInvoiceService invoiceService, ILogger<RecurringClientInvoiceJob> logger)
    {
        _db = db;
        _invoiceService = invoiceService;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        var dueSchedules = await _db.RecurringInvoiceSchedules
            .Include(s => s.Client)
            .Include(s => s.LineItems)
            .Where(s => s.IsActive && s.NextRunDate <= DateTime.UtcNow)
            .Take(100)
            .ToListAsync();

        foreach (var schedule in dueSchedules)
        {
            try
            {
                var lineItems = schedule.LineItems.OrderBy(l => l.SortOrder).Select(l => new ClientInvoiceLineItem
                {
                    Description = l.Description,
                    Quantity = l.Quantity,
                    UnitPrice = l.UnitPrice,
                    Amount = Math.Round(l.Quantity * l.UnitPrice, 2, MidpointRounding.AwayFromZero),
                    SortOrder = l.SortOrder
                }).ToList();

                var subTotal = lineItems.Sum(l => l.Amount);
                var tax = await _invoiceService.CalculateTaxAsync(schedule.Client, subTotal);

                var invoice = new ClientInvoice
                {
                    AgentUserId = schedule.AgentUserId,
                    ClientId = schedule.ClientId,
                    DocumentType = ClientInvoiceDocumentType.Invoice,
                    Status = ClientInvoiceStatus.Draft,
                    IssueDate = DateTime.UtcNow.Date,
                    DueDate = DateTime.UtcNow.Date.AddDays(schedule.DueInDays),
                    Notes = schedule.Notes,
                    ViewToken = Guid.NewGuid().ToString("N"),
                    DocumentNumber = await _invoiceService.GenerateDocumentNumberAsync(schedule.AgentUserId, ClientInvoiceDocumentType.Invoice),
                    SubTotal = subTotal,
                    TaxRegion = tax.Region,
                    TaxRate = tax.Rate,
                    TaxAmount = tax.Amount,
                    Total = subTotal + tax.Amount,
                    LineItems = lineItems
                };

                _db.ClientInvoices.Add(invoice);

                schedule.NextRunDate = schedule.Frequency switch
                {
                    RecurringInvoiceFrequency.Quarterly => schedule.NextRunDate.AddMonths(3),
                    RecurringInvoiceFrequency.Annually => schedule.NextRunDate.AddYears(1),
                    _ => schedule.NextRunDate.AddMonths(1)
                };
                schedule.UpdatedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Recurring invoice schedule {ScheduleId} failed to generate an invoice", schedule.Id);
            }
        }

        await _db.SaveChangesAsync();
    }
}
