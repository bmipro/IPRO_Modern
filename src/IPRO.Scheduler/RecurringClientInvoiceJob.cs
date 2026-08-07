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
            .OrderBy(s => s.NextRunDate)
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

                // Save per schedule, not once at the end.
                //
                // GenerateDocumentNumberAsync derives the next number from COMMITTED rows. With a
                // single terminal save, every schedule for the same agent in one run computed its
                // number against the same unchanged data and got the SAME number -- and since L-12
                // added a unique index on (AgentUserId, DocumentNumber), the duplicate now fails the
                // whole SaveChanges. One agent with two schedules would stop recurring invoicing for
                // every agent, permanently, because the next run hits the same collision.
                //
                // Committing here means the next iteration's lookup sees this invoice and moves past
                // it. It also stops one bad schedule discarding the invoices already generated for
                // everyone else in the batch.
                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Recurring invoice schedule {ScheduleId} failed to generate an invoice", schedule.Id);

                // Detach whatever this iteration added. A failed SaveChanges leaves the entity
                // tracked in Added state, so the NEXT schedule's save would retry it and fail the
                // same way -- turning one bad row into a failure for every schedule after it.
                foreach (var entry in _db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
                {
                    entry.State = EntityState.Detached;
                }

                // The schedule itself is tracked as Modified with NextRunDate already advanced.
                // Left attached, the NEXT schedule's successful save would commit that advance --
                // an occurrence silently skipped forever with no invoice behind it (independent
                // review H-3). Detaching discards the advance so this schedule is retried whole,
                // invoice and all, on the next run.
                _db.Entry(schedule).State = EntityState.Detached;
            }
        }
    }
}
