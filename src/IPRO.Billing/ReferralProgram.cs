using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Billing;

// The PayPal payer behind a subscription, for the one automatic block the owner asked for: a friend who pays iPro
// from the referrer's own PayPal account earns nothing. Reached is false when PayPal could not be asked (the job
// tries again next hour); an empty PayerId with Reached true means there is nothing to compare.
public interface IReferralPayerLookup
{
    Task<(bool Reached, string PayerId)> PayerAsync(string subscriptionId);
}

// 532 (2026-10-05): Refer a Friend -- Give $50, Get $50, on the owner's design of 2026-09-29 (DOCS/TODO.md 532).
// Everything the program decides lives here: the codes, the friend's gift at checkout, the ledger's stages, and
// where an earned reward is refunded. No code here moves money: an earned reward waits in SuperAdmin -> Refunds
// with its refund already worked out, and the owner refunds at PayPal (DOCS/22).
public static class ReferralProgram
{
    public const string SignupBaseUrlSetting = "Referrals:SignupBaseUrl";
    public const string DefaultSignupBaseUrl = "https://www.iproadvisers.com";
    // An earned reward is planned only against payments with at least this many days left in PayPal's refund
    // window, so the owner has time to act on it.
    public const int RefundWindowMarginDays = 14;
    public const decimal MaxAmount = 500m;

    // ---- the program's settings --------------------------------------------------------------------------

