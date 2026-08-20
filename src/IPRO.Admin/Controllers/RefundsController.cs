using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Admin.Controllers;

// DOCS/22: the manual-refund queue. No code here moves money -- the owner refunds at PayPal's
// portal against the transaction id this page shows, then flips the row's status. Everything is
// precomputed at cancel time (net / HST / gross, refund window) so nobody hand-calculates tax on
// a refund. SuperAdmin-only: this is a money workflow.
[Authorize(Policy = "SuperAdmin")]
public class RefundsController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IAdminAuditLogService _auditLog;
    private int CurrentAdminId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    private string CurrentAdminUsername => User.Identity?.Name ?? "unknown";

    public RefundsController(IPRODbContext db, IAdminAuditLogService auditLog)
    {
        _db = db;
        _auditLog = auditLog;
    }

    public async Task<IActionResult> Index()
    {
        var rows = await _db.SubscriptionChanges.AsNoTracking()
            .Include(c => c.AgentUser)
            .Include(c => c.CurrentBillingRule)
            .Where(c => c.ChangeType == SubscriptionChangeType.Cancel && c.RefundStatus != RefundStatus.None)
            .OrderBy(c => c.RefundStatus == RefundStatus.Pending ? 0 : 1)
            .ThenByDescending(c => c.CreatedAt)
            .Take(200)
            .ToListAsync();
        return View(rows);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRefunded(int id, string refundTransactionId)
    {
        var row = await LoadPendingAsync(id);
        if (row == null) return NotFound();
        if (string.IsNullOrWhiteSpace(refundTransactionId))
        {
            TempData["Error"] = "Enter the PayPal refund transaction id so the refund stays reconcilable.";
            return RedirectToAction(nameof(Index));
        }

        row.RefundStatus = RefundStatus.Refunded;
        row.RefundResolvedAt = DateTime.UtcNow;
        row.RefundResolutionNote = $"{row.RefundResolutionNote} | Refunded at PayPal, refund txn {refundTransactionId.Trim()}.";
        await _db.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "RefundMarkedRefunded",
            $"Cancel change #{row.Id} agent #{row.AgentUserId}: {row.RefundGrossAmount:0.00} {row.Currency} gross, refund txn {refundTransactionId.Trim()}");
        TempData["Success"] = $"Marked refunded: ${row.RefundGrossAmount:N2} {row.Currency} for agent #{row.AgentUserId}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkWaived(int id, string reason)
    {
        var row = await LoadPendingAsync(id);
        if (row == null) return NotFound();
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["Error"] = "A waived refund needs a reason on the record.";
            return RedirectToAction(nameof(Index));
        }

        row.RefundStatus = RefundStatus.Waived;
        row.RefundResolvedAt = DateTime.UtcNow;
        row.RefundResolutionNote = $"{row.RefundResolutionNote} | Waived: {reason.Trim()}";
        await _db.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "RefundWaived",
            $"Cancel change #{row.Id} agent #{row.AgentUserId}: {row.RefundGrossAmount:0.00} {row.Currency} waived: {reason.Trim()}");
        TempData["Success"] = "Refund marked waived.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<SubscriptionChange?> LoadPendingAsync(int id) =>
        await _db.SubscriptionChanges.FirstOrDefaultAsync(c =>
            c.Id == id && c.ChangeType == SubscriptionChangeType.Cancel && c.RefundStatus == RefundStatus.Pending);
}
