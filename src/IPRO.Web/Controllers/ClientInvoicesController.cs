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

    public async Task<IActionResult> Index(string documentType = "all", string status = "all", int? clientId = null, string? search = null, int page = 1)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        page = Math.Max(1, page);
        var query = BuildFilteredQuery(documentType, status, clientId, search);

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

        var invoices = await BuildFilteredQuery(documentType, status, clientId, search)
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

        var invoices = await BuildFilteredQuery("invoice", status, clientId, search)
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

            foreach (var line in lineItems)
            {
                csv.AppendLine(string.Join(",",
                    CsvEscape(invoice.DocumentNumber),
                    CsvEscape(customer),
                    CsvEscape(invoice.IssueDate.ToString("yyyy-MM-dd")),
                    CsvEscape(invoice.DueDate?.ToString("yyyy-MM-dd") ?? string.Empty),
                    CsvEscape(terms),
                    CsvEscape("Services"),
                    CsvEscape(line.Description),
                    CsvEscape(line.Quantity.ToString("0.##")),
                    CsvEscape(line.UnitPrice.ToString("0.00")),
                    CsvEscape(line.Amount.ToString("0.00")),
                    CsvEscape(invoice.Currency),
                    CsvEscape(invoice.Status.ToString()),
                    CsvEscape(invoice.PaidAt?.ToString("yyyy-MM-dd") ?? string.Empty)));
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
        return View("Edit", new ClientInvoiceEditViewModel { DocumentType = documentType });
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

        ApplyLineItems(invoice, model.LineItems);
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
            LineItems = invoice.LineItems.OrderBy(l => l.SortOrder).Select(l => new ClientInvoiceLineItemInputModel
            {
                Description = l.Description,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice
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
        ApplyLineItems(invoice, model.LineItems);
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

        var invoice = await _db.ClientInvoices.Include(i => i.Client).Include(i => i.AgentUser).FirstOrDefaultAsync(i => i.Id == id && i.AgentUserId == AgentId);
        if (invoice == null) return NotFound();
        if (string.IsNullOrWhiteSpace(invoice.Client?.Email))
        {
            TempData["Error"] = "This client has no email address on file.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var publicUrl = BuildPublicDocumentUrl(invoice.ViewToken);
        var docLabel = invoice.DocumentType == ClientInvoiceDocumentType.Estimate ? "estimate" : "invoice";
        var senderName = $"{invoice.AgentUser.FirstName} {invoice.AgentUser.LastName}".Trim();
        var html = $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              <div style="padding:22px;background:#1457d9;color:white"><h1 style="margin:0;font-size:24px">{System.Net.WebUtility.HtmlEncode(invoice.AgentUser.CompanyName)}</h1></div>
              <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                <p>{System.Net.WebUtility.HtmlEncode(senderName)} sent you {(invoice.DocumentType == ClientInvoiceDocumentType.Estimate ? "an" : "an")} {docLabel} <strong>{System.Net.WebUtility.HtmlEncode(invoice.DocumentNumber)}</strong> for <strong>${invoice.Total:N2} {invoice.Currency}</strong>.</p>
                <p><a href="{publicUrl}" style="display:inline-block;padding:11px 18px;background:#1457d9;color:white;text-decoration:none;border-radius:6px">View {docLabel}</a></p>
              </div>
            </div>
            """;
        var subject = $"{senderName} sent you {docLabel} {invoice.DocumentNumber}";
        var docLabelCap = invoice.DocumentType == ClientInvoiceDocumentType.Estimate ? "Estimate" : "Invoice";

        // 452 (2026-09-02): the mail goes FIRST and the invoice only becomes Sent once the provider
        // accepted it. Until this change the status was stamped before the send and the provider's
        // answer was thrown away, so a rejected address or a quota refusal still showed "Invoice
        // sent to x" -- and the agent had no way to know. Every attempt is recorded, with the
        // provider's message id so the delivery pipeline can report Delivered / Bounced on it.
        var result = await _email.SendDetailedAsync(invoice.Client.Email, $"{invoice.Client.FirstName} {invoice.Client.LastName}".Trim(), subject, html);
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
        invoice.IssueDate = DateTime.UtcNow.Date;
        invoice.DueDate = DateTime.UtcNow.Date.AddDays(15);
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

        var copy = new ClientInvoice
        {
            AgentUserId = AgentId,
            ClientId = source.ClientId,
            DocumentType = source.DocumentType,
            Status = ClientInvoiceStatus.Draft,
            IssueDate = DateTime.UtcNow.Date,
            DueDate = source.DocumentType == ClientInvoiceDocumentType.Invoice ? DateTime.UtcNow.Date.AddDays(15) : null,
            Notes = source.Notes,
            ViewToken = Guid.NewGuid().ToString("N"),
            DocumentNumber = await _invoiceService.GenerateDocumentNumberAsync(AgentId, source.DocumentType),
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
                Amount = l.Amount,
                SortOrder = l.SortOrder
            }).ToList()
        };

        _db.ClientInvoices.Add(copy);
        await _db.SaveChangesAsync();

        TempData["Success"] = $"Duplicated as {copy.DocumentNumber}.";
        return RedirectToAction(nameof(Details), new { id = copy.Id });
    }

    private async Task LoadClientsAsync()
    {
        ViewBag.Clients = await _db.Clients.AsNoTracking().Where(c => c.AgentUserId == AgentId).OrderBy(c => c.LastName).ThenBy(c => c.FirstName).ToListAsync();
    }

    private static void ApplyLineItems(ClientInvoice invoice, List<ClientInvoiceLineItemInputModel> lineItems)
    {
        var sortOrder = 0;
        foreach (var item in lineItems.Where(l => !string.IsNullOrWhiteSpace(l.Description)))
        {
            invoice.LineItems.Add(new ClientInvoiceLineItem
            {
                Description = item.Description!.Trim(),
                Quantity = item.Quantity <= 0 ? 1 : item.Quantity,
                UnitPrice = item.UnitPrice,
                Amount = Math.Round((item.Quantity <= 0 ? 1 : item.Quantity) * item.UnitPrice, 2, MidpointRounding.AwayFromZero),
                SortOrder = sortOrder++
            });
        }
    }

    private async Task ApplyTotalsAsync(ClientInvoice invoice, Client client)
    {
        invoice.SubTotal = invoice.LineItems.Sum(l => l.Amount);
        var tax = await _invoiceService.CalculateTaxAsync(client, invoice.SubTotal);
        invoice.TaxRate = tax.Rate;
        invoice.TaxAmount = tax.Amount;
        invoice.TaxRegion = tax.Region;
        invoice.Total = invoice.SubTotal + invoice.TaxAmount;
    }

    private IQueryable<ClientInvoice> BuildFilteredQuery(string documentType, string status, int? clientId, string? search)
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
