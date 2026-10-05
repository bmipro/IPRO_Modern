using System.Security.Claims;
using IPRO.Billing;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Email;
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
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RefundsController> _logger;
    private int CurrentAdminId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    private string CurrentAdminUsername => User.Identity?.Name ?? "unknown";

    public RefundsController(IPRODbContext db, IAdminAuditLogService auditLog, IEmailService email, IConfiguration configuration, ILogger<RefundsController> logger)
    {
        _db = db;
        _auditLog = auditLog;
        _email = email;
        _configuration = configuration;
        _logger = logger;
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

        // 532: Refer a Friend rewards -- earned ones waiting to be refunded (with where to refund them, worked out
        // against the adviser's own payments), and the latest ones paid.
        var rewards = await _db.Referrals.AsNoTracking()
            .Where(r => r.Stage == ReferralStages.Earned)
            .OrderBy(r => r.EarnedAt)
            .ToListAsync();
        var paidRewards = await _db.Referrals.AsNoTracking()
            .Where(r => r.Stage == ReferralStages.Paid)
            .OrderByDescending(r => r.PaidAt)
            .Take(20)
            .ToListAsync();
        var adviserIds = rewards.Concat(paidRewards).Select(r => r.AgentUserId).Distinct().ToList();
        ViewBag.ReferralRewards = rewards;
        ViewBag.ReferralRewardsPaid = paidRewards;
        ViewBag.ReferralAdvisers = await _db.AgentUsers.AsNoTracking().Where(a => adviserIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id);
        return View(rows);
    }

    // 532: the owner refunded a Refer a Friend reward at PayPal. The referral is paid, its credit note numbered, and
    // the referrer gets the "paid" email -- sent when the row is marked, as the owner decided.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkReferralRefunded(int id, string refundTransactionId)
    {
        if (string.IsNullOrWhiteSpace(refundTransactionId))
        {
            TempData["Error"] = "Enter the PayPal refund transaction id(s) so the reward stays reconcilable.";
            return RedirectToAction(nameof(Index));
        }
        var now = DateTime.UtcNow;
        var paid = await ReferralProgram.MarkPaidAsync(_db, id, refundTransactionId, now);
        if (paid == null)
        {
            TempData["Error"] = "That reward is not waiting to be refunded.";
            return RedirectToAction(nameof(Index));
        }
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "ReferralRewardRefunded",
            $"Referral #{paid.Id}: adviser #{paid.AgentUserId} for referring {paid.FriendName}: {paid.RewardGross:0.00} CAD gross ({paid.RefundPlan}), refund txn {paid.RefundTransactionId}, credit note {paid.CreditNoteNumber}");

        var referrer = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == paid.AgentUserId);
        var emailed = false;
        if (referrer != null && !string.IsNullOrWhiteSpace(referrer.Email))
        {
            var mail = ReferralEmails.Paid(paid, referrer, _configuration);
            try
            {
                emailed = await _email.SendAsync(referrer.Email, $"{referrer.FirstName} {referrer.LastName}".Trim(), mail.Subject, mail.Html, mail.Text);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Refer a Friend: the paid email for referral {ReferralId} failed.", paid.Id);
            }
            if (emailed)
            {
                await _db.Referrals.Where(r => r.Id == paid.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.PaidEmailSentAt, now));
            }
        }
        TempData["Success"] = emailed
            ? $"Marked refunded: {ReferralProgram.Money(paid.RewardGross)} for referring {paid.FriendName}, credit note {paid.CreditNoteNumber}. The adviser was emailed."
            : $"Marked refunded: {ReferralProgram.Money(paid.RewardGross)} for referring {paid.FriendName}, credit note {paid.CreditNoteNumber}. The adviser's email did NOT go out -- tell them by phone or a support ticket.";
        return RedirectToAction(nameof(Index));
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
