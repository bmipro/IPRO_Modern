using System.Security.Claims;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[AllowAnonymous]
[Route("invoice")]
public class ClientDocumentController : Controller
{
    private readonly IPRODbContext _db;

    public ClientDocumentController(IPRODbContext db) => _db = db;

    [HttpGet("{token}")]
    public async Task<IActionResult> Show(string token)
    {
        var invoice = await LoadAsync(token);
        if (invoice == null) return NotFound();

        // 452: the client opening the document is the strongest "they received it" signal there
        // is, and it needs nothing from the mail provider. The agent opening their own public link
        // from the portal is a preview, not a view.
        if (!IsOwningAgent(invoice))
        {
            var now = DateTime.UtcNow;
            invoice.FirstViewedAt ??= now;
            invoice.LastViewedAt = now;
            invoice.ViewCount++;
            await _db.SaveChangesAsync();
        }

        ViewBag.Agent = invoice.AgentUser;
        ViewBag.IsPublicView = true;
        ViewBag.PaymentLink = PayPalMeLinkHelper.WithAmount(invoice.AgentUser.DefaultPaymentLink, invoice.Total, invoice.Currency);
        return View(invoice);
    }

    [HttpPost("{token}/approve")]
    public async Task<IActionResult> Approve(string token)
    {
        var invoice = await LoadAsync(token);
        if (invoice == null) return NotFound();

        if (invoice.DocumentType == ClientInvoiceDocumentType.Estimate && invoice.Status == ClientInvoiceStatus.Sent)
        {
            invoice.Status = ClientInvoiceStatus.Approved;
            invoice.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Show), new { token });
    }

    [HttpPost("{token}/decline")]
    public async Task<IActionResult> Decline(string token)
    {
        var invoice = await LoadAsync(token);
        if (invoice == null) return NotFound();

        if (invoice.DocumentType == ClientInvoiceDocumentType.Estimate && invoice.Status == ClientInvoiceStatus.Sent)
        {
            invoice.Status = ClientInvoiceStatus.Declined;
            invoice.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Show), new { token });
    }

    private bool IsOwningAgent(ClientInvoice invoice) =>
        User?.Identity?.IsAuthenticated == true
        && int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var agentId)
        && agentId == invoice.AgentUserId;

    private Task<ClientInvoice?> LoadAsync(string token) =>
        _db.ClientInvoices
            .Include(i => i.LineItems)
            .Include(i => i.Client)
            .Include(i => i.AgentUser)
            .FirstOrDefaultAsync(i => i.ViewToken == token);
}
