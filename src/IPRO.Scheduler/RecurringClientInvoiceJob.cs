using IPRO.Business.Services;
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
        // 641 (the fresh sweep, 2026-11-20): NextRunDate is the adviser's own calendar date (a date box), but it was
        // compared with the UTC clock, and the job ran at 00:00 UTC: a Toronto adviser's December 1 invoice was made
        // on the evening of November 30, dated November 30. A schedule is due when the adviser's OWN today has
        // reached its date; the job runs hourly (Program.cs), so an invoice appears soon after the adviser's
        // midnight. Candidates are read a day ahead and judged per adviser's time zone.
        var now = DateTime.UtcNow;
        // 678 (audit 2026-11-24 C-3): every due schedule, a page at a time in Id order, and at most
        // RecurringScheduleRules.PerAdviserPerRun of one adviser's in a run. It read the 200 earliest dates and made
        // the first 100, so one adviser's schedules dated long ago could hold every other adviser's invoices back.
        var perAdviser = new Dictionary<int, int>();
        var lastId = 0;
        while (true)
        {
        var candidates = await _db.RecurringInvoiceSchedules
            .Include(s => s.Client)
            .Include(s => s.LineItems)
            .Where(s => s.IsActive && s.NextRunDate <= now.AddDays(1) && s.Id > lastId)
            .OrderBy(s => s.Id)
            .Take(RecurringScheduleRules.PageSize)
            .ToListAsync();
        if (candidates.Count == 0) break;
        lastId = candidates[^1].Id;
        var advisers = candidates.Select(s => s.AgentUserId).Distinct().ToList();
        var zones = await _db.AgentUsers.AsNoTracking()
            .Where(a => advisers.Contains(a.Id))
            .Select(a => new { a.Id, a.TimeZone })
            .ToDictionaryAsync(a => a.Id, a => a.TimeZone);
        var dueSchedules = new List<RecurringInvoiceSchedule>();
        foreach (var candidate in candidates)
        {
            if (AgentLocalTime.FromUtc(now, zones.GetValueOrDefault(candidate.AgentUserId)).Date < candidate.NextRunDate.Date) continue;
            // A schedule outside the bounds is paused, not retried at the head of every run.
            if (RecurringScheduleRules.ParkReason(candidate.NextRunDate, candidate.DueInDays, now) is { } reason)
            {
                var id = candidate.Id;
                await _db.RecurringInvoiceSchedules.Where(x => x.Id == id)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false).SetProperty(x => x.UpdatedAt, DateTime.UtcNow));
                _logger.LogWarning("Recurring invoice schedule {ScheduleId} (agent {AgentId}) was paused: {Reason}", id, candidate.AgentUserId, reason);
                continue;
            }
            var taken = perAdviser.GetValueOrDefault(candidate.AgentUserId);
            if (taken >= RecurringScheduleRules.PerAdviserPerRun) continue;
            perAdviser[candidate.AgentUserId] = taken + 1;
            dueSchedules.Add(candidate);
        }

            foreach (var schedule in dueSchedules)
            {
                try
                {
                    // 566: a line's amount is quantity x unit price less its own discount; a discount on the
                    // whole invoice comes off the sum, and tax is charged on what is left (ClientInvoiceMath).
                    var lineItems = schedule.LineItems.OrderBy(l => l.SortOrder).Select(l =>
                    {
                        var gross = Math.Round(l.Quantity * l.UnitPrice, 2, MidpointRounding.AwayFromZero);
                        var discount = ClientInvoiceMath.Discount(gross, l.DiscountType, l.DiscountValue);
                        return new ClientInvoiceLineItem
                        {
                            Description = l.Description,
                            Quantity = l.Quantity,
                            UnitPrice = l.UnitPrice,
                            DiscountType = discount > 0 ? l.DiscountType : InvoiceDiscountType.None,
                            DiscountValue = discount > 0 ? l.DiscountValue : 0m,
                            DiscountAmount = discount,
                            Amount = gross - discount,
                            IsTaxable = l.IsTaxable,
                            SortOrder = l.SortOrder
                        };
                    }).ToList();

                    var totals = ClientInvoiceMath.Totals(lineItems.Select(l => (l.Amount, l.IsTaxable)), schedule.DiscountType, schedule.DiscountValue);
                    var subTotal = totals.SubTotal;
                    // 563: tax on the ticked lines, at the client's province's rate or else the business's own.
                    var business = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == schedule.AgentUserId);
                    var tax = await _invoiceService.CalculateTaxAsync(schedule.Client, business, totals.TaxableAmount);
                    // 564: dated the adviser's own today, not the UTC date.
                    var today = AgentLocalTime.FromUtc(DateTime.UtcNow, business?.TimeZone).Date;

                    var invoice = new ClientInvoice
                    {
                        AgentUserId = schedule.AgentUserId,
                        ClientId = schedule.ClientId,
                        DocumentType = ClientInvoiceDocumentType.Invoice,
                        Status = ClientInvoiceStatus.Draft,
                        IssueDate = today,
                        DueDate = today.AddDays(schedule.DueInDays),
                        Notes = schedule.Notes,
                        ViewToken = Guid.NewGuid().ToString("N"),
                        DocumentNumber = await _invoiceService.GenerateDocumentNumberAsync(schedule.AgentUserId, ClientInvoiceDocumentType.Invoice),
                        DiscountType = totals.InvoiceDiscount > 0 ? schedule.DiscountType : InvoiceDiscountType.None,
                        DiscountValue = totals.InvoiceDiscount > 0 ? schedule.DiscountValue : 0m,
                        DiscountAmount = totals.InvoiceDiscount,
                        SubTotal = subTotal,
                        TaxRegion = tax.Region,
                        TaxRate = tax.Rate,
                        TaxAmount = tax.Amount,
                        Total = subTotal + tax.Amount,
                        LineItems = lineItems
                    };

                    _db.ClientInvoices.Add(invoice);

                    // 670 (2026-11-21): a schedule saved before 670 has no day of its own yet; it takes the day it is
                    // on now and keeps it from here. One the old arithmetic already moved to the 28th stays on the
                    // 28th: the day it started on was never recorded.
                    var anchorDay = schedule.AnchorDay ??= schedule.NextRunDate.Day;
                    schedule.NextRunDate = NextRunAfter(schedule.NextRunDate, schedule.Frequency, anchorDay);
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
            // The page's rows are done with; the next page starts with nothing tracked.
            _db.ChangeTracker.Clear();
        }
    }

    // 670 (2026-11-21): the next run is the period's months on from this run's month, on the schedule's own day,
    // or on that month's last day when the month is shorter. It was AddMonths from the last run, and .NET clamps
    // to the month's end (Jan 31 -> Feb 28), so the shortened day stuck: Mar 28, Apr 28... for good, and the same
    // for the 29th and 30th. Clamped within the one month only: Jan 31 -> Feb 28 -> Mar 31 -> Apr 30.
    internal static DateTime NextRunAfter(DateTime runDate, RecurringInvoiceFrequency frequency, int anchorDay)
    {
        var months = frequency switch
        {
            RecurringInvoiceFrequency.Quarterly => 3,
            RecurringInvoiceFrequency.Annually => 12,
            _ => 1
        };
        var month = new DateTime(runDate.Year, runDate.Month, 1, 0, 0, 0, runDate.Kind).AddMonths(months);
        var day = Math.Clamp(anchorDay, 1, DateTime.DaysInMonth(month.Year, month.Month));
        return month.AddDays(day - 1).Add(runDate.TimeOfDay);
    }

    // 683 (audit 2026-11-26, W04-03): the schedule's first date on or after `today`, on its own rhythm. Used when
    // a paused schedule is resumed: its date had stayed where it was when paused, so the job found it due on every
    // hourly run and made one invoice per missed period, all dated today (paused in May, resumed in November:
    // seven invoices in seven hours), or parked it again without a word once the date was over a year old.
    public static DateTime NextRunOnOrAfter(DateTime nextRunDate, RecurringInvoiceFrequency frequency, int anchorDay, DateTime today)
    {
        var next = nextRunDate;
        for (var guard = 0; next.Date < today.Date && guard < 5000; guard++) next = NextRunAfter(next, frequency, anchorDay);
        return next;
    }
}