    public static async Task<ReferralProgramSettings> LoadSettingsAsync(IPRODbContext db) =>
        await db.ReferralProgramSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1) ?? new ReferralProgramSettings();

    public static async Task SaveSettingsAsync(IPRODbContext db, bool enabled, decimal friendGift, decimal referrerReward, DateTime now)
    {
        var row = await db.ReferralProgramSettings.FirstOrDefaultAsync(s => s.Id == 1);
        if (row == null)
        {
            row = new ReferralProgramSettings { Id = 1 };
            db.ReferralProgramSettings.Add(row);
        }
        row.Enabled = enabled;
        row.FriendGiftAmount = friendGift;
        row.ReferrerRewardAmount = referrerReward;
        row.UpdatedAt = now;
        await db.SaveChangesAsync();
    }

    // ---- codes ---------------------------------------------------------------------------------------------

    private const string CodeAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    public static string Normalize(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    // The adviser's first name in capitals (accents dropped, at most eight letters) and four characters nobody
    // misreads: JANE-7K3Q.
    internal static string MakeCode(string? firstName)
    {
        var name = new StringBuilder();
        foreach (var c in (firstName ?? string.Empty).Normalize(NormalizationForm.FormD))
        {
            var upper = char.ToUpperInvariant(c);
            if (upper is >= 'A' and <= 'Z') name.Append(upper);
            if (name.Length == 8) break;
        }
        if (name.Length < 2) name.Clear().Append("IPRO");
        var tail = new char[4];
        for (var i = 0; i < tail.Length; i++) tail[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return $"{name}-{new string(tail)}";
    }

    // One permanent code per adviser, made the first time it is needed; never equal to a promotion code.
    public static async Task<ReferralCode> GetOrCreateCodeAsync(IPRODbContext db, int agentUserId, string? firstName, DateTime now)
    {
        var existing = await db.ReferralCodes.FirstOrDefaultAsync(c => c.AgentUserId == agentUserId);
        if (existing != null) return existing;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var code = MakeCode(firstName);
            if (await db.ReferralCodes.AnyAsync(c => c.Code == code) || await db.PromotionCodes.AnyAsync(p => p.Code == code)) continue;
            var row = new ReferralCode { AgentUserId = agentUserId, Code = code, CreatedAt = now };
            db.ReferralCodes.Add(row);
            try
            {
                await db.SaveChangesAsync();
                return row;
            }
            catch (DbUpdateException)
            {
                db.Entry(row).State = EntityState.Detached;
                var raced = await db.ReferralCodes.FirstOrDefaultAsync(c => c.AgentUserId == agentUserId);
                if (raced != null) return raced;
            }
        }
        throw new InvalidOperationException($"Could not make a unique referral code for adviser {agentUserId}.");
    }

    public static string Link(string code, IConfiguration configuration)
    {
        var baseUrl = configuration[SignupBaseUrlSetting];
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = DefaultSignupBaseUrl;
        return $"{baseUrl.Trim().TrimEnd('/')}/Account/Register?ref={Uri.EscapeDataString(code)}";
    }

    public sealed record UsableCode(string Code, AgentUser Referrer, decimal GiftAmount, decimal RewardAmount)
    {
        public string ReferrerName => $"{Referrer.FirstName} {Referrer.LastName}".Trim();
        public string ReferrerBusiness => Referrer.CompanyName?.Trim() ?? string.Empty;
    }

    // A code a friend can still use: the program is on, the code is not paused, and its adviser's account is open.
    // Anything else -- the friend still signs up, without the gift.
    public static async Task<UsableCode?> FindUsableAsync(IPRODbContext db, string? code)
    {
        var normalized = Normalize(code);
        if (normalized.Length == 0 || normalized.Length > 20) return null;
        var settings = await LoadSettingsAsync(db);
        if (!settings.Enabled) return null;
        var row = await db.ReferralCodes.AsNoTracking().FirstOrDefaultAsync(c => c.Code == normalized);
        if (row == null || row.IsPaused) return null;
        var referrer = await db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == row.AgentUserId);
        if (referrer == null || !referrer.IsActive) return null;
        return new UsableCode(row.Code, referrer, settings.FriendGiftAmount, settings.ReferrerRewardAmount);
    }

    public static async Task<bool> SetPausedAsync(IPRODbContext db, int agentUserId, bool paused, DateTime now)
    {
        var row = await db.ReferralCodes.FirstOrDefaultAsync(c => c.AgentUserId == agentUserId);
        if (row == null) return false;
        row.IsPaused = paused;
        row.PausedAt = paused ? now : null;
        await db.SaveChangesAsync();
        return true;
    }

    // ---- sign-up -------------------------------------------------------------------------------------------

    // Called once the friend's account exists. The code is the one in the sign-up's promotion-code field (one code
    // per sign-up): a promotion code typed in its place is simply not a referral. Amounts are copied from the
    // program now -- a later change applies to new referrals only.
    public static async Task<Referral?> RecordSignupAsync(IPRODbContext db, AgentUser friend, DateTime now)
    {
        var usable = await FindUsableAsync(db, friend.PromotionCode);
        if (usable == null || usable.Referrer.Id == friend.Id) return null;
        if (await db.Referrals.AnyAsync(r => r.FriendAgentUserId == friend.Id)) return null;

        var referral = new Referral
        {
            AgentUserId = usable.Referrer.Id,
            FriendAgentUserId = friend.Id,
            Code = usable.Code,
            FriendName = $"{friend.FirstName} {friend.LastName}".Trim(),
            FriendBusiness = friend.CompanyName ?? string.Empty,
            FriendEmail = friend.Email ?? string.Empty,
            GiftAmount = usable.GiftAmount,
            RewardAmount = usable.RewardAmount,
            Stage = ReferralStages.SignedUp,
            SignedUpAt = now,
            Attention = SelfReferralSignal(usable.Referrer, friend),
            UpdatedAt = now,
        };
        db.Referrals.Add(referral);
        await db.SaveChangesAsync();
        return referral;
    }

    // "A possible self-referral" (the owner's Needs attention list): the friend shares a phone number, the sign-up
    // network address or the business address with the referrer. A flag for a person to look at, never a block --
    // colleagues share offices. The block is the same PayPal payer (AdvanceAsync).
    internal static string SelfReferralSignal(AgentUser referrer, AgentUser friend)
    {
        var reasons = new List<string>();
        var referrerPhones = new[] { Digits(referrer.Phone), Digits(referrer.CellPhone) }.Where(p => p.Length >= 7).ToHashSet();
        if (new[] { Digits(friend.Phone), Digits(friend.CellPhone) }.Any(referrerPhones.Contains)) reasons.Add("the same phone number");
        if (!string.IsNullOrWhiteSpace(referrer.RegistrationIpAddress) && referrer.RegistrationIpAddress == friend.RegistrationIpAddress)
            reasons.Add("the same network address at sign-up");
        if (!string.IsNullOrWhiteSpace(referrer.CompanyAddress) && Same(referrer.CompanyAddress, friend.CompanyAddress) && Same(referrer.City, friend.City))
            reasons.Add("the same business address");
        return reasons.Count == 0 ? string.Empty : $"Possible self-referral: {string.Join(", ", reasons)} as the referrer.";

        static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
        static bool Same(string? a, string? b) => string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- the friend's gift ---------------------------------------------------------------------------------

    public sealed record GiftSplit(decimal SetupDiscount, decimal Cycle1Discount, decimal Cycle2Discount)
    {
        public decimal Total => SetupDiscount + Cycle1Discount + Cycle2Discount;
    }

    // The owner's rule: the gift comes off the setup fee as it stands after any waiver; what the setup fee cannot
    // absorb comes off the first billing period, then the second (PayPal plans carry at most two discounted cycles).
    public static GiftSplit Split(decimal gift, decimal setupFee, decimal cycleAmount)
    {
        var left = Math.Max(0m, gift);
        var setup = Math.Min(left, Math.Max(0m, setupFee));
        left -= setup;
        var first = Math.Min(left, Math.Max(0m, cycleAmount));
        left -= first;
        var second = Math.Min(left, Math.Max(0m, cycleAmount));
        return new GiftSplit(setup, first, second);
    }

    public sealed record CheckoutGift(Referral Referral, string ReferrerName, GiftSplit Split);

    // The gift for this checkout: the friend's referral, still waiting for their first subscription. A promised
    // gift is kept even if the program has been switched off since; a voided referral gives nothing.
    public static async Task<CheckoutGift?> ForCheckoutAsync(IPRODbContext db, int friendAgentUserId, decimal setupFee, decimal cycleAmount)
    {
        var referral = await db.Referrals.AsNoTracking()
            .FirstOrDefaultAsync(r => r.FriendAgentUserId == friendAgentUserId && r.Stage == ReferralStages.SignedUp);
        if (referral == null || referral.GiftAmount <= 0) return null;
        var split = Split(referral.GiftAmount, setupFee, cycleAmount);
        if (split.Total <= 0) return null;
        var referrer = await db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == referral.AgentUserId);
        var name = referrer == null ? "a friend" : $"{referrer.FirstName} {referrer.LastName}".Trim();
        return new CheckoutGift(referral, name, split);
    }

    public static async Task RecordCheckoutAsync(IPRODbContext db, int referralId, int billingId, GiftSplit split, DateTime now) =>
        await db.Referrals.Where(r => r.Id == referralId).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.GiftBillingId, billingId)
            .SetProperty(r => r.GiftSetupDiscount, split.SetupDiscount)
            .SetProperty(r => r.GiftCycle1Discount, split.Cycle1Discount)
            .SetProperty(r => r.GiftCycle2Discount, split.Cycle2Discount)
            .SetProperty(r => r.UpdatedAt, now));

    // The first invoice says what the gift did, line by line (the promotion codes' rule, 510).
    public static string? SetupLabel(string packageName, GiftSplit split, decimal setupFee, string referrerName) =>
        split.SetupDiscount <= 0 || setupFee <= 0
            ? null
            : split.SetupDiscount >= setupFee
                ? $"{packageName} one-time setup fee - waived, a referral gift from {referrerName}"
                : $"{packageName} one-time setup fee - {Money(split.SetupDiscount)} off, a referral gift from {referrerName}";

    public static string? RecurringLabel(string packageName, BillingPeriod period, GiftSplit split, decimal cycleAmount, string referrerName)
    {
        if (split.Cycle1Discount <= 0) return null;
        var unit = Unit(period);
        var first = split.Cycle1Discount >= cycleAmount ? $"first {unit} free" : $"{Money(split.Cycle1Discount)} off the first {unit}";
        var second = split.Cycle2Discount <= 0
            ? string.Empty
            : split.Cycle2Discount >= cycleAmount ? $" and the second {unit} free" : $" and {Money(split.Cycle2Discount)} off the second";
        return $"{packageName} {PeriodWord(period)} recurring subscription - {first}{second}, a referral gift from {referrerName}";
    }

    // What the friend is told when they apply the code: who it is from and exactly what it takes off this plan.
    public static string DescribeGift(UsableCode usable, string packageName, BillingPeriod period, decimal setupFee, decimal cycleAmount)
    {
        var split = Split(usable.GiftAmount, setupFee, cycleAmount);
        var unit = Unit(period);
        var parts = new List<string>();
        if (split.SetupDiscount > 0)
            parts.Add(split.SetupDiscount >= setupFee ? $"no setup fee (it was {Money(setupFee)})" : $"a setup fee of {Money(setupFee - split.SetupDiscount)} instead of {Money(setupFee)}");
        if (split.Cycle1Discount > 0)
            parts.Add(split.Cycle1Discount >= cycleAmount ? $"your first {unit} free" : $"your first {unit} at {Money(cycleAmount - split.Cycle1Discount)} instead of {Money(cycleAmount)}");
        if (split.Cycle2Discount > 0)
            parts.Add(split.Cycle2Discount >= cycleAmount ? $"your second {unit} free" : $"your second {unit} at {Money(cycleAmount - split.Cycle2Discount)}");
        var what = parts.Count == 0 ? "nothing to take off on this plan" : string.Join(", and ", parts);
        return $"{GiftFrom(usable)}: {what} on {packageName}, before tax.";
    }

    public static string GiftFrom(UsableCode usable) =>
        string.IsNullOrWhiteSpace(usable.ReferrerBusiness) || string.Equals(usable.ReferrerBusiness, usable.ReferrerName, StringComparison.OrdinalIgnoreCase)
            ? $"A {Money(usable.GiftAmount)} gift from {usable.ReferrerName}"
            : $"A {Money(usable.GiftAmount)} gift from {usable.ReferrerName} of {usable.ReferrerBusiness}";

    // ---- the ledger, hour by hour (ReferralJob) --------------------------------------------------------------

    public sealed record AdvanceReport(int Joined, int Blocked, int JoinedEmails, int Earned, int NotEarned, int Planned, int Waiting);

    public static async Task<AdvanceReport> AdvanceAsync(IPRODbContext db, IEmailService email, IConfiguration configuration, ILogger logger, IReferralPayerLookup payers, DateTime now)
    {
        int joined = 0, blocked = 0, joinedEmails = 0, earned = 0, notEarned = 0, planned = 0, waiting = 0;

        // 1. Signed up -> joined. Activation marks it the moment the subscription starts (OnSubscriptionStartedAsync);
        // this is the backstop for any start that did not come through there. A friend whose account is gone
        // before subscribing has nothing left to earn.
        foreach (var r in await db.Referrals.Where(r => r.Stage == ReferralStages.SignedUp).ToListAsync())
        {
            if (!await db.AgentUsers.AnyAsync(a => a.Id == r.FriendAgentUserId))
            {
                Close(r, ReferralStages.NotEarned, "The friend's account was closed before they subscribed.", string.Empty, now);
                notEarned++;
                continue;
            }
            var active = await db.Billings.AsNoTracking()
                .Where(b => b.AgentUserId == r.FriendAgentUserId && b.Status == BillingStatus.Active)
                .OrderBy(b => b.StartDate)
                .FirstOrDefaultAsync();
            if (active == null) continue;
            var firstBill = await db.Invoices.AsNoTracking().Where(i => i.BillingId == active.Id).OrderBy(i => i.IssuedAt).FirstOrDefaultAsync();
            MarkJoined(r, active, firstBill?.Total ?? 0m, now);
            joined++;
        }
        await db.SaveChangesAsync();

        // 2. Joined: the same-payer block first, then the "joined" email (once).
        foreach (var r in await db.Referrals.Where(r => r.Stage == ReferralStages.Joined && (r.PayerCheckedAt == null || r.JoinedEmailSentAt == null)).ToListAsync())
        {
            if (r.PayerCheckedAt == null)
            {
                var samePayer = await SamePayerAsync(db, payers, r);
                if (samePayer == null) continue;   // PayPal could not be asked; next hour
                if (samePayer == true)
                {
                    Close(r, ReferralStages.Voided, "Blocked: the friend pays iPro from the referrer's own PayPal account.", "automatic", now);
                    blocked++;
                    continue;
                }
                r.PayerCheckedAt = now;
            }
            if (r.JoinedEmailSentAt == null)
            {
                var referrer = await db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == r.AgentUserId);
                if (referrer == null || string.IsNullOrWhiteSpace(referrer.Email))
                {
                    r.JoinedEmailSentAt = now;   // nobody to tell
                }
                else if (await SendAsync(email, referrer, ReferralEmails.Joined(r, referrer, configuration), logger))
                {
                    r.JoinedEmailSentAt = now;
                    joinedEmails++;
                }
            }
            r.UpdatedAt = now;
        }
        await db.SaveChangesAsync();

        // 3. Joined -> earned (the second monthly payment; 30 days into an annual plan, still subscribed) or not.
        foreach (var r in await db.Referrals.Where(r => r.Stage == ReferralStages.Joined && r.PayerCheckedAt != null).ToListAsync())
        {
            var friendExists = await db.AgentUsers.AnyAsync(a => a.Id == r.FriendAgentUserId);
            var billings = await db.Billings.AsNoTracking().Where(b => b.AgentUserId == r.FriendAgentUserId).ToListAsync();
            var paid = await db.Invoices.AsNoTracking()
                .Where(i => i.AgentUserId == r.FriendAgentUserId && i.IsPaid && i.Total > 0m && i.IssuedAt >= r.SignedUpAt)
                .OrderBy(i => i.IssuedAt)
                .ToListAsync();
            var subscribed = billings.Any(b => b.Status == BillingStatus.Active);
            var isEarned = r.FriendPeriod == BillingPeriod.Annually
                ? paid.Count >= 1 && subscribed && now >= paid[0].IssuedAt.AddDays(30)
                : paid.Count >= 2;
            if (isEarned)
            {
                r.Stage = ReferralStages.Earned;
                r.EarnedAt = now;
                earned++;
            }
            else if (!friendExists)
            {
                Close(r, ReferralStages.NotEarned, "The friend's account was closed before the reward was earned.", string.Empty, now);
                notEarned++;
            }
            else if (!subscribed && billings.OrderByDescending(b => b.CreatedAt).FirstOrDefault()?.Status is BillingStatus.Cancelled or BillingStatus.Expired)
            {
                Close(r, ReferralStages.NotEarned, "The friend's subscription ended before the reward was earned.", string.Empty, now);
                notEarned++;
            }
            r.UpdatedAt = now;
        }
        await db.SaveChangesAsync();

        // 4. Earned -> its refund worked out against the referrer's own payments, or waiting for one.
        foreach (var r in await db.Referrals.Where(r => r.Stage == ReferralStages.Earned && r.RefundPlan == "").OrderBy(r => r.EarnedAt).ToListAsync())
        {
            var plan = await PlanRefundAsync(db, r, now);
            if (plan == null)
            {
                waiting++;
            }
            else
            {
                r.RewardNet = plan.Net;
                r.RewardTax = plan.Tax;
                r.RewardGross = plan.Gross;
                r.RewardTaxRate = plan.TaxRate;
                r.RewardTaxRegion = plan.TaxRegion;
                r.RefundPlan = plan.Plan;
                r.RefundWindowEndsAt = plan.WindowEndsAt;
                planned++;
            }
            r.UpdatedAt = now;
            await db.SaveChangesAsync();   // one at a time: the next row's plan must see this one's
        }

        return new AdvanceReport(joined, blocked, joinedEmails, earned, notEarned, planned, waiting);
    }

    // A friend's subscription started (ActivateSubscriptionBillingAsync): the referral is joined at once, so the gift
    // can never be priced into a second checkout -- the hourly job would leave an hour's window otherwise.
    public static async Task<bool> OnSubscriptionStartedAsync(IPRODbContext db, int friendAgentUserId, IPRO.Entities.Billing billing, decimal firstBillTotal, DateTime now)
    {
        var referral = await db.Referrals.FirstOrDefaultAsync(r => r.FriendAgentUserId == friendAgentUserId && r.Stage == ReferralStages.SignedUp);
        if (referral == null) return false;
        MarkJoined(referral, billing, firstBillTotal, now);
        await db.SaveChangesAsync();
        return true;
    }

    private static void MarkJoined(Referral r, IPRO.Entities.Billing billing, decimal firstBillTotal, DateTime now)
    {
        var start = billing.StartDate == default ? now : billing.StartDate;
        r.Stage = ReferralStages.Joined;
        r.JoinedAt = start;
        r.FriendPeriod = billing.Period;
        r.ExpectedEarnAt = ExpectedEarnAt(start, billing.Period, firstBillTotal > 0m);
        r.UpdatedAt = now;
    }

    internal static DateTime ExpectedEarnAt(DateTime start, BillingPeriod period, bool chargedAtStart) =>
        period == BillingPeriod.Annually
            ? (chargedAtStart ? start.AddDays(30) : start.AddYears(1).AddDays(30))
            : start.AddMonths(chargedAtStart ? 1 : 2);

    // true: the same PayPal payer; false: different, or nothing to compare; null: PayPal could not be asked.
    private static async Task<bool?> SamePayerAsync(IPRODbContext db, IReferralPayerLookup payers, Referral r)
    {
        var friendSubscription = await db.Billings.AsNoTracking()
            .Where(b => b.AgentUserId == r.FriendAgentUserId && b.Status == BillingStatus.Active && b.PayPalSubscriptionId != "")
            .OrderByDescending(b => b.StartDate).Select(b => b.PayPalSubscriptionId).FirstOrDefaultAsync();
        var referrerSubscription = await db.Billings.AsNoTracking()
            .Where(b => b.AgentUserId == r.AgentUserId && b.PayPalSubscriptionId != "")
            .OrderByDescending(b => b.CreatedAt).Select(b => b.PayPalSubscriptionId).FirstOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(friendSubscription) || string.IsNullOrWhiteSpace(referrerSubscription)) return false;

        var friendPayer = await payers.PayerAsync(friendSubscription);
        if (!friendPayer.Reached) return null;
        var referrerPayer = await payers.PayerAsync(referrerSubscription);
        if (!referrerPayer.Reached) return null;
        return friendPayer.PayerId.Length > 0 && string.Equals(friendPayer.PayerId, referrerPayer.PayerId, StringComparison.OrdinalIgnoreCase);
    }

    private static void Close(Referral r, string stage, string reason, string by, DateTime now)
    {
        r.Stage = stage;
        r.ClosedAt = now;
        r.ClosedReason = reason;
        r.VoidedBy = by;
        r.UpdatedAt = now;
    }

    // ---- where an earned reward is refunded -----------------------------------------------------------------

    public sealed record RefundPlanResult(decimal Net, decimal Tax, decimal Gross, decimal TaxRate, string TaxRegion, string Plan, DateTime WindowEndsAt);

    // The reward and its tax (at the rate the referrer was charged on their latest payment -- the tax on the $50 is
    // returned with it, documented by the credit note), refunded against the referrer's own payments, newest first.
    // PayPal will not refund more than a payment was, so a reward bigger than the latest payment is split across
    // the ones before it; only payments with RefundWindowMarginDays left in PayPal's window count, and money already
    // promised back from a payment (another reward, a cancellation refund) is taken off what it can still return.
    // Null: not enough yet -- the reward waits for the adviser's next payments (Needs attention).
    public static async Task<RefundPlanResult?> PlanRefundAsync(IPRODbContext db, Referral referral, DateTime now)
    {
        var since = now.AddDays(-(PrepaidValue.PayPalRefundWindowDays - RefundWindowMarginDays));
        var invoices = await db.Invoices.AsNoTracking().Include(i => i.LineItems)
            .Where(i => i.AgentUserId == referral.AgentUserId && i.IsPaid && i.Total > 0m && i.IssuedAt >= since)
            .OrderByDescending(i => i.IssuedAt)
            .ToListAsync();
        if (invoices.Count == 0) return null;

        var rate = invoices[0].TaxRate;
        var net = referral.RewardAmount;
        var tax = PayPalBillingService.RoundTax(net, rate);
        var gross = net + tax;
        var promised = await PromisedBackAsync(db, referral.Id);

        var parts = new List<(string Sale, decimal Amount, DateTime WindowEndsAt)>();
        var left = gross;
        foreach (var invoice in invoices)
        {
            foreach (var (sale, capacity) in Sales(invoice).AsEnumerable().Reverse())
            {
                var free = capacity - promised.GetValueOrDefault(sale);
                if (free <= 0m) continue;
                var take = Math.Min(free, left);
                parts.Add((sale, take, PrepaidValue.RefundWindowEndsAt(invoice.IssuedAt)));
                left -= take;
                if (left <= 0m) break;
            }
            if (left <= 0m) break;
        }
        if (left > 0m) return null;

        var plan = string.Join(";", parts.Select(p => $"{p.Sale}={p.Amount.ToString("0.00", CultureInfo.InvariantCulture)}"));
        return new RefundPlanResult(net, tax, gross, rate, invoices[0].TaxRegion, plan, parts.Min(p => p.WindowEndsAt));
    }

    // The PayPal sales an iPro invoice was paid with, and how much each was. A renewal is one sale; a sign-up is
    // PayPal's setup-fee sale and then its first-cycle sale, minutes apart (the order HandleSubscriptionPayment-
    // CompletedWebhookAsync records them in). Anything else is left out: a refund is never planned against an
    // amount this cannot know.
    internal static List<(string Sale, decimal Capacity)> Sales(Invoice invoice)
    {
        var ids = (invoice.PayPalTransactionId ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(id => !id.StartsWith("I-", StringComparison.Ordinal) && !id.StartsWith("PAYPAL_FAILED:", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 1) return new() { (ids[0], invoice.Total) };
        if (ids.Count == 2)
        {
            var setupNet = invoice.LineItems.Where(l => l.Description.Contains("setup fee", StringComparison.OrdinalIgnoreCase)).Sum(l => l.Amount);
            if (setupNet > 0m)
            {
                var setupGross = setupNet + PayPalBillingService.RoundTax(setupNet, invoice.TaxRate);
                var cycleGross = invoice.Total - setupGross;
                if (cycleGross > 0m) return new() { (ids[0], setupGross), (ids[1], cycleGross) };
            }
        }
        return new();
    }

    // Money already promised back from each sale: other rewards' plans, and cancellation refunds still to make or made.
    private static async Task<Dictionary<string, decimal>> PromisedBackAsync(IPRODbContext db, int exceptReferralId)
    {
        var promised = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var plans = await db.Referrals.AsNoTracking()
            .Where(r => r.Id != exceptReferralId && r.RefundPlan != "" && (r.Stage == ReferralStages.Earned || r.Stage == ReferralStages.Paid))
            .Select(r => r.RefundPlan)
            .ToListAsync();
        foreach (var plan in plans)
            foreach (var (sale, amount) in ParsePlan(plan))
                promised[sale] = promised.GetValueOrDefault(sale) + amount;
        var refunds = await db.SubscriptionChanges.AsNoTracking()
            .Where(c => c.RefundPayPalTransactionId != "" && (c.RefundStatus == RefundStatus.Pending || c.RefundStatus == RefundStatus.Refunded))
            .Select(c => new { c.RefundPayPalTransactionId, c.RefundGrossAmount })
            .ToListAsync();
        foreach (var refund in refunds)
            promised[refund.RefundPayPalTransactionId] = promised.GetValueOrDefault(refund.RefundPayPalTransactionId) + refund.RefundGrossAmount;
        return promised;
    }

    public static IEnumerable<(string Sale, decimal Amount)> ParsePlan(string? plan)
    {
        foreach (var part in (plan ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = part.LastIndexOf('=');
            if (at <= 0) continue;
            if (decimal.TryParse(part[(at + 1)..], NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
                yield return (part[..at], amount);
        }
    }

    // ---- SuperAdmin's two actions ----------------------------------------------------------------------------

    // Refunded at PayPal (the owner's step): the reward is paid, a credit note number is issued. The "paid" email is
    // the caller's to send (ReferralEmails.Paid), then it records PaidEmailSentAt.
    public static async Task<Referral?> MarkPaidAsync(IPRODbContext db, int referralId, string refundTransactionId, DateTime now)
    {
        var r = await db.Referrals.FirstOrDefaultAsync(x => x.Id == referralId && x.Stage == ReferralStages.Earned && x.RefundPlan != "");
        if (r == null || string.IsNullOrWhiteSpace(refundTransactionId)) return null;
        r.CreditNoteNumber = await NextCreditNoteNumberAsync(db, now);
        r.Stage = ReferralStages.Paid;
        r.PaidAt = now;
        r.RefundTransactionId = refundTransactionId.Trim();
        r.UpdatedAt = now;
        await db.SaveChangesAsync();
        return r;
    }

    public static async Task<Referral?> VoidAsync(IPRODbContext db, int referralId, string reason, string by, DateTime now)
    {
        var r = await db.Referrals.FirstOrDefaultAsync(x => x.Id == referralId);
        if (r == null || r.Stage is ReferralStages.Paid or ReferralStages.Voided || string.IsNullOrWhiteSpace(reason)) return null;
        Close(r, ReferralStages.Voided, reason.Trim(), by, now);
        await db.SaveChangesAsync();
        return r;
    }

    // "Looks fine": a person looked at the possible self-referral and let it stand.
    public static async Task<bool> ClearAttentionAsync(IPRODbContext db, int referralId, DateTime now) =>
        await db.Referrals.Where(r => r.Id == referralId && r.Attention != "")
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Attention, string.Empty).SetProperty(r => r.UpdatedAt, now)) > 0;

    private static async Task<string> NextCreditNoteNumberAsync(IPRODbContext db, DateTime now)
    {
        var prefix = $"CN-{now:yyyy}-";
        var next = await NumberSequences.NextAsync(db, $"credit-note:{now:yyyy}", async () =>
        {
            var numbers = await db.Referrals.AsNoTracking().Where(r => r.CreditNoteNumber.StartsWith(prefix)).Select(r => r.CreditNoteNumber).ToListAsync();
            return numbers.Select(n => long.TryParse(n[prefix.Length..], out var v) ? v : 0L).DefaultIfEmpty(0L).Max();
        });
        return $"{prefix}{next:0000}";
    }

    // Needs attention: a possible self-referral not yet looked at, or an earned reward with nothing to refund
    // against yet. A refund PayPal refuses joins this list with the automatic switch (later).
    public static IQueryable<Referral> NeedingAttention(IPRODbContext db) =>
        db.Referrals.Where(r =>
            (r.Attention != "" && r.Stage != ReferralStages.Paid && r.Stage != ReferralStages.Voided && r.Stage != ReferralStages.NotEarned) ||
            (r.Stage == ReferralStages.Earned && r.RefundPlan == ""));

    // ---- what the adviser's page shows ------------------------------------------------------------------------

    // The Profile and Dashboard cards: shown while the program runs, or once the adviser has referrals to follow.
    public sealed record Summary(bool Enabled, decimal Gift, decimal Reward, int Referred, decimal Earned)
    {
        public bool ShowCard => Enabled || Referred > 0;
    }

    public static async Task<Summary> SummaryAsync(IPRODbContext db, int agentUserId)
    {
        var settings = await LoadSettingsAsync(db);
        var rows = await db.Referrals.AsNoTracking().Where(r => r.AgentUserId == agentUserId)
            .Select(r => new { r.Stage, r.RewardAmount }).ToListAsync();
        return new Summary(settings.Enabled, settings.FriendGiftAmount, settings.ReferrerRewardAmount,
            rows.Count(r => r.Stage != ReferralStages.Voided),
            rows.Where(r => r.Stage == ReferralStages.Earned || r.Stage == ReferralStages.Paid).Sum(r => r.RewardAmount));
    }

    public sealed record AdviserView(ReferralCode Code, string Link, ReferralProgramSettings Settings, IReadOnlyList<Referral> Referrals)
    {
        public int ReferredCount => Referrals.Count(r => r.Stage != ReferralStages.Voided);
        public decimal EarnedTotal => Referrals.Where(r => r.Stage is ReferralStages.Earned or ReferralStages.Paid).Sum(r => r.RewardAmount);
    }

    public static async Task<AdviserView> ForAdviserAsync(IPRODbContext db, IConfiguration configuration, int agentUserId, string? firstName, DateTime now)
    {
        var code = await GetOrCreateCodeAsync(db, agentUserId, firstName, now);
        var settings = await LoadSettingsAsync(db);
        var referrals = await db.Referrals.AsNoTracking().Where(r => r.AgentUserId == agentUserId).OrderByDescending(r => r.SignedUpAt).ToListAsync();
        return new AdviserView(code, Link(code.Code, configuration), settings, referrals);
    }

    // What the adviser reads for a row: the stage and, while it is on its way, when. One that is not earned (or
    // was voided) says only that -- nothing more about the friend (the owner's rule).
    public static string AdviserStatus(Referral r) => r.Stage switch
    {
        ReferralStages.SignedUp => "Signed up; the gift waits for their first payment",
        ReferralStages.Joined => r.ExpectedEarnAt.HasValue ? $"Joined; your reward is expected around {r.ExpectedEarnAt.Value.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)}" : "Joined",
        ReferralStages.Earned => "Earned; your refund is on its way",
        ReferralStages.Paid => r.PaidAt.HasValue ? $"Paid {r.PaidAt.Value.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)}" : "Paid",
        _ => "Not earned"
    };

    // ---- words -------------------------------------------------------------------------------------------------

    public static string Money(decimal amount) =>
        amount == Math.Round(amount, 0)
            ? "$" + amount.ToString("#,0", CultureInfo.InvariantCulture)
            : "$" + amount.ToString("#,0.00", CultureInfo.InvariantCulture);

    private static string Unit(BillingPeriod period) => period == BillingPeriod.Annually ? "year" : "month";

    private static string PeriodWord(BillingPeriod period) => period == BillingPeriod.Annually ? "annual" : "monthly";

    private static async Task<bool> SendAsync(IEmailService email, AgentUser to, SignupNotice.Notice mail, ILogger logger)
    {
        try
        {
            var sent = await email.SendAsync(to.Email, $"{to.FirstName} {to.LastName}".Trim(), mail.Subject, mail.Html, mail.Text);
            if (!sent) logger.LogWarning("Referral email to adviser {AgentId} was not sent ({Subject}); it is tried again next hour.", to.Id, mail.Subject);
            return sent;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Referral email to adviser {AgentId} failed ({Subject}); it is tried again next hour.", to.Id, mail.Subject);
            return false;
        }
    }
}
