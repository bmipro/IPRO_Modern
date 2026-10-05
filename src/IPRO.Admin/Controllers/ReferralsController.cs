using System.Globalization;
using System.Security.Claims;
using System.Text;
using IPRO.Admin.Models;
using IPRO.Billing;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Admin.Controllers;

// 532 (2026-10-05): SuperAdmin -> Referrals, on the owner's design of 2026-09-29: program-level settings only (on/off
// and the two amounts; a change applies to new referrals, a promised $50 stays $50), month and all-time totals, the
// top referrers, a "Needs attention" list for the exceptions only, search, stage filters, paging, CSV, Void with a
// reason (audit-logged) and a per-adviser pause. Codes are never rows in Promotion Codes. Money moves only at
// PayPal, by the owner: an earned reward is refunded from SuperAdmin -> Refunds.
[Authorize(Policy = "SuperAdmin")]
public class ReferralsController : Controller
{
    private const int PageSize = 50;
    private readonly IPRODbContext _db;
    private readonly IAdminAuditLogService _auditLog;

    public ReferralsController(IPRODbContext db, IAdminAuditLogService auditLog)
    {
        _db = db;
        _auditLog = auditLog;
    }

    private int CurrentAdminId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    private string CurrentAdminUsername => User.Identity?.Name ?? "unknown";

    public async Task<IActionResult> Index(string? q = null, string? stage = null, int? referrer = null, int page = 1)
    {
        var query = Filtered(q, stage, referrer);
        var total = await query.CountAsync();
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);
        var rows = await query.OrderByDescending(r => r.SignedUpAt).Skip((page - 1) * PageSize).Take(PageSize).ToListAsync();

        var all = await _db.Referrals.AsNoTracking().ToListAsync();
        var needs = await ReferralProgram.NeedingAttention(_db).AsNoTracking().OrderBy(r => r.SignedUpAt).Take(50).ToListAsync();
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var top = all.Where(r => r.Stage != ReferralStages.Voided)
            .GroupBy(r => r.AgentUserId)
            .Select(g => new ReferralsIndexModel.TopReferrer(g.Key, g.Count(),
                g.Count(r => r.Stage is ReferralStages.Joined or ReferralStages.Earned or ReferralStages.Paid),
                g.Where(r => r.Stage is ReferralStages.Earned or ReferralStages.Paid).Sum(r => r.RewardAmount)))
            .OrderByDescending(t => t.Joined).ThenByDescending(t => t.Referred)
            .Take(5)
            .ToList();

        var adviserIds = rows.SelectMany(r => new[] { r.AgentUserId, r.FriendAgentUserId })
            .Concat(needs.Select(r => r.AgentUserId)).Concat(top.Select(t => t.AgentUserId))
            .Append(referrer ?? 0).Distinct().ToList();
        var advisers = await _db.AgentUsers.AsNoTracking().Where(a => adviserIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id);

