using IPRO.Business.Services;
using System.Security.Claims;
using System.Text;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class ClientInvoicesController : Controller
{
    private const int PageSize = 20;

    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IClientInvoiceService _invoiceService;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public ClientInvoicesController(IPRODbContext db, IPackageEntitlementService entitlements, IClientInvoiceService invoiceService, IEmailService email, IConfiguration configuration)
    {
        _db = db;
        _entitlements = entitlements;
        _invoiceService = invoiceService;
        _email = email;
        _configuration = configuration;
    }

    private string BuildPublicDocumentUrl(string token) => $"{PortalUrlHelper.GetAgentPortalBaseUrl(_configuration)}/invoice/{token}";

    // 523: the adviser's own zone and day (INVARIANTS rule 10), for the glance, the aging page and
    // the "overdue" filter alike.
    private async Task<(string? Zone, DateTime Today)> AgentDayAsync()
    {
        var zone = await AgentTimeZoneHelper.ResolveForAgentAsync(_db, AgentId);
        return (zone, AgentTimeZoneHelper.FromUtc(DateTime.UtcNow, zone).Date);
    }

    // 523 (slice 2): who owes what and for how long -- the unpaid invoices by client, bucketed by
    // days past due in the adviser's day.
    public async Task<IActionResult> Aging()
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var (zone, today) = await AgentDayAsync();
        var unpaid = await _db.ClientInvoices.AsNoTracking()
            .Include(i => i.Client)
            .Where(i => i.AgentUserId == AgentId
                        && i.DocumentType == ClientInvoiceDocumentType.Invoice
                        && (i.Status == ClientInvoiceStatus.Sent || i.Status == ClientInvoiceStatus.Approved))
            .ToListAsync();
        ViewBag.Today = today;
        ViewBag.Zone = zone;
        return View(ClientInvoiceAging.From(unpaid, today));
    }

    // 523 (slice 2): the aging page's one-click reminder -- the nightly job's email, sent now, with
    // the same record on the invoice and the same marker, and never twice in a day.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Remind(int id, string? returnTo = null)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var invoice = await _db.ClientInvoices.Include(i => i.Client).Include(i => i.AgentUser).FirstOrDefaultAsync(i => i.Id == id && i.AgentUserId == AgentId);
        if (invoice == null) return NotFound();
        IActionResult Back() => returnTo == "aging" ? RedirectToAction(nameof(Aging)) : RedirectToAction(nameof(Details), new { id });

        var (zone, today) = await AgentDayAsync();
        var overdue = invoice.DocumentType == ClientInvoiceDocumentType.Invoice
                      && invoice.Status is ClientInvoiceStatus.Sent or ClientInvoiceStatus.Approved
                      && invoice.DueDate.HasValue && invoice.DueDate.Value.Date < today;
        if (!overdue)
        {
            TempData["Error"] = $"Invoice {invoice.DocumentNumber} is not overdue, so there is nothing to remind about.";
            return Back();
        }
        if (string.IsNullOrWhiteSpace(invoice.Client?.Email))
        {
            TempData["Error"] = "This client has no email address on file.";
            return Back();
        }
        if (invoice.LastReminderSentAt.HasValue && invoice.LastReminderSentAt.Value > DateTime.UtcNow.AddHours(-24))
        {
            var when = AgentTimeZoneHelper.FromUtc(invoice.LastReminderSentAt.Value, zone);
            TempData["Error"] = $"A reminder for {invoice.DocumentNumber} already went to {invoice.Client.Email} at {when:h:mm tt} on {when:MMM d}. Give it a day.";
            return Back();
        }

        // 523 (slice 3): the adviser's own overdue wording, the same the daily job sends.
        var reminderSettings = await ClientInvoiceReminderSchedule.LoadAsync(_db, AgentId);
        var (subject, html) = IPRO.Scheduler.ClientInvoiceReminderEmail.Build(invoice, BuildPublicDocumentUrl(invoice.ViewToken), reminderSettings, today);
        var result = await _email.SendDetailedAsync(invoice.Client.Email, $"{invoice.Client.FirstName} {invoice.Client.LastName}".Trim(), subject, html, customArgs: AdviserSender.Tags(invoice.AgentUser, EmailStreams.Notify), replyToEmail: AdviserSender.ReplyToEmail(invoice.AgentUser), replyToName: AdviserSender.ReplyToName(invoice.AgentUser));
        await ClientInvoiceEmailLog.RecordAsync(_db, invoice, ClientInvoiceEmailKind.Reminder, invoice.Client.Email, subject, result.Success, result.ProviderMessageId, result.Message);
        if (!result.Success)
        {
            TempData["Error"] = $"The reminder for {invoice.DocumentNumber} could not be sent to {invoice.Client.Email}: {result.Message}";
            return Back();
        }

        invoice.LastReminderSentAt = DateTime.UtcNow;
        invoice.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        TempData["Success"] = $"Reminder for {invoice.DocumentNumber} sent to {invoice.Client.Email}.";
        return Back();
    }

    // 523 (slice 3): the reminder schedule the adviser controls -- which stages are on and what each
    // says -- with a preview of every wording as a sample invoice would read it.
    public async Task<IActionResult> Reminders()
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var saved = await ClientInvoiceReminderSchedule.LoadAsync(_db, AgentId);
        await FillReminderPreviewAsync(saved);
        return View(IPRO.Web.Models.ClientInvoiceReminderForm.From(saved));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reminders(IPRO.Web.Models.ClientInvoiceReminderForm form)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        if (!ModelState.IsValid)
        {
            await FillReminderPreviewAsync(await ClientInvoiceReminderSchedule.LoadAsync(_db, AgentId));
            return View(form);
        }

        await ClientInvoiceReminderSchedule.SaveAsync(_db, form.ToSettings(AgentId));
        TempData["Success"] = "Reminder schedule saved.";
        return RedirectToAction(nameof(Reminders));
    }

    private async Task FillReminderPreviewAsync(ClientInvoiceReminderSettings saved)
    {
        var (_, today) = await AgentDayAsync();
        ViewBag.Saved = saved;
        ViewBag.Today = today;
        ViewBag.Company = await _db.AgentUsers.AsNoTracking().Where(a => a.Id == AgentId).Select(a => a.CompanyName).FirstOrDefaultAsync() ?? string.Empty;
    }

    // 523 (slice 4): the statement for the adviser's accountant -- a month or a quarter, in the
    // adviser's own day: invoiced (by issue date), tax by rate, received (by the day the payment
    // was recorded), and what was still owed at the period's end.
    public async Task<IActionResult> Statement(int? year = null, int? month = null, int? quarter = null)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var (zone, today) = await AgentDayAsync();
        var period = StatementPeriod(year, month, quarter, today);
        ViewBag.Year = period.From.Year;
        ViewBag.Month = period.Quarter.HasValue ? null : (int?)period.From.Month;
        ViewBag.Quarter = period.Quarter;
        ViewBag.PeriodLabel = period.Label;
        ViewBag.Company = await _db.AgentUsers.AsNoTracking().Where(a => a.Id == AgentId).Select(a => a.CompanyName).FirstOrDefaultAsync() ?? string.Empty;
        return View(ClientInvoiceStatement.From(await StatementInvoicesAsync(), period.From, period.To, zone));
    }

    // The statement's rows as a file, one invoice per row.
    public async Task<IActionResult> ExportStatement(int? year = null, int? month = null, int? quarter = null)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var (zone, today) = await AgentDayAsync();
        var period = StatementPeriod(year, month, quarter, today);
        var statement = ClientInvoiceStatement.From(await StatementInvoicesAsync(), period.From, period.To, zone);

        var csv = new StringBuilder();
        csv.AppendLine("Document #,Client,Issue Date,Due Date,Status,Subtotal,Tax Region,Tax Rate,Tax,Total,Currency,Paid On,Paid Method");
        foreach (var row in statement.Invoiced)
        {
            csv.AppendLine(string.Join(",",
                CsvEscape(row.DocumentNumber),
                CsvEscape(row.ClientName),
                CsvEscape(row.IssueDate.ToString("yyyy-MM-dd")),
                CsvEscape(row.DueDate?.ToString("yyyy-MM-dd") ?? string.Empty),
                CsvEscape(row.Status.ToString()),
                CsvEscape(row.Subtotal.ToString("0.00")),
                CsvEscape(row.TaxRegion),
                CsvEscape($"{row.TaxRate * 100:0.000}%"),
                CsvEscape(row.Tax.ToString("0.00")),
                CsvEscape(row.Total.ToString("0.00")),
                CsvEscape(statement.Currency),
                CsvEscape(row.PaidOn?.ToString("yyyy-MM-dd") ?? string.Empty),
                CsvEscape(row.PaidMethod?.ToString() ?? string.Empty)));
        }
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"statement-{period.FileStem}.csv");
    }

    // Xero's sales-invoice import layout (Business > Invoices > Import), one row per line item, for
    // the invoices issued in the period. The tax type and the account code are Xero's own names:
    // the wizard lets the adviser map them on the first import.
    public async Task<IActionResult> ExportXero(int? year = null, int? month = null, int? quarter = null)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var (_, today) = await AgentDayAsync();
        var period = StatementPeriod(year, month, quarter, today);
        var dayAfter = period.To.AddDays(1);
        var invoices = await _db.ClientInvoices.AsNoTracking()
            .Include(i => i.Client)
            .Include(i => i.LineItems)
            .Where(i => i.AgentUserId == AgentId
                        && i.DocumentType == ClientInvoiceDocumentType.Invoice
                        && (i.Status == ClientInvoiceStatus.Sent || i.Status == ClientInvoiceStatus.Approved || i.Status == ClientInvoiceStatus.Paid)
                        && i.IssueDate >= period.From && i.IssueDate < dayAfter)
            .OrderBy(i => i.IssueDate).ThenBy(i => i.DocumentNumber)
            .ToListAsync();

        var csv = new StringBuilder();
        csv.AppendLine("*ContactName,EmailAddress,POAddressLine1,POAddressLine2,POAddressLine3,POAddressLine4,POCity,PORegion,POPostalCode,POCountry,*InvoiceNumber,Reference,*InvoiceDate,*DueDate,Total,InventoryItemCode,*Description,*Quantity,*UnitAmount,Discount,*AccountCode,*TaxType,TaxAmount,TrackingName1,TrackingOption1,TrackingName2,TrackingOption2,Currency,BrandingTheme");
        foreach (var invoice in invoices)
        {
            var contact = $"{invoice.Client?.FirstName} {invoice.Client?.LastName}".Trim();
            var taxedType = invoice.TaxRate == 0 ? "Tax Exempt" : $"{invoice.TaxRegion} {invoice.TaxRate * 100:0.###}%".Trim();
            var lines = invoice.LineItems.OrderBy(l => l.SortOrder).ToList();
            if (lines.Count == 0)
            {
                lines.Add(new ClientInvoiceLineItem { Description = "Services", Quantity = 1, UnitPrice = invoice.SubTotal, Amount = invoice.SubTotal });
            }
            void XeroRow(string description, string quantity, string unitAmount, string discountPercent, string taxType, decimal lineTax) =>
                csv.AppendLine(string.Join(",",
                    CsvEscape(contact),
                    CsvEscape(invoice.Client?.Email ?? string.Empty),
                    string.Empty, string.Empty, string.Empty, string.Empty,
                    CsvEscape(invoice.Client?.City ?? string.Empty),
                    CsvEscape(invoice.Client?.Province ?? string.Empty),
                    CsvEscape(invoice.Client?.PostalCode ?? string.Empty),
                    CsvEscape(invoice.Client?.Country ?? string.Empty),
                    CsvEscape(invoice.DocumentNumber),
                    string.Empty,
                    CsvEscape(invoice.IssueDate.ToString("yyyy-MM-dd")),
                    CsvEscape((invoice.DueDate ?? invoice.IssueDate).ToString("yyyy-MM-dd")),
                    CsvEscape(invoice.Total.ToString("0.00")),
                    string.Empty,
                    CsvEscape(description),
                    CsvEscape(quantity),
                    CsvEscape(unitAmount),
                    CsvEscape(discountPercent),
                    "200",
                    CsvEscape(taxType),
                    CsvEscape(lineTax.ToString("0.00")),
                    string.Empty, string.Empty, string.Empty, string.Empty,
                    CsvEscape(invoice.Currency),
                    string.Empty));

            foreach (var line in lines)
            {
                // 563: a line that is not ticked carries no tax and says so.
                var taxType = line.IsTaxable ? taxedType : "Tax Exempt";
                var lineTax = line.IsTaxable ? Math.Round(line.Amount * invoice.TaxRate, 2) : 0m;
                // 566: a percent off an item goes in Xero's own Discount column. Dollars off an item have
                // no column there, so the unit amount is the price after them.
                var percentOff = line.DiscountAmount > 0 && line.DiscountType == InvoiceDiscountType.Percent;
                var unit = line.DiscountAmount > 0 && !percentOff && line.Quantity != 0 ? Math.Round(line.Amount / line.Quantity, 4) : line.UnitPrice;
                XeroRow(string.IsNullOrWhiteSpace(line.Description) ? "Services" : line.Description, line.Quantity.ToString("0.##"), unit.ToString("0.####"),
                    percentOff ? line.DiscountValue.ToString("0.##") : string.Empty, taxType, lineTax);
            }

            // 566: a discount on the whole invoice is a line of its own, split as the tax was: the taxed
            // lines' share carries the invoice's tax type (and takes its tax back), the rest is exempt.
            if (invoice.DiscountAmount > 0)
            {
                var label = ("Discount " + ClientInvoiceMath.Words(invoice.DiscountType, invoice.DiscountValue)).Trim();
                var itemsTotal = lines.Sum(l => l.Amount);
                var taxableItems = lines.Where(l => l.IsTaxable).Sum(l => l.Amount);
                var taxedShare = invoice.TaxRate == 0 ? 0m : taxableItems - ClientInvoiceMath.TaxableAfterDiscount(itemsTotal, taxableItems, invoice.DiscountAmount);
                var exemptShare = invoice.DiscountAmount - taxedShare;
                if (taxedShare > 0) XeroRow(label, "1", (0m - taxedShare).ToString("0.##"), string.Empty, taxedType, 0m - Math.Round(taxedShare * invoice.TaxRate, 2));
                if (exemptShare > 0) XeroRow(label, "1", (0m - exemptShare).ToString("0.##"), string.Empty, "Tax Exempt", 0m);
            }
        }
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"xero-invoices-{period.FileStem}.csv");
    }

    // A month by default (this one, in the adviser's day); a quarter when one is asked for.
    private static (DateTime From, DateTime To, int? Quarter, string Label, string FileStem) StatementPeriod(int? year, int? month, int? quarter, DateTime today)
    {
        var y = Math.Clamp(year ?? today.Year, 2000, 2100);
        if (quarter is >= 1 and <= 4)
        {
            var (from, to) = ClientInvoiceStatement.Quarter(y, quarter.Value);
            return (from, to, quarter, $"Q{quarter} {y} ({from:MMMM d} to {to:MMMM d, yyyy})", $"{y}-q{quarter}");
        }
        var m = Math.Clamp(month ?? today.Month, 1, 12);
        var (monthFrom, monthTo) = ClientInvoiceStatement.Month(y, m);
        return (monthFrom, monthTo, null, monthFrom.ToString("MMMM yyyy"), $"{y}-{m:00}");
    }

    private async Task<List<ClientInvoice>> StatementInvoicesAsync() =>
        await _db.ClientInvoices.AsNoTracking()
            .Include(i => i.Client)
            .Where(i => i.AgentUserId == AgentId && i.DocumentType == ClientInvoiceDocumentType.Invoice)
            .ToListAsync();

    public async Task<IActionResult> Index(string documentType = "all", string status = "all", int? clientId = null, string? search = null, int page = 1)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        page = Math.Max(1, page);
        var (glanceZone, glanceToday) = await AgentDayAsync();
        var query = BuildFilteredQuery(documentType, status, clientId, search, glanceToday);

        var totalCount = await query.CountAsync();
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)PageSize));
        page = Math.Min(page, totalPages);

        ViewBag.DocumentType = documentType;
        ViewBag.Status = status;
        ViewBag.ClientId = clientId;
        ViewBag.Search = search;
        ViewBag.Page = page;
        ViewBag.TotalPages = totalPages;
        ViewBag.TotalCount = totalCount;

        // 523: the adviser's money at a glance -- every invoice of theirs, read in their own day
        // (INVARIANTS rule 10), whatever filter the list below is showing.
        var glanceInvoices = await _db.ClientInvoices.AsNoTracking()
            .Where(i => i.AgentUserId == AgentId && i.DocumentType == ClientInvoiceDocumentType.Invoice)
            .ToListAsync();
        ViewBag.Glance = ClientInvoiceGlance.From(glanceInvoices, glanceToday, glanceZone);
        ViewBag.Clients = await _db.Clients.AsNoTracking().Where(c => c.AgentUserId == AgentId).OrderBy(c => c.LastName).ThenBy(c => c.FirstName).ToListAsync();

        var invoices = await query
            .OrderByDescending(i => i.IssueDate)
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync();

        // 452: the latest email per invoice on this page drives the Delivery column.
        var pageIds = invoices.Select(i => i.Id).ToList();
        var latestEmails = await _db.ClientInvoiceEmails.AsNoTracking()
            .Where(e => pageIds.Contains(e.ClientInvoiceId))
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync();
        ViewBag.LatestEmail = latestEmails.GroupBy(e => e.ClientInvoiceId).ToDictionary(g => g.Key, g => g.First());

        return View(invoices);
    }

    public async Task<IActionResult> Export(string documentType = "all", string status = "all", int? clientId = null, string? search = null)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var invoices = await BuildFilteredQuery(documentType, status, clientId, search, (await AgentDayAsync()).Today)
            .OrderByDescending(i => i.IssueDate)
            .ToListAsync();

        var csv = new StringBuilder();
        csv.AppendLine("Document #,Type,Status,Client,Issue Date,Due Date,SubTotal,Tax,Total,Currency,Paid At,Paid Method");
        foreach (var invoice in invoices)
        {
            csv.AppendLine(string.Join(",",
                CsvEscape(invoice.DocumentNumber),
                CsvEscape(invoice.DocumentType.ToString()),
                CsvEscape(invoice.Status.ToString()),
                CsvEscape($"{invoice.Client?.FirstName} {invoice.Client?.LastName}".Trim()),
                CsvEscape(invoice.IssueDate.ToString("yyyy-MM-dd")),
                CsvEscape(invoice.DueDate?.ToString("yyyy-MM-dd") ?? string.Empty),
                CsvEscape(invoice.SubTotal.ToString("0.00")),
                CsvEscape(invoice.TaxAmount.ToString("0.00")),
                CsvEscape(invoice.Total.ToString("0.00")),
                CsvEscape(invoice.Currency),
                CsvEscape(invoice.PaidAt?.ToString("yyyy-MM-dd") ?? string.Empty),
                CsvEscape(invoice.PaidMethod?.ToString() ?? string.Empty)));
        }

        var bytes = Encoding.UTF8.GetBytes(csv.ToString());
        return File(bytes, "text/csv", $"client-invoices-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    // Invoices only (not Estimates, a separate QuickBooks concept) - one row per line item, invoice
    // fields repeated per row, matching the flat-file shape QuickBooks Online's own "Import Invoices"
    // wizard expects (Settings -> Import Data). There's no product/service catalog anywhere in this
    // app, so "Product/Service" is a fixed "Services" value QBO will auto-create as an Item on first
    // import; the actual line-item text goes in Description. This can't make QBO mark anything paid -
    // a plain CSV import only creates/updates invoice records, not payment application - so PaidDate
    // is informational only.
    public async Task<IActionResult> ExportQuickBooks(string status = "all", int? clientId = null, string? search = null)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var invoices = await BuildFilteredQuery("invoice", status, clientId, search, (await AgentDayAsync()).Today)
            .Include(i => i.LineItems)
            .OrderByDescending(i => i.IssueDate)
            .ToListAsync();

        var csv = new StringBuilder();
        csv.AppendLine("InvoiceNo,Customer,InvoiceDate,DueDate,Terms,Product/Service,Description,Qty,Rate,Amount,Currency,Status,PaidDate");
        foreach (var invoice in invoices)
        {
            var customer = $"{invoice.Client?.FirstName} {invoice.Client?.LastName}".Trim();
            var terms = invoice.DueDate.HasValue ? $"Net {Math.Max(0, (invoice.DueDate.Value.Date - invoice.IssueDate.Date).Days)}" : string.Empty;
            var lineItems = invoice.LineItems.OrderBy(l => l.SortOrder).ToList();
            if (lineItems.Count == 0)
            {
                // Still emit one row so the invoice itself isn't silently dropped from the export.
                lineItems.Add(new ClientInvoiceLineItem { Description = string.Empty, Quantity = 1, UnitPrice = invoice.Total, Amount = invoice.Total });
            }

            void QuickBooksRow(string description, string quantity, string rate, decimal amount) =>
                csv.AppendLine(string.Join(",",
                    CsvEscape(invoice.DocumentNumber),
                    CsvEscape(customer),
                    CsvEscape(invoice.IssueDate.ToString("yyyy-MM-dd")),
                    CsvEscape(invoice.DueDate?.ToString("yyyy-MM-dd") ?? string.Empty),
                    CsvEscape(terms),
                    CsvEscape("Services"),
                    CsvEscape(description),
                    CsvEscape(quantity),
                    CsvEscape(rate),
                    CsvEscape(amount.ToString("0.00")),
                    CsvEscape(invoice.Currency),
                    CsvEscape(invoice.Status.ToString()),
                    CsvEscape(invoice.PaidAt?.ToString("yyyy-MM-dd") ?? string.Empty)));

            foreach (var line in lineItems)
            {
                // 566: with a discount on the item, the rate is the price after it, so quantity x rate
                // is still the amount.
                var rate = line.DiscountAmount > 0 && line.Quantity != 0
                    ? Math.Round(line.Amount / line.Quantity, 4).ToString("0.00##")
                    : line.UnitPrice.ToString("0.00");
                QuickBooksRow(line.Description, line.Quantity.ToString("0.##"), rate, line.Amount);
            }

            // 566: a discount on the whole invoice is a row of its own, so the rows add up to the invoice.
            if (invoice.DiscountAmount > 0)
            {
                var off = 0m - invoice.DiscountAmount;
                QuickBooksRow(("Discount " + ClientInvoiceMath.Words(invoice.DiscountType, invoice.DiscountValue)).Trim(), "1", off.ToString("0.00"), off);
            }
        }

        var bytes = Encoding.UTF8.GetBytes(csv.ToString());
        return File(bytes, "text/csv", $"quickbooks-invoices-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    public async Task<IActionResult> Create(ClientInvoiceDocumentType documentType = ClientInvoiceDocumentType.Invoice)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        await LoadClientsAsync();
        // 564: a new document is dated the adviser's own today (it was the UTC date, which is tomorrow
        // from eight in the evening in Toronto).
        var (_, today) = await AgentDayAsync();
        return View("Edit", new ClientInvoiceEditViewModel { DocumentType = documentType, IssueDate = today, DueDate = today.AddDays(15) });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ClientInvoiceEditViewModel model)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == model.ClientId && c.AgentUserId == AgentId);
        if (client == null)
        {
            ModelState.AddModelError(nameof(model.ClientId), "Choose a client.");
        }
        if (model.LineItems.Count == 0 || model.LineItems.All(l => string.IsNullOrWhiteSpace(l.Description)))
        {
            ModelState.AddModelError(string.Empty, "Add at least one line item.");
        }
        if (!ModelState.IsValid)
        {
            await LoadClientsAsync();
            return View("Edit", model);
        }

        var invoice = new ClientInvoice
        {
            AgentUserId = AgentId,
            ClientId = model.ClientId,
            DocumentType = model.DocumentType,
            Status = ClientInvoiceStatus.Draft,
            IssueDate = model.IssueDate,
            DueDate = model.DocumentType == ClientInvoiceDocumentType.Invoice ? model.DueDate : null,
            Notes = model.Notes?.Trim(),
            ViewToken = Guid.NewGuid().ToString("N"),
            DocumentNumber = await _invoiceService.GenerateDocumentNumberAsync(AgentId, model.DocumentType)
        };

        ApplyDiscount(invoice, model);
        ApplyLineItems(invoice, model.LineItems, model.DiscountMode);
        await ApplyTotalsAsync(invoice, client!);

        _db.ClientInvoices.Add(invoice);
        await _db.SaveChangesAsync();

        TempData["Success"] = $"{(model.DocumentType == ClientInvoiceDocumentType.Estimate ? "Estimate" : "Invoice")} {invoice.DocumentNumber} created.";
        return RedirectToAction(nameof(Details), new { id = invoice.Id });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var invoice = await _db.ClientInvoices.Include(i => i.LineItems).FirstOrDefaultAsync(i => i.Id == id && i.AgentUserId == AgentId);
        if (invoice == null) return NotFound();
        if (invoice.Status != ClientInvoiceStatus.Draft)
        {
            TempData["Error"] = "Only drafts can be edited.";
            return RedirectToAction(nameof(Details), new { id });
        }

        await LoadClientsAsync();
        return View(new ClientInvoiceEditViewModel
        {
            Id = invoice.Id,
            DocumentType = invoice.DocumentType,
            ClientId = invoice.ClientId,
            IssueDate = invoice.IssueDate,
            DueDate = invoice.DueDate,
            Notes = invoice.Notes,
            // 566: the discount as it was saved -- on the whole invoice, on the items, or none.
            DiscountMode = invoice.DiscountAmount > 0 ? ClientInvoiceMath.ModeInvoice
                : invoice.LineItems.Any(l => l.DiscountAmount > 0) ? ClientInvoiceMath.ModeLines : ClientInvoiceMath.ModeNone,
            DiscountType = invoice.DiscountAmount > 0 ? invoice.DiscountType : InvoiceDiscountType.Percent,
            DiscountValue = invoice.DiscountAmount > 0 ? invoice.DiscountValue : 0m,
            LineItems = invoice.LineItems.OrderBy(l => l.SortOrder).Select(l => new ClientInvoiceLineItemInputModel
            {
                Description = l.Description,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                IsTaxable = l.IsTaxable,
                DiscountType = l.DiscountAmount > 0 ? l.DiscountType : InvoiceDiscountType.Percent,
                DiscountValue = l.DiscountAmount > 0 ? l.DiscountValue : 0m
            }).ToList()
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ClientInvoiceEditViewModel model)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var invoice = await _db.ClientInvoices.Include(i => i.LineItems).FirstOrDefaultAsync(i => i.Id == id && i.AgentUserId == AgentId);
        if (invoice == null) return NotFound();
        if (invoice.Status != ClientInvoiceStatus.Draft)
        {
            TempData["Error"] = "Only drafts can be edited.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == model.ClientId && c.AgentUserId == AgentId);
        if (client == null)
        {
            ModelState.AddModelError(nameof(model.ClientId), "Choose a client.");
        }
        if (model.LineItems.Count == 0 || model.LineItems.All(l => string.IsNullOrWhiteSpace(l.Description)))
        {
            ModelState.AddModelError(string.Empty, "Add at least one line item.");
        }
        if (!ModelState.IsValid)
        {
            await LoadClientsAsync();
            model.Id = id;
            return View(model);
        }

        invoice.ClientId = model.ClientId;
        invoice.IssueDate = model.IssueDate;
        invoice.DueDate = invoice.DocumentType == ClientInvoiceDocumentType.Invoice ? model.DueDate : null;
        invoice.Notes = model.Notes?.Trim();
        invoice.UpdatedAt = DateTime.UtcNow;

        _db.ClientInvoiceLineItems.RemoveRange(invoice.LineItems);
        invoice.LineItems.Clear();
        ApplyDiscount(invoice, model);
        ApplyLineItems(invoice, model.LineItems, model.DiscountMode);
        await ApplyTotalsAsync(invoice, client!);

        await _db.SaveChangesAsync();

        TempData["Success"] = "Draft updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var invoice = await _db.ClientInvoices
            .Include(i => i.LineItems)
            .Include(i => i.Client)
            .FirstOrDefaultAsync(i => i.Id == id && i.AgentUserId == AgentId);
        if (invoice == null) return NotFound();

        ViewBag.Agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == AgentId);
        // 515: the adviser's website logo brands the document, when they have one.
        ViewBag.LogoUrl = await _db.AgentWebsites.AsNoTracking().Where(w => w.AgentUserId == AgentId).Select(w => w.LogoUrl).FirstOrDefaultAsync();
        ViewBag.PublicUrl = BuildPublicDocumentUrl(invoice.ViewToken);
        // 452: what happened to each email this invoice generated, newest first.
        ViewBag.EmailHistory = await _db.ClientInvoiceEmails.AsNoTracking()
            .Where(e => e.ClientInvoiceId == invoice.Id)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync();
        return View(invoice);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(int id)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        // 542: the line items name what the invoice is for in the email.
        var invoice = await _db.ClientInvoices.Include(i => i.Client).Include(i => i.AgentUser).Include(i => i.LineItems).FirstOrDefaultAsync(i => i.Id == id && i.AgentUserId == AgentId);
        if (invoice == null) return NotFound();
        if (string.IsNullOrWhiteSpace(invoice.Client?.Email))
        {
            TempData["Error"] = "This client has no email address on file.";
            return RedirectToAction(nameof(Details), new { id });
        }

        // 542: a letter from the adviser, not a bare button (the owner's estimate in that shape went to
        // Yahoo's Spam): the greeting, the number, amount, dates and items, and the adviser's sign-off.
        var html = IPRO.Scheduler.ClientInvoiceEmail.Html(invoice, BuildPublicDocumentUrl(invoice.ViewToken));
        var docLabelCap = invoice.DocumentType == ClientInvoiceDocumentType.Estimate ? "Estimate" : "Invoice";
        // 530: the business names the email, so the client knows whose invoice it is before opening it.
        var business = AdviserSender.BusinessName(invoice.AgentUser);
        var subject = business.Length == 0 ? $"{docLabelCap} {invoice.DocumentNumber}" : $"{docLabelCap} {invoice.DocumentNumber} from {business}";

        // 452 (2026-09-02): the mail goes FIRST and the invoice only becomes Sent once the provider
        // accepted it. Until this change the status was stamped before the send and the provider's
        // answer was thrown away, so a rejected address or a quota refusal still showed "Invoice
        // sent to x" -- and the agent had no way to know. Every attempt is recorded, with the
        // provider's message id so the delivery pipeline can report Delivered / Bounced on it.
        var result = await _email.SendDetailedAsync(invoice.Client.Email, $"{invoice.Client.FirstName} {invoice.Client.LastName}".Trim(), subject, html, customArgs: AdviserSender.Tags(invoice.AgentUser, EmailStreams.Notify), replyToEmail: AdviserSender.ReplyToEmail(invoice.AgentUser), replyToName: AdviserSender.ReplyToName(invoice.AgentUser));
        await ClientInvoiceEmailLog.RecordAsync(_db, invoice, ClientInvoiceEmailKind.Send, invoice.Client.Email, subject, result.Success, result.ProviderMessageId, result.Message);
        if (!result.Success)
        {
            TempData["Error"] = $"{docLabelCap} {invoice.DocumentNumber} could not be sent to {invoice.Client.Email}: {result.Message} Nothing was changed; check the client's email address and try again.";
            return RedirectToAction(nameof(Details), new { id });
        }

        // A5-M-RESEND (fixed 2026-08-20): re-sending a PAID invoice used to flip it back to Sent,
        // which put it straight back into the overdue-reminder query -- the client got dunned for
        // a bill they had already settled. A resend of a paid invoice is just a copy.
        if (invoice.Status != ClientInvoiceStatus.Paid)
        {
            invoice.Status = ClientInvoiceStatus.Sent;
        }
        invoice.SentAt = DateTime.UtcNow;
        invoice.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = $"{docLabelCap} sent to {invoice.Client.Email}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkPaid(int id, ClientInvoicePaymentMethod method)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var invoice = await _db.ClientInvoices.FirstOrDefaultAsync(i => i.Id == id && i.AgentUserId == AgentId);
        if (invoice == null) return NotFound();

        invoice.Status = ClientInvoiceStatus.Paid;
        invoice.PaidAt = DateTime.UtcNow;
        invoice.PaidMethod = method;
        invoice.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Invoice marked as paid.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Void(int id)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var invoice = await _db.ClientInvoices.FirstOrDefaultAsync(i => i.Id == id && i.AgentUserId == AgentId);
        if (invoice == null) return NotFound();

        invoice.Status = ClientInvoiceStatus.Void;
        invoice.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = $"{(invoice.DocumentType == ClientInvoiceDocumentType.Estimate ? "Estimate" : "Invoice")} voided.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ConvertToInvoice(int id)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var invoice = await _db.ClientInvoices.FirstOrDefaultAsync(i => i.Id == id && i.AgentUserId == AgentId);
        if (invoice == null) return NotFound();
        if (invoice.DocumentType != ClientInvoiceDocumentType.Estimate || invoice.Status != ClientInvoiceStatus.Approved)
        {
            TempData["Error"] = "Only an approved estimate can be converted to an invoice.";
            return RedirectToAction(nameof(Details), new { id });
        }

        invoice.DocumentType = ClientInvoiceDocumentType.Invoice;
        invoice.Status = ClientInvoiceStatus.Draft;
        invoice.DocumentNumber = await _invoiceService.GenerateDocumentNumberAsync(AgentId, ClientInvoiceDocumentType.Invoice);
        var (_, today) = await AgentDayAsync();   // 564: the adviser's own day
        invoice.IssueDate = today;
        invoice.DueDate = today.AddDays(15);
        invoice.SentAt = null;
        invoice.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = $"Converted to invoice {invoice.DocumentNumber}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Duplicate(int id)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var source = await _db.ClientInvoices.Include(i => i.LineItems).Include(i => i.Client).FirstOrDefaultAsync(i => i.Id == id && i.AgentUserId == AgentId);
        if (source == null) return NotFound();

        var (_, today) = await AgentDayAsync();   // 564: the adviser's own day
        var copy = new ClientInvoice
        {
            AgentUserId = AgentId,
            ClientId = source.ClientId,
            DocumentType = source.DocumentType,
            Status = ClientInvoiceStatus.Draft,
            IssueDate = today,
            DueDate = source.DocumentType == ClientInvoiceDocumentType.Invoice ? today.AddDays(15) : null,
            Notes = source.Notes,
            ViewToken = Guid.NewGuid().ToString("N"),
            DocumentNumber = await _invoiceService.GenerateDocumentNumberAsync(AgentId, source.DocumentType),
            DiscountType = source.DiscountType,     // 566: the copy keeps the discount, on the invoice and on its items
            DiscountValue = source.DiscountValue,
            DiscountAmount = source.DiscountAmount,
            SubTotal = source.SubTotal,
            TaxRegion = source.TaxRegion,
            TaxRate = source.TaxRate,
            TaxAmount = source.TaxAmount,
            Total = source.Total,
            Currency = source.Currency,
            LineItems = source.LineItems.OrderBy(l => l.SortOrder).Select(l => new ClientInvoiceLineItem
            {
                Description = l.Description,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                DiscountType = l.DiscountType,
                DiscountValue = l.DiscountValue,
                DiscountAmount = l.DiscountAmount,
                Amount = l.Amount,
                IsTaxable = l.IsTaxable,
                SortOrder = l.SortOrder
            }).ToList()
        };
        // 563: the copy is taxed by what is true today (the client may have a province now), not by
        // the figures the original was saved with.
        if (source.Client != null)
        {
            await ApplyTotalsAsync(copy, source.Client);
        }

        _db.ClientInvoices.Add(copy);
        await _db.SaveChangesAsync();

        TempData["Success"] = $"Duplicated as {copy.DocumentNumber}.";
        return RedirectToAction(nameof(Details), new { id = copy.Id });
    }

    private async Task LoadClientsAsync()
    {
        var clients = await _db.Clients.AsNoTracking().Where(c => c.AgentUserId == AgentId).OrderBy(c => c.LastName).ThenBy(c => c.FirstName).ToListAsync();
        ViewBag.Clients = clients;
        // 563: the editor shows the subtotal, the tax and the total while the invoice is written, so
        // it needs each client's rule (the same one the save applies).
        var business = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == AgentId);
        var rates = await _invoiceService.ActiveTaxRatesAsync();
        ViewBag.TaxByClient = clients.ToDictionary(c => c.Id, c => IPRO.Business.Services.ClientInvoiceTaxRules.Resolve(c, business, rates));
        // 568: what this business has invoiced before, offered in the editor as a description is typed.
        ViewBag.RememberedItems = await RememberedInvoiceItems.ForAgentAsync(_db, AgentId);
    }

    // 566: one discount on the whole document, when that is where the editor says the discount is.
    private static void ApplyDiscount(ClientInvoice invoice, ClientInvoiceEditViewModel model)
    {
        var typed = model.DiscountValue ?? 0m;
        var onInvoice = ClientInvoiceMath.CleanMode(model.DiscountMode) == ClientInvoiceMath.ModeInvoice && typed > 0 && model.DiscountType != InvoiceDiscountType.None;
        invoice.DiscountType = onInvoice ? model.DiscountType : InvoiceDiscountType.None;
        invoice.DiscountValue = !onInvoice ? 0m : model.DiscountType == InvoiceDiscountType.Percent ? Math.Min(typed, 100m) : typed;
    }

    // 566: a line's amount is quantity x unit price less its own discount, when the discount is on the items.
    private static void ApplyLineItems(ClientInvoice invoice, List<ClientInvoiceLineItemInputModel> lineItems, string? discountMode = null)
    {
        var onLines = ClientInvoiceMath.CleanMode(discountMode) == ClientInvoiceMath.ModeLines;
        var sortOrder = 0;
        foreach (var item in lineItems.Where(l => !string.IsNullOrWhiteSpace(l.Description)))
        {
            var gross = ClientInvoiceMath.LineGross(item.Quantity, item.UnitPrice);
            var typed = item.DiscountValue ?? 0m;
            var discount = onLines ? ClientInvoiceMath.Discount(gross, item.DiscountType, typed) : 0m;
            invoice.LineItems.Add(new ClientInvoiceLineItem
            {
                Description = item.Description!.Trim(),
                Quantity = item.Quantity <= 0 ? 1 : item.Quantity,
                UnitPrice = item.UnitPrice,
                DiscountType = discount > 0 ? item.DiscountType : InvoiceDiscountType.None,
                DiscountValue = discount <= 0 ? 0m : item.DiscountType == InvoiceDiscountType.Percent ? Math.Min(typed, 100m) : typed,
                DiscountAmount = discount,
                Amount = gross - discount,
                IsTaxable = item.IsTaxable,
                SortOrder = sortOrder++
            });
        }
    }

    // 563: tax is charged on the lines that are ticked, at the client's province's rate or, when
    // the client has no province on file, the business's own (ClientInvoiceTaxRules).
    // 566: and on the price after any discount (ClientInvoiceMath). SubTotal is what is left after
    // a discount on the whole invoice, so SubTotal + TaxAmount is still the Total.
    private async Task ApplyTotalsAsync(ClientInvoice invoice, Client client)
    {
        var totals = ClientInvoiceMath.Totals(invoice.LineItems.Select(l => (l.Amount, l.IsTaxable)), invoice.DiscountType, invoice.DiscountValue);
        invoice.DiscountAmount = totals.InvoiceDiscount;
        if (totals.InvoiceDiscount <= 0)
        {
            invoice.DiscountType = InvoiceDiscountType.None;
            invoice.DiscountValue = 0m;
        }
        invoice.SubTotal = totals.SubTotal;
        var business = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == AgentId);
        var tax = await _invoiceService.CalculateTaxAsync(client, business, totals.TaxableAmount);
        invoice.TaxRate = tax.Rate;
        invoice.TaxAmount = tax.Amount;
        invoice.TaxRegion = tax.Region;
        invoice.Total = invoice.SubTotal + invoice.TaxAmount;
    }

    private IQueryable<ClientInvoice> BuildFilteredQuery(string documentType, string status, int? clientId, string? search, DateTime today)
    {
        var query = _db.ClientInvoices
            .AsNoTracking()
            .Include(i => i.Client)
            .Where(i => i.AgentUserId == AgentId);

        query = documentType?.Trim().ToLowerInvariant() switch
        {
            "estimate" => query.Where(i => i.DocumentType == ClientInvoiceDocumentType.Estimate),
            "invoice" => query.Where(i => i.DocumentType == ClientInvoiceDocumentType.Invoice),
            _ => query
        };

        query = status?.Trim().ToLowerInvariant() switch
        {
            "draft" => query.Where(i => i.Status == ClientInvoiceStatus.Draft),
            "sent" => query.Where(i => i.Status == ClientInvoiceStatus.Sent),
            "approved" => query.Where(i => i.Status == ClientInvoiceStatus.Approved),
            "declined" => query.Where(i => i.Status == ClientInvoiceStatus.Declined),
            "paid" => query.Where(i => i.Status == ClientInvoiceStatus.Paid),
            "void" => query.Where(i => i.Status == ClientInvoiceStatus.Void),
            // 523 (slice 2): what is owed and past its due date, in the adviser's own day.
            "overdue" => query.Where(i => i.DocumentType == ClientInvoiceDocumentType.Invoice
                                          && (i.Status == ClientInvoiceStatus.Sent || i.Status == ClientInvoiceStatus.Approved)
                                          && i.DueDate != null && i.DueDate < today),
            _ => query
        };

        if (clientId.HasValue)
        {
            query = query.Where(i => i.ClientId == clientId.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(i =>
                i.DocumentNumber.Contains(search) ||
                i.Client.FirstName.Contains(search) ||
                i.Client.LastName.Contains(search) ||
                i.Client.Email.Contains(search));
        }

        return query;
    }

    private static string CsvEscape(string? value)
    {
        value ??= string.Empty;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private async Task<IActionResult?> RequireClientInvoicingAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.ClientInvoicing);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Index", "Billing");
    }
}