        return View(new ReferralsIndexModel
        {
            Settings = await ReferralProgram.LoadSettingsAsync(_db),
            Query = q?.Trim() ?? string.Empty,
            Stage = stage ?? string.Empty,
            ReferrerId = referrer,
            Page = page,
            PageCount = pageCount,
            TotalMatching = total,
            Rows = rows,
            Advisers = advisers,
            NeedsAttention = needs,
            TopReferrers = top,
            Month = Totals(all, monthStart),
            AllTime = Totals(all, null),
            ReferrerCode = referrer.HasValue ? await _db.ReferralCodes.AsNoTracking().FirstOrDefaultAsync(c => c.AgentUserId == referrer.Value) : null,
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings(bool enabled, decimal friendGiftAmount, decimal referrerRewardAmount)
    {
        if (friendGiftAmount <= 0m || friendGiftAmount > ReferralProgram.MaxAmount || referrerRewardAmount <= 0m || referrerRewardAmount > ReferralProgram.MaxAmount)
        {
            TempData["Error"] = $"Both amounts must be more than $0 and at most {ReferralProgram.Money(ReferralProgram.MaxAmount)}.";
            return RedirectToAction(nameof(Index));
        }
        var before = await ReferralProgram.LoadSettingsAsync(_db);
        await ReferralProgram.SaveSettingsAsync(_db, enabled, Math.Round(friendGiftAmount, 2), Math.Round(referrerRewardAmount, 2), DateTime.UtcNow);
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "ReferralProgramSettings",
            $"Refer a Friend: {Describe(before.Enabled, before.FriendGiftAmount, before.ReferrerRewardAmount)} -> {Describe(enabled, friendGiftAmount, referrerRewardAmount)}");
        TempData["Success"] = enabled
            ? $"Refer a Friend is ON: Give {ReferralProgram.Money(friendGiftAmount)}, Get {ReferralProgram.Money(referrerRewardAmount)}. A change applies to new referrals; promised amounts stay."
            : "Refer a Friend is OFF. New sign-ups get no gift; referrals already made keep going.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Void(int id, string? reason, string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["Error"] = "A voided referral needs a reason on the record.";
            return Back(returnUrl);
        }
        var voided = await ReferralProgram.VoidAsync(_db, id, reason, CurrentAdminUsername, DateTime.UtcNow);
        if (voided == null)
        {
            TempData["Error"] = "That referral cannot be voided (it is paid or already voided).";
            return Back(returnUrl);
        }
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "ReferralVoided",
            $"Referral #{voided.Id} (adviser #{voided.AgentUserId} referred {voided.FriendName}, adviser #{voided.FriendAgentUserId}) voided: {reason.Trim()}");
        TempData["Success"] = $"Referral of {voided.FriendName} voided.";
        return Back(returnUrl);
    }

    // A possible self-referral looked at and let stand.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> LooksFine(int id, string? returnUrl = null)
    {
        var referral = await _db.Referrals.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        if (referral != null && await ReferralProgram.ClearAttentionAsync(_db, id, DateTime.UtcNow))
        {
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "ReferralAttentionCleared",
                $"Referral #{id} ({referral.FriendName}): let stand after \"{referral.Attention}\"");
            TempData["Success"] = $"The referral of {referral.FriendName} stands.";
        }
        return Back(returnUrl);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Pause(int agentUserId, bool paused)
    {
        if (!await ReferralProgram.SetPausedAsync(_db, agentUserId, paused, DateTime.UtcNow))
        {
            TempData["Error"] = "That adviser has no Refer a Friend code yet (one is made the first time they open the page).";
            return RedirectToAction(nameof(Index), new { referrer = agentUserId });
        }
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, paused ? "ReferralCodePaused" : "ReferralCodeResumed",
            $"Adviser #{agentUserId}'s Refer a Friend code {(paused ? "paused" : "resumed")}");
        TempData["Success"] = paused ? "Their referral link is paused: new friends sign up without the gift." : "Their referral link works again.";
        return RedirectToAction(nameof(Index), new { referrer = agentUserId });
    }

    public async Task<IActionResult> Csv(string? q = null, string? stage = null, int? referrer = null)
    {
        var rows = await Filtered(q, stage, referrer).OrderByDescending(r => r.SignedUpAt).ToListAsync();
        var ids = rows.Select(r => r.AgentUserId).Distinct().ToList();
        var advisers = await _db.AgentUsers.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id);

        var sb = new StringBuilder();
        sb.AppendLine("Id,SignedUpUtc,ReferrerId,Referrer,ReferrerEmail,Code,FriendId,Friend,FriendBusiness,FriendEmail,Stage,GiftPromised,GiftOffSetup,GiftOffCycle1,GiftOffCycle2,JoinedUtc,ExpectedEarnUtc,EarnedUtc,RewardNet,RewardTax,RewardGross,TaxRegion,RefundPlan,PaidUtc,PayPalRefund,CreditNote,ClosedUtc,ClosedReason,VoidedBy,Attention");
        foreach (var r in rows)
        {
            advisers.TryGetValue(r.AgentUserId, out var a);
            sb.AppendLine(string.Join(",",
                r.Id.ToString(CultureInfo.InvariantCulture), Date(r.SignedUpAt), r.AgentUserId.ToString(CultureInfo.InvariantCulture),
                Cell(a == null ? "(deleted)" : $"{a.FirstName} {a.LastName}".Trim()), Cell(a?.Email), Cell(r.Code),
                r.FriendAgentUserId.ToString(CultureInfo.InvariantCulture), Cell(r.FriendName), Cell(r.FriendBusiness), Cell(r.FriendEmail),
                Cell(ReferralStages.Label(r.Stage)), Amount(r.GiftAmount), Amount(r.GiftSetupDiscount), Amount(r.GiftCycle1Discount), Amount(r.GiftCycle2Discount),
                Date(r.JoinedAt), Date(r.ExpectedEarnAt), Date(r.EarnedAt), Amount(r.RewardNet), Amount(r.RewardTax), Amount(r.RewardGross),
                Cell(r.RewardTaxRegion), Cell(r.RefundPlan), Date(r.PaidAt), Cell(r.RefundTransactionId), Cell(r.CreditNoteNumber),
                Date(r.ClosedAt), Cell(r.ClosedReason), Cell(r.VoidedBy), Cell(r.Attention)));
        }
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"ipro-referrals-{DateTime.UtcNow:yyyyMMdd}.csv");

        static string Date(DateTime? value) => value?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;
        static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    // A friend's name and business are typed by the public: a leading = + - @ would run as a formula in a
    // spreadsheet, so it is shown as text.
    internal static string Cell(string? value)
    {
        value ??= string.Empty;
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    private IQueryable<Referral> Filtered(string? q, string? stage, int? referrer)
    {
        var query = _db.Referrals.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(stage) && ReferralStages.All.Contains(stage)) query = query.Where(r => r.Stage == stage);
        if (referrer.HasValue) query = query.Where(r => r.AgentUserId == referrer.Value);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            var referrerIds = _db.AgentUsers.Where(a => (a.FirstName + " " + a.LastName).Contains(needle) || a.Email.Contains(needle) || a.CompanyName.Contains(needle)).Select(a => a.Id);
            query = query.Where(r => r.FriendName.Contains(needle) || r.FriendEmail.Contains(needle) || r.FriendBusiness.Contains(needle) ||
                                     r.Code.Contains(needle) || referrerIds.Contains(r.AgentUserId));
        }
        return query;
    }

    // Each figure by its own date: signed up, joined (and the gifts given then), earned, paid.
    private static ReferralsIndexModel.Totals Totals(IReadOnlyList<Referral> all, DateTime? since)
    {
        bool In(DateTime? at) => since == null || (at.HasValue && at.Value >= since.Value);
        bool HasJoined(Referral r) => r.Stage is ReferralStages.Joined or ReferralStages.Earned or ReferralStages.Paid;
        bool HasEarned(Referral r) => r.Stage is ReferralStages.Earned or ReferralStages.Paid;
        return new ReferralsIndexModel.Totals
        {
            SignedUp = all.Count(r => r.Stage != ReferralStages.Voided && In(r.SignedUpAt)),
            Joined = all.Count(r => HasJoined(r) && In(r.JoinedAt)),
            GiftsGiven = all.Where(r => HasJoined(r) && In(r.JoinedAt)).Sum(r => r.GiftSetupDiscount + r.GiftCycle1Discount + r.GiftCycle2Discount),
            RewardsEarned = all.Where(r => HasEarned(r) && In(r.EarnedAt)).Sum(r => r.RewardAmount),
            RewardsPaid = all.Where(r => r.Stage == ReferralStages.Paid && In(r.PaidAt)).Sum(r => r.RewardGross),
        };
    }

    private static string Describe(bool enabled, decimal gift, decimal reward) =>
        $"{(enabled ? "ON" : "OFF")}, give {gift:0.00}, get {reward:0.00}";

    private IActionResult Back(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Index));
}
