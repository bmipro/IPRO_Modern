using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Admin.Controllers;
using IPRO.Billing;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using EBilling = IPRO.Entities.Billing;

namespace IPRO.IntegrationTests;

// 532 (2026-10-05). Refer a Friend -- Give $50, Get $50, on the owner's design of 2026-09-29 (DOCS/TODO.md 532), built
// when he said "we might as well do the refere a friend system too". The friend's gift comes off the setup fee after
// any waiver, then the first period, then the second; the referrer's reward is earned at the friend's second monthly
// payment (30 days into an annual plan) and refunded, with its tax, against the referrer's own payments -- never by
// code: SuperAdmin -> Refunds. The same PayPal payer blocks; a shared phone or address only flags.
public class ReferAFriend532Tests
{
    private static readonly DateTime Now = new(2026, 10, 5, 15, 0, 0, DateTimeKind.Utc);

    // ---- the gift ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(50, 200, 60, 50, 0, 0)]    // Gold: all of it off the setup fee
    [InlineData(50, 0, 90, 0, 50, 0)]      // Platinum, setup fee waived: off the first month
    [InlineData(50, 0, 40, 0, 40, 10)]     // Silver, setup fee waived: first month free, $10 off the second
    [InlineData(50, 30, 40, 30, 20, 0)]    // a small setup fee, the rest off the first month
    [InlineData(50, 0, 20, 0, 20, 20)]     // never past the second period
    [InlineData(50, 0, 480, 0, 50, 0)]     // a year
    public void The_gift_comes_off_the_setup_fee_then_the_first_period_then_the_second(int gift, int setup, int cycle, int offSetup, int offFirst, int offSecond)
    {
        var split = ReferralProgram.Split(gift, setup, cycle);
        Assert.Equal((offSetup, offFirst, offSecond), ((int)split.SetupDiscount, (int)split.Cycle1Discount, (int)split.Cycle2Discount));
    }

    [Fact]
    public void The_first_invoice_and_the_sign_up_page_say_what_the_gift_did()
    {
        var gold = ReferralProgram.Split(50m, 200m, 60m);
        Assert.Equal("IPro Gold one-time setup fee - $50 off, a referral gift from Jane Doe", ReferralProgram.SetupLabel("IPro Gold", gold, 200m, "Jane Doe"));
        Assert.Null(ReferralProgram.RecurringLabel("IPro Gold", BillingPeriod.Monthly, gold, 60m, "Jane Doe"));

        var small = ReferralProgram.Split(50m, 30m, 40m);
        Assert.Equal("IPro Silver one-time setup fee - waived, a referral gift from Jane Doe", ReferralProgram.SetupLabel("IPro Silver", small, 30m, "Jane Doe"));
        Assert.Equal("IPro Silver monthly recurring subscription - $20 off the first month, a referral gift from Jane Doe",
            ReferralProgram.RecurringLabel("IPro Silver", BillingPeriod.Monthly, small, 40m, "Jane Doe"));

        var waived = ReferralProgram.Split(50m, 0m, 40m);
        Assert.Equal("IPro Silver monthly recurring subscription - first month free and $10 off the second, a referral gift from Jane Doe",
            ReferralProgram.RecurringLabel("IPro Silver", BillingPeriod.Monthly, waived, 40m, "Jane Doe"));

        var usable = new ReferralProgram.UsableCode("JANE-7K3Q", new AgentUser { FirstName = "Jane", LastName = "Doe", CompanyName = "Doe Insurance" }, 50m, 50m);
        Assert.Equal("A $50 gift from Jane Doe of Doe Insurance: a setup fee of $150 instead of $200 on IPro Gold, before tax.",
            ReferralProgram.DescribeGift(usable, "IPro Gold", BillingPeriod.Monthly, 200m, 60m));
        Assert.Equal("A $50 gift from Jane Doe of Doe Insurance: your first month at $40 instead of $90 on IPro Platinum, before tax.",
            ReferralProgram.DescribeGift(usable, "IPro Platinum", BillingPeriod.Monthly, 0m, 90m));
        Assert.Equal("A $50 gift from Jane Doe of Doe Insurance: your first month free, and your second month at $30 on IPro Silver, before tax.",
            ReferralProgram.DescribeGift(usable, "IPro Silver", BillingPeriod.Monthly, 0m, 40m));
        Assert.Equal("A $50 gift from Jane Doe", ReferralProgram.GiftFrom(usable with { Referrer = new AgentUser { FirstName = "Jane", LastName = "Doe" } }));
    }

    [Fact]
    public void A_free_period_is_a_PayPal_trial_with_no_price_and_the_regular_price_follows()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(PayPalBillingService.ReferralBillingCycles(BillingPeriod.Monthly, 0m, 30m, 40m)));
        var cycles = document.RootElement.EnumerateArray().ToList();
        Assert.Equal(3, cycles.Count);
        Assert.Equal("TRIAL", cycles[0].GetProperty("tenure_type").GetString());
        Assert.False(cycles[0].TryGetProperty("pricing_scheme", out _));
        Assert.Equal("30.00", cycles[1].GetProperty("pricing_scheme").GetProperty("fixed_price").GetProperty("value").GetString());
        Assert.Equal("REGULAR", cycles[2].GetProperty("tenure_type").GetString());
        Assert.Equal(3, cycles[2].GetProperty("sequence").GetInt32());
        Assert.Equal(0, cycles[2].GetProperty("total_cycles").GetInt32());
        Assert.Equal("40.00", cycles[2].GetProperty("pricing_scheme").GetProperty("fixed_price").GetProperty("value").GetString());
    }

    // ---- codes and sign-up ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Jane", "JANE")]
    [InlineData("Émilie", "EMILIE")]
    [InlineData("Jean-Pierre", "JEANPIER")]
    [InlineData("", "IPRO")]
    [InlineData("X", "IPRO")]
    public void A_code_is_the_first_name_and_four_characters_nobody_misreads(string firstName, string prefix) =>
        Assert.Matches($"^{prefix}-[23456789ABCDEFGHJKMNPQRSTUVWXYZ]{{4}}$", ReferralProgram.MakeCode(firstName));

    [Fact]
    public void The_link_opens_the_main_site_sign_up_with_no_business_type()
    {
        Assert.Equal("https://www.iproadvisers.com/Account/Register?ref=JANE-7K3Q", ReferralProgram.Link("JANE-7K3Q", Config()));
        Assert.Equal("https://staging.example.test/Account/Register?ref=JANE-7K3Q",
            ReferralProgram.Link("JANE-7K3Q", Config(("Referrals:SignupBaseUrl", "https://staging.example.test/"))));
    }

    [Fact]
    public void A_shared_phone_network_address_or_business_address_is_flagged_never_blocked()
    {
        var referrer = new AgentUser { Phone = "416-555-0100", RegistrationIpAddress = "203.0.113.7", CompanyAddress = "1 King St", City = "Toronto" };
        Assert.Equal("", ReferralProgram.SelfReferralSignal(referrer, new AgentUser { Phone = "905-555-0199", RegistrationIpAddress = "198.51.100.2", CompanyAddress = "9 Queen St", City = "Toronto" }));
        Assert.Equal("Possible self-referral: the same phone number as the referrer.", ReferralProgram.SelfReferralSignal(referrer, new AgentUser { CellPhone = "(416) 555 0100" }));
        Assert.Equal("Possible self-referral: the same network address at sign-up, the same business address as the referrer.",
            ReferralProgram.SelfReferralSignal(referrer, new AgentUser { RegistrationIpAddress = "203.0.113.7", CompanyAddress = " 1 king st", City = "toronto" }));
    }

    [Fact]
    public async Task A_code_works_only_while_the_program_runs_unpaused_for_an_open_account()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var jane = await Adviser(db, "Jane", "Doe");
        var code = await ReferralProgram.GetOrCreateCodeAsync(db, jane.Id, jane.FirstName, Now);
        Assert.Equal(code.Code, (await ReferralProgram.GetOrCreateCodeAsync(db, jane.Id, jane.FirstName, Now)).Code);
        Assert.StartsWith("JANE-", code.Code);

        Assert.Null(await ReferralProgram.FindUsableAsync(db, code.Code));   // the program ships OFF
        await ReferralProgram.SaveSettingsAsync(db, true, 50m, 50m, Now);
        var usable = await ReferralProgram.FindUsableAsync(db, " " + code.Code.ToLowerInvariant());
        Assert.NotNull(usable);
        Assert.Equal(jane.Id, usable!.Referrer.Id);
        Assert.Equal(50m, usable.GiftAmount);

        await ReferralProgram.SetPausedAsync(db, jane.Id, true, Now);
        Assert.Null(await ReferralProgram.FindUsableAsync(db, code.Code));
        await ReferralProgram.SetPausedAsync(db, jane.Id, false, Now);
        Assert.NotNull(await ReferralProgram.FindUsableAsync(db, code.Code));
        await db.AgentUsers.Where(a => a.Id == jane.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, false));
        Assert.Null(await ReferralProgram.FindUsableAsync(db, code.Code));   // a closed account's code stops by itself
    }

    [Fact]
    public async Task A_friend_joins_the_ledger_once_with_the_amounts_promised_that_day()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await ReferralProgram.SaveSettingsAsync(db, true, 50m, 50m, Now);
        var jane = await Adviser(db, "Jane", "Doe");
        var code = await ReferralProgram.GetOrCreateCodeAsync(db, jane.Id, jane.FirstName, Now);
        var bob = await Adviser(db, "Bob", "Moore", promotionCode: code.Code);

        var referral = await ReferralProgram.RecordSignupAsync(db, bob, Now);
        Assert.NotNull(referral);
        Assert.Equal((jane.Id, bob.Id, ReferralStages.SignedUp, "Bob Moore", 50m, 50m),
            (referral!.AgentUserId, referral.FriendAgentUserId, referral.Stage, referral.FriendName, referral.GiftAmount, referral.RewardAmount));
        Assert.Null(await ReferralProgram.RecordSignupAsync(db, bob, Now));

        await ReferralProgram.SaveSettingsAsync(db, true, 75m, 25m, Now);
        var kept = await db.Referrals.AsNoTracking().SingleAsync();
        Assert.Equal((50m, 50m), (kept.GiftAmount, kept.RewardAmount));   // "a promised $50 stays $50"

        var carol = await Adviser(db, "Carol", "Lee", promotionCode: "SPRING10");   // another code typed instead
        Assert.Null(await ReferralProgram.RecordSignupAsync(db, carol, Now));
        jane.PromotionCode = code.Code;                                              // nobody refers themselves
        Assert.Null(await ReferralProgram.RecordSignupAsync(db, jane, Now));
        Assert.Equal(1, await db.Referrals.CountAsync());
    }

    // ---- checkout ------------------------------------------------------------------------------------------

    [Fact]
    public async Task On_Gold_the_gift_comes_off_the_setup_fee_and_the_invoice_says_so()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var gold = await Package(db, "IPro Gold", setupFee: 200m, monthly: 60m);
        var (bob, _) = await ReferredFriend(db);
        var paypal = new ScriptedPayPal();

        var result = await Service(db, paypal).CreateSubscriptionAsync(bob.Id, gold.Id, BillingPeriod.Monthly, "https://app.example.test/Billing/PayPalReturn", "https://app.example.test/Billing/Cancel");

        Assert.True(result.Success, result.Message);
        Assert.Empty(paypal.PlansCreated);
        var subscription = Assert.Single(paypal.SubscriptionsCreated);
        Assert.Equal("P-IPRO-GOLD-M", subscription.GetProperty("plan_id").GetString());
        Assert.Equal("150.00", subscription.GetProperty("plan").GetProperty("payment_preferences").GetProperty("setup_fee").GetProperty("value").GetString());
        var lines = await db.InvoiceLineItems.AsNoTracking().Where(l => l.InvoiceId == result.InvoiceId).ToListAsync();
        Assert.Contains(lines, l => l.Description == "IPro Gold one-time setup fee - $50 off, a referral gift from Jane Doe" && l.Amount == 150m);
        Assert.Contains(lines, l => l.Description == "IPro Gold monthly recurring subscription" && l.Amount == 60m);
        var referral = await db.Referrals.AsNoTracking().SingleAsync();
        Assert.Equal((50m, 0m, 0m), (referral.GiftSetupDiscount, referral.GiftCycle1Discount, referral.GiftCycle2Discount));
        Assert.NotNull(referral.GiftBillingId);
    }

    [Fact]
    public async Task With_the_setup_fee_waived_the_gift_comes_off_the_first_months_on_a_plan_made_once()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var silver = await Package(db, "IPro Silver", setupFee: 150m, monthly: 40m, waived: true);
        db.Add(new ProvinceTaxRate { ProvinceCode = "ON", ProvinceName = "Ontario", TaxLabel = "HST", Rate = 0.13m, IsActive = true });
        await db.SaveChangesAsync();
        var (bob, _) = await ReferredFriend(db, province: "ON");
        var paypal = new ScriptedPayPal();
        var service = Service(db, paypal);

        var first = await service.CreateSubscriptionAsync(bob.Id, silver.Id, BillingPeriod.Monthly, "https://app.example.test/r", "https://app.example.test/c");
        Assert.True(first.Success, first.Message);

        var plan = Assert.Single(paypal.PlansCreated);
        var cycles = plan.GetProperty("billing_cycles").EnumerateArray().ToList();
        Assert.Equal(new[] { "TRIAL", "TRIAL", "REGULAR" }, cycles.Select(c => c.GetProperty("tenure_type").GetString()).ToArray());
        Assert.False(cycles[0].TryGetProperty("pricing_scheme", out _));   // the first month is free
        Assert.Equal("30.00", cycles[1].GetProperty("pricing_scheme").GetProperty("fixed_price").GetProperty("value").GetString());
        Assert.Equal("40.00", cycles[2].GetProperty("pricing_scheme").GetProperty("fixed_price").GetProperty("value").GetString());

        var subscription = Assert.Single(paypal.SubscriptionsCreated);
        Assert.Equal(paypal.PlanIds[0], subscription.GetProperty("plan_id").GetString());
        // HST grossed onto the two priced cycles; the free one has nothing to gross up.
        var overrides = subscription.GetProperty("plan").GetProperty("billing_cycles").EnumerateArray()
            .ToDictionary(c => c.GetProperty("sequence").GetInt32(), c => c.GetProperty("pricing_scheme").GetProperty("fixed_price").GetProperty("value").GetString());
        Assert.Equal(new Dictionary<int, string?> { [2] = "33.90", [3] = "45.20" }, overrides);
        Assert.False(subscription.GetProperty("plan").TryGetProperty("payment_preferences", out _));   // no setup fee

        var lines = await db.InvoiceLineItems.AsNoTracking().Where(l => l.InvoiceId == first.InvoiceId).ToListAsync();
        Assert.Contains(lines, l => l.Description == "IPro Silver monthly recurring subscription - first month free and $10 off the second, a referral gift from Jane Doe" && l.Amount == 0m);
        Assert.Equal(1, await db.ReferralPayPalPlans.CountAsync());

        // Back from PayPal without approving, they try again: the plan is reused, not made twice.
        var second = await service.CreateSubscriptionAsync(bob.Id, silver.Id, BillingPeriod.Monthly, "https://app.example.test/r", "https://app.example.test/c");
        Assert.True(second.Success, second.Message);
        Assert.Single(paypal.PlansCreated);
        Assert.Equal(2, paypal.SubscriptionsCreated.Count);
    }

    [Fact]
    public async Task A_promotion_code_or_a_voided_referral_brings_no_gift()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var gold = await Package(db, "IPro Gold", setupFee: 200m, monthly: 60m);
        var (bob, referral) = await ReferredFriend(db);
        await ReferralProgram.VoidAsync(db, referral.Id, "Test: voided before checkout", "owner", Now);
        var paypal = new ScriptedPayPal();

        var result = await Service(db, paypal).CreateSubscriptionAsync(bob.Id, gold.Id, BillingPeriod.Monthly, "https://app.example.test/r", "https://app.example.test/c");

        Assert.True(result.Success, result.Message);
        Assert.Equal("200.00", Assert.Single(paypal.SubscriptionsCreated).GetProperty("plan").GetProperty("payment_preferences").GetProperty("setup_fee").GetProperty("value").GetString());
    }

    [Fact]
    public async Task The_start_of_the_subscription_spends_the_gift_at_once_so_a_second_checkout_pays_in_full()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var gold = await Package(db, "IPro Gold", setupFee: 200m, monthly: 60m);
        var (bob, _) = await ReferredFriend(db);
        var paypal = new ScriptedPayPal();
        var service = Service(db, paypal);

        var checkout = await service.CreateSubscriptionAsync(bob.Id, gold.Id, BillingPeriod.Monthly, "https://app.example.test/r", "https://app.example.test/c");
        Assert.True(checkout.Success, checkout.Message);
        var approved = await service.CapturePaymentAsync(bob.Id, "I-SUB-1");
        Assert.True(approved.Success, approved.Message);
        var joined = await db.Referrals.AsNoTracking().SingleAsync();
        Assert.Equal(ReferralStages.Joined, joined.Stage);   // no hourly job needed
        Assert.NotNull(joined.ExpectedEarnAt);

        // They cancel within the hour and sign up again: full price, the gift was spent.
        await db.Billings.Where(b => b.AgentUserId == bob.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BillingStatus.Cancelled));
        db.ChangeTracker.Clear();
        var again = await Service(db, paypal).CreateSubscriptionAsync(bob.Id, gold.Id, BillingPeriod.Monthly, "https://app.example.test/r", "https://app.example.test/c");
        Assert.True(again.Success, again.Message);
        Assert.Equal("200.00", paypal.SubscriptionsCreated[^1].GetProperty("plan").GetProperty("payment_preferences").GetProperty("setup_fee").GetProperty("value").GetString());
    }

    // ---- the ledger, hour by hour -------------------------------------------------------------------------------

    [Fact]
    public async Task A_friend_deleted_before_subscribing_leaves_a_referral_that_is_not_earned()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (bob, _) = await ReferredFriend(db);
        await db.AgentUsers.Where(a => a.Id == bob.Id).ExecuteDeleteAsync();

        Assert.Equal(1, (await Advance(db, new RecordingEmail(), new Payers(), Now)).NotEarned);
        var closed = await db.Referrals.AsNoTracking().SingleAsync();
        Assert.Equal((ReferralStages.NotEarned, "The friend's account was closed before they subscribed."), (closed.Stage, closed.ClosedReason));
    }

    [Fact]
    public async Task Joined_then_earned_at_the_second_monthly_payment_then_refunded_against_the_referrers_own_payments()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var silver = await Package(db, "IPro Silver", setupFee: 150m, monthly: 40m);
        var (bob, referral) = await ReferredFriend(db);
        var jane = await db.AgentUsers.SingleAsync(a => a.Id == referral.AgentUserId);
        var janeBilling = await ActiveBilling(db, jane.Id, silver.Id, "I-JANE", Now.AddMonths(-3));
        await PaidInvoice(db, jane.Id, janeBilling.Id, 45.20m, "8JANE1", Now.AddDays(-20));
        var bobBilling = await ActiveBilling(db, bob.Id, silver.Id, "I-BOB", Now.AddHours(-1));
        await PaidInvoice(db, bob.Id, bobBilling.Id, 169.50m, "I-BOB, 1SETUP, 2CYCLE", Now.AddMinutes(-50));
        var email = new RecordingEmail();
        var payers = new Payers { ["I-JANE"] = "PAYER-JANE", ["I-BOB"] = "PAYER-BOB" };

        var run1 = await Advance(db, email, payers, Now);
        Assert.Equal(1, run1.Joined);
        var joined = await db.Referrals.AsNoTracking().SingleAsync();
        Assert.Equal(ReferralStages.Joined, joined.Stage);
        Assert.Equal(bobBilling.StartDate.AddMonths(1), joined.ExpectedEarnAt);
        var mail = Assert.Single(email.Sent);
        Assert.Equal(("jane@example.test", "Bob Moore joined with your gift"), (mail.To, mail.Subject));
        Assert.NotNull(joined.JoinedEmailSentAt);
        Assert.Equal(0, (await Advance(db, email, payers, Now)).JoinedEmails);   // once

        // The second monthly payment: earned. Jane's one $45.20 payment cannot take $56.50 back -- it waits.
        await PaidInvoice(db, bob.Id, bobBilling.Id, 45.20m, "9BOB2", Now.AddMonths(1));
        var run2 = await Advance(db, email, payers, Now.AddMonths(1).AddHours(1));
        Assert.Equal((1, 0, 1), (run2.Earned, run2.Planned, run2.Waiting));
        Assert.Equal(1, await ReferralProgram.NeedingAttention(db).CountAsync());

        // Jane pays again: the refund is planned newest first, split because PayPal refunds no more than a payment was.
        await PaidInvoice(db, jane.Id, janeBilling.Id, 45.20m, "8JANE2", Now.AddMonths(1).AddDays(1));
        var run3 = await Advance(db, email, payers, Now.AddMonths(1).AddDays(1).AddHours(1));
        Assert.Equal(1, run3.Planned);
        var earned = await db.Referrals.AsNoTracking().SingleAsync();
        Assert.Equal((ReferralStages.Earned, 50m, 6.50m, 56.50m, "8JANE2=45.20;8JANE1=11.30"),
            (earned.Stage, earned.RewardNet, earned.RewardTax, earned.RewardGross, earned.RefundPlan));
        Assert.Equal(0, await ReferralProgram.NeedingAttention(db).CountAsync());

        var paid = await ReferralProgram.MarkPaidAsync(db, earned.Id, "REFUND-1, REFUND-2", Now.AddMonths(1).AddDays(2));
        Assert.NotNull(paid);
        Assert.Equal((ReferralStages.Paid, "CN-2026-0001"), (paid!.Stage, paid.CreditNoteNumber));
        Assert.Null(await ReferralProgram.MarkPaidAsync(db, earned.Id, "AGAIN", Now));
    }

    [Fact]
    public async Task The_same_PayPal_payer_blocks_and_an_unreachable_PayPal_waits()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var silver = await Package(db, "IPro Silver", setupFee: 150m, monthly: 40m);
        var (bob, referral) = await ReferredFriend(db);
        await ActiveBilling(db, referral.AgentUserId, silver.Id, "I-JANE", Now.AddMonths(-3));
        await ActiveBilling(db, bob.Id, silver.Id, "I-BOB", Now.AddHours(-1));
        var email = new RecordingEmail();

        await Advance(db, email, new Payers { Unreachable = true }, Now);
        var waiting = await db.Referrals.AsNoTracking().SingleAsync();
        Assert.Equal(ReferralStages.Joined, waiting.Stage);
        Assert.Null(waiting.PayerCheckedAt);
        Assert.Empty(email.Sent);   // nobody is told before the check

        var run = await Advance(db, email, new Payers { ["I-JANE"] = "SAME-PAYER", ["I-BOB"] = "same-payer" }, Now);
        Assert.Equal(1, run.Blocked);
        var blocked = await db.Referrals.AsNoTracking().SingleAsync();
        Assert.Equal((ReferralStages.Voided, "Blocked: the friend pays iPro from the referrer's own PayPal account.", "automatic"),
            (blocked.Stage, blocked.ClosedReason, blocked.VoidedBy));
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task An_annual_plan_earns_30_days_after_its_first_payment_and_a_friend_who_leaves_earns_nothing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var gold = await Package(db, "IPro Gold", setupFee: 200m, monthly: 60m);
        var (bob, _) = await ReferredFriend(db);
        var bobBilling = await ActiveBilling(db, bob.Id, gold.Id, "I-BOB", Now.AddHours(-1), BillingPeriod.Annually);
        await PaidInvoice(db, bob.Id, bobBilling.Id, 870m, "1BOBYEAR", Now.AddMinutes(-50));
        var email = new RecordingEmail();
        var payers = new Payers();

        await Advance(db, email, payers, Now);
        var joined = await db.Referrals.AsNoTracking().SingleAsync();
        Assert.Equal(bobBilling.StartDate.AddDays(30), joined.ExpectedEarnAt);
        Assert.Contains("is earned 30 days after Bob Moore&#39;s first payment", email.Sent.Single().Html);
        Assert.Equal(0, (await Advance(db, email, payers, Now.AddDays(29))).Earned);
        Assert.Equal(1, (await Advance(db, email, payers, Now.AddDays(31))).Earned);

        // Another friend joins and leaves before their second payment.
        var (carol, _) = await ReferredFriend(db, friendFirst: "Carol", friendLast: "Lee", referrerFirst: "Ann", referrerLast: "Kerr");
        var carolBilling = await ActiveBilling(db, carol.Id, gold.Id, "I-CAROL", Now.AddHours(-1));
        await PaidInvoice(db, carol.Id, carolBilling.Id, 260m, "1CAROL", Now.AddMinutes(-40));
        await Advance(db, email, payers, Now);
        await db.Billings.Where(b => b.Id == carolBilling.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BillingStatus.Cancelled));
        Assert.Equal(1, (await Advance(db, email, payers, Now.AddDays(10))).NotEarned);
        var left = await db.Referrals.AsNoTracking().SingleAsync(r => r.FriendAgentUserId == carol.Id);
        Assert.Equal(ReferralStages.NotEarned, left.Stage);
        Assert.Equal("Not earned", ReferralProgram.AdviserStatus(left));   // nothing more about the friend
    }

    [Fact]
    public async Task A_refund_already_promised_from_a_payment_is_not_promised_twice()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var gold = await Package(db, "IPro Gold", setupFee: 200m, monthly: 60m);
        var (_, referral) = await ReferredFriend(db);
        var janeBilling = await ActiveBilling(db, referral.AgentUserId, gold.Id, "I-JANE", Now.AddMonths(-3));
        await PaidInvoice(db, referral.AgentUserId, janeBilling.Id, 67.80m, "7SALE", Now.AddDays(-5));
        db.Add(new SubscriptionChange
        {
            AgentUserId = referral.AgentUserId, BillingId = janeBilling.Id, CurrentBillingRuleId = gold.Id, RequestedBillingRuleId = gold.Id,
            ChangeType = SubscriptionChangeType.Cancel, Status = SubscriptionChangeStatus.Applied,
            RefundStatus = RefundStatus.Pending, RefundGrossAmount = 30m, RefundPayPalTransactionId = "7SALE", EffectiveDate = Now,
        });
        await db.Referrals.Where(r => r.Id == referral.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Stage, ReferralStages.Earned).SetProperty(r => r.EarnedAt, Now));
        await db.SaveChangesAsync();

        var reloaded = await db.Referrals.AsNoTracking().SingleAsync();
        Assert.Null(await ReferralProgram.PlanRefundAsync(db, reloaded, Now));   // 67.80 - 30 = 37.80 < 56.50
    }

    [Fact]
    public void A_refund_is_planned_only_against_amounts_known_per_PayPal_sale()
    {
        Assert.Equal(new[] { ("8RENEWAL", 45.20m) }, ReferralProgram.Sales(new Invoice { Total = 45.20m, TaxRate = 0.13m, PayPalTransactionId = "8RENEWAL" }));

        var signup = new Invoice
        {
            Total = 214.70m, TaxRate = 0.13m, PayPalTransactionId = "I-SUB1, 1SETUP, 2CYCLE",
            LineItems = { new InvoiceLineItem { Description = "IPro Silver one-time setup fee", Amount = 150m }, new InvoiceLineItem { Description = "IPro Silver monthly recurring subscription", Amount = 40m } }
        };
        Assert.Equal(new[] { ("1SETUP", 169.50m), ("2CYCLE", 45.20m) }, ReferralProgram.Sales(signup));

        Assert.Empty(ReferralProgram.Sales(new Invoice { Total = 45.20m, PayPalTransactionId = "I-SUB1" }));
        Assert.Equal(new[] { ("3RETRY", 45.20m) }, ReferralProgram.Sales(new Invoice { Total = 45.20m, PayPalTransactionId = "PAYPAL_FAILED:9FAIL, 3RETRY" }));
        Assert.Empty(ReferralProgram.Sales(new Invoice { Total = 100m, PayPalTransactionId = "A1, B2" }));   // the split is unknown
        Assert.Equal(new[] { ("A1", 45.20m), ("B2", 11.30m) }, ReferralProgram.ParsePlan("A1=45.20;B2=11.30").ToArray());
    }

    // ---- the emails ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_referrer_is_told_exactly_what_happens_and_when()
    {
        var jane = new AgentUser { FirstName = "Jane", LastName = "Doe", Email = "jane@example.test" };
        var monthly = new Referral { FriendName = "Bob Moore", RewardAmount = 50m, FriendPeriod = BillingPeriod.Monthly, ExpectedEarnAt = new DateTime(2026, 11, 5) };
        var joined = ReferralEmails.Joined(monthly, jane, Config(("App:BaseUrl", "https://app.example.test")));
        Assert.Equal("Bob Moore joined with your gift", joined.Subject);
        Assert.Contains("Your $50 is earned at Bob Moore&#39;s second monthly payment, expected around November 5, 2026, and comes back to the card or PayPal account you pay iPro with.", joined.Html);
        Assert.Contains("href=\"https://app.example.test/portal/ReferAFriend\"", joined.Html);
        Assert.StartsWith("Hi Jane,", joined.Text);

        var annual = new Referral { FriendName = "Bob Moore", RewardAmount = 50m, FriendPeriod = BillingPeriod.Annually, ExpectedEarnAt = new DateTime(2026, 11, 4) };
        Assert.Contains("Your $50 is earned 30 days after Bob Moore&#39;s first payment, around November 4, 2026, if Bob Moore is still subscribed then",
            ReferralEmails.Joined(annual, jane, Config()).Html);

        var paid = ReferralEmails.Paid(new Referral { FriendName = "Bob Moore", RewardNet = 50m, RewardTax = 6.50m, RewardGross = 56.50m, CreditNoteNumber = "CN-2026-0001" }, jane, Config());
        Assert.Equal("Your $50 referral reward for Bob Moore", paid.Subject);
        Assert.Contains("$50 plus tax ($56.50) for Bob Moore&#39;s referral was refunded today to the card or PayPal account you pay iPro with. PayPal shows it right away; a card can take a few business days.", paid.Html);
        Assert.Contains("CN-2026-0001", paid.Html);
    }

    // ---- SuperAdmin ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Marking_a_reward_refunded_numbers_its_credit_note_and_emails_the_referrer()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (_, referral) = await ReferredFriend(db);
        await db.Referrals.Where(r => r.Id == referral.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Stage, ReferralStages.Earned).SetProperty(r => r.EarnedAt, Now)
            .SetProperty(r => r.RewardNet, 50m).SetProperty(r => r.RewardTax, 6.50m).SetProperty(r => r.RewardGross, 56.50m)
            .SetProperty(r => r.RefundPlan, "8JANE2=56.50"));
        var email = new RecordingEmail();
        var audit = new RecordingAudit();
        var controller = Admin(new RefundsController(db, audit, email, Config(), NullLogger<RefundsController>.Instance));

        await controller.MarkReferralRefunded(referral.Id, "");
        Assert.Equal(ReferralStages.Earned, (await db.Referrals.AsNoTracking().SingleAsync()).Stage);   // no refund id, no change

        await controller.MarkReferralRefunded(referral.Id, "REFUND-9");
        var paid = await db.Referrals.AsNoTracking().SingleAsync();
        Assert.Equal((ReferralStages.Paid, "REFUND-9"), (paid.Stage, paid.RefundTransactionId));
        Assert.StartsWith("CN-", paid.CreditNoteNumber);
        Assert.NotNull(paid.PaidEmailSentAt);
        var mail = Assert.Single(email.Sent);
        Assert.Equal(("jane@example.test", "Your $50 referral reward for Bob Moore"), (mail.To, mail.Subject));
        Assert.Contains(audit.Entries, e => e.Action == "ReferralRewardRefunded" && e.Details.Contains("REFUND-9"));
    }

    [Fact]
    public async Task SuperAdmin_switches_the_program_voids_with_a_reason_and_exports_safely()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (_, referral) = await ReferredFriend(db, friendFirst: "=cmd", friendLast: "Evil");
        var audit = new RecordingAudit();
        var controller = Admin(new ReferralsController(db, audit));

        await controller.Settings(true, 600m, 50m);
        Assert.Equal(50m, (await ReferralProgram.LoadSettingsAsync(db)).FriendGiftAmount);   // refused: over the limit
        await controller.Settings(false, 40m, 60m);
        var settings = await ReferralProgram.LoadSettingsAsync(db);
        Assert.Equal((false, 40m, 60m), (settings.Enabled, settings.FriendGiftAmount, settings.ReferrerRewardAmount));
        Assert.Contains(audit.Entries, e => e.Action == "ReferralProgramSettings");

        await controller.Void(referral.Id, "  ", null);
        Assert.Equal(ReferralStages.SignedUp, (await db.Referrals.AsNoTracking().SingleAsync()).Stage);
        await controller.Void(referral.Id, "Duplicate account", null);
        var voided = await db.Referrals.AsNoTracking().SingleAsync();
        Assert.Equal((ReferralStages.Voided, "Duplicate account"), (voided.Stage, voided.ClosedReason));
        Assert.Contains(audit.Entries, e => e.Action == "ReferralVoided");

        var csv = Assert.IsType<Microsoft.AspNetCore.Mvc.FileContentResult>(await controller.Csv());
        var text = Encoding.UTF8.GetString(csv.FileContents);
        Assert.Contains("'=cmd Evil", text);
        Assert.Equal("'=HYPERLINK(1)", ReferralsController.Cell("=HYPERLINK(1)"));
        Assert.Equal("\"Doe, Jane\"", ReferralsController.Cell("Doe, Jane"));
    }

    [Fact]
    public async Task The_start_up_tables_match_the_model()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        foreach (var table in new[] { "Referrals", "ReferralCodes", "ReferralPayPalPlans", "ReferralProgramSettings" })
            await db.Database.ExecuteSqlRawAsync("DROP TABLE `" + table + "`");   // four fixed names
        await StartupSchemaRepair.EnsureReferralSchemaAsync(db);
        await StartupSchemaRepair.EnsureReferralSchemaAsync(db);   // idempotent

        await ReferralProgram.SaveSettingsAsync(db, true, 50m, 50m, Now);
        var (_, referral) = await ReferredFriend(db);
        await db.Referrals.Where(r => r.Id == referral.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.GiftBillingId, 7).SetProperty(r => r.GiftSetupDiscount, 50m).SetProperty(r => r.JoinedAt, Now)
            .SetProperty(r => r.FriendPeriod, BillingPeriod.Annually).SetProperty(r => r.ExpectedEarnAt, Now).SetProperty(r => r.JoinedEmailSentAt, Now)
            .SetProperty(r => r.PayerCheckedAt, Now).SetProperty(r => r.EarnedAt, Now).SetProperty(r => r.RewardTaxRate, 0.14975m)
            .SetProperty(r => r.RewardTaxRegion, "QC GST+QST").SetProperty(r => r.RefundPlan, "A=1.00").SetProperty(r => r.RefundWindowEndsAt, Now)
            .SetProperty(r => r.RefundTransactionId, "R").SetProperty(r => r.PaidAt, Now).SetProperty(r => r.PaidEmailSentAt, Now)
            .SetProperty(r => r.CreditNoteNumber, "CN-2026-0009").SetProperty(r => r.ClosedAt, Now).SetProperty(r => r.ClosedReason, "x")
            .SetProperty(r => r.VoidedBy, "owner").SetProperty(r => r.Attention, "y"));
        var row = await db.Referrals.AsNoTracking().SingleAsync();
        Assert.Equal((0.14975m, BillingPeriod.Annually, "CN-2026-0009"), (row.RewardTaxRate, row.FriendPeriod, row.CreditNoteNumber));
        db.ReferralPayPalPlans.Add(new ReferralPayPalPlan { BillingRuleId = 1, Period = BillingPeriod.Monthly, Cycle1Price = 0m, Cycle2Price = 30m, RegularPrice = 40m, PayPalPlanId = "P-1" });
        await db.SaveChangesAsync();
        Assert.Equal(30m, (await db.ReferralPayPalPlans.AsNoTracking().SingleAsync()).Cycle2Price);
    }

    // ---- the wiring -------------------------------------------------------------------------------------------

    [Fact]
    public void Sign_up_checkout_the_job_and_both_apps_are_wired()
    {
        var account = Read(@"src\IPRO.Web\Controllers\AccountController.cs");
        Assert.Contains("[FromQuery(Name = \"ref\")] string? referralCode = null", account);
        Assert.Contains("PromotionCode = referralGift?.Code", account);
        Assert.Contains("if (await ReferralProgram.FindUsableAsync(_db, model.PromotionCode) == null)", account);
        Assert.Contains("await ReferralProgram.RecordSignupAsync(_db, agent, DateTime.UtcNow);", account);
        Assert.Contains("message = ReferralProgram.DescribeGift(referral, package.PackageName, referralPeriod,", account);
        Assert.Contains("ViewBag.ReferralSummary = await ReferralProgram.SummaryAsync(_db, agent.Id);", account);

        var register = Read(@"src\IPRO.Web\Views\Account\Register.cshtml");
        Assert.Contains("@IPRO.Billing.ReferralProgram.GiftFrom(referralGift)", register);
        Assert.Contains("When you join, we will thank @referralGift.Referrer.FirstName for referring you.", register);

        Assert.Contains("referralGift = await ReferralProgram.ForCheckoutAsync(_db, userId, baseSetupFee, cycleAmount);", Read(@"src\IPRO.Billing\PayPalBillingService.cs"));

        var profile = Read(@"src\IPRO.Web\Views\Account\Profile.cshtml");
        Assert.True(profile.IndexOf("<a href=\"/portal/ReferAFriend\" class=\"btn btn-primary\">", StringComparison.Ordinal)
                    < profile.IndexOf(">Calendar Source</h5>", StringComparison.Ordinal));   // just above Calendar Source
        Assert.Contains("href=\"/portal/ReferAFriend\"", Read(@"src\IPRO.Web\Views\Dashboard\Index.cshtml"));
        Assert.Contains("ViewBag.ReferralSummary = await ReferralProgram.SummaryAsync(_db, agentId);", Read(@"src\IPRO.Web\Controllers\DashboardController.cs"));

        var web = Read(@"src\IPRO.Web\Program.cs");
        Assert.Contains("RecurringJob.AddOrUpdate<ReferralJob>(\"refer-a-friend\", job => job.RunAsync(), \"20 * * * *\");", web);
        Assert.Contains("builder.Services.AddScoped<IReferralPayerLookup, PayPalBillingService>();", web);
        foreach (var program in new[] { @"src\IPRO.Web\Program.cs", @"src\IPRO.Admin\Program.cs" })
            Assert.Contains("StartupSchemaRepair.EnsureReferralSchemaAsync(db)", Read(program));

        var eraser = Read(@"src\IPRO.DataAccess\AgentDataEraser.cs");
        var financialMap = eraser.IndexOf("FinancialMap =", StringComparison.Ordinal);
        Assert.InRange(eraser.IndexOf("(\"ReferralCodes\",", StringComparison.Ordinal), 0, financialMap);   // goes with the adviser
        Assert.True(eraser.IndexOf("(\"Referrals\",", StringComparison.Ordinal) > financialMap);              // kept with the invoices

        Assert.Contains("href=\"/Referrals\"", Read(@"src\IPRO.Admin\Views\Shared\_Layout.cshtml"));
        Assert.Contains("asp-action=\"MarkReferralRefunded\"", Read(@"src\IPRO.Admin\Views\Refunds\Index.cshtml"));
        Assert.Contains("ViewBag.ReferralsNeedingAttention = await ReferralProgram.NeedingAttention(_db).CountAsync();", Read(@"src\IPRO.Admin\Controllers\AdminDashboardController.cs"));
        Assert.Contains("else if (await _db.ReferralCodes.AnyAsync(r => r.Code == model.Code))", Read(@"src\IPRO.Admin\Controllers\PromotionCodesController.cs"));
        Assert.Contains("## Refer a Friend", Read(@"DOCS\01_AGENT_ACCOUNT_AND_DASHBOARD.md"));
    }

    // ---- plumbing ---------------------------------------------------------------------------------------------

    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    private static Task<ReferralProgram.AdvanceReport> Advance(IPRODbContext db, RecordingEmail email, Payers payers, DateTime now)
    {
        db.ChangeTracker.Clear();
        return ReferralProgram.AdvanceAsync(db, email, Config(), NullLogger.Instance, payers, now);
    }

    private static PayPalBillingService Service(IPRODbContext db, ScriptedPayPal paypal) => new(
        new UnitOfWork(db), db, paypal, new RecordingEmail(),
        Options.Create(new PayPalSettings { ClientId = "test-client", ClientSecret = "test-secret" }),
        Config(), NullLogger<PayPalBillingService>.Instance);

    private static int _adviserNumber;

    private static async Task<AgentUser> Adviser(IPRODbContext db, string first, string last, string promotionCode = "", string province = "")
    {
        // Distinct phones and network addresses, or a test could flag a "possible self-referral" by chance.
        var n = Interlocked.Increment(ref _adviserNumber);
        var agent = new AgentUser
        {
            UserName = $"rf-{Guid.NewGuid():N}"[..20], DomainName = $"rf-{Guid.NewGuid():N}"[..24],
            FirstName = first, LastName = last, CompanyName = $"{last} Group", Email = $"{first.ToLowerInvariant().Trim('=')}@example.test",
            Phone = $"416-555-{n:D4}", Province = province, Country = province.Length > 0 ? "Canada" : "",
            PromotionCode = promotionCode, RegistrationIpAddress = $"198.51.{n / 250}.{n % 250 + 1}",
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent;
    }

    // A referrer with a code, the program on, and a friend signed up with it.
    private static async Task<(AgentUser Friend, Referral Referral)> ReferredFriend(IPRODbContext db, string province = "",
        string friendFirst = "Bob", string friendLast = "Moore", string referrerFirst = "Jane", string referrerLast = "Doe")
    {
        await ReferralProgram.SaveSettingsAsync(db, true, 50m, 50m, Now);
        var referrer = await Adviser(db, referrerFirst, referrerLast);
        var code = await ReferralProgram.GetOrCreateCodeAsync(db, referrer.Id, referrer.FirstName, Now);
        var friend = await Adviser(db, friendFirst, friendLast, promotionCode: code.Code, province: province);
        var referral = await ReferralProgram.RecordSignupAsync(db, friend, Now.AddHours(-2));
        Assert.NotNull(referral);
        db.ChangeTracker.Clear();
        return (friend, referral!);
    }

    private static async Task<BillingRule> Package(IPRODbContext db, string name, decimal setupFee, decimal monthly, bool waived = false)
    {
        var slug = name.ToUpperInvariant().Replace(' ', '-');
        var package = new BillingRule
        {
            PackageName = name, SetupFee = setupFee, SetupFeeWaived = waived, MonthlyPrice = monthly, AnnualPrice = monthly * 12,
            PayPalMonthlyPlanId = $"P-{slug}-M", PayPalAnnualPlanId = $"P-{slug}-A", IsActive = true,
        };
        db.Add(package);
        await db.SaveChangesAsync();
        return package;
    }

    private static async Task<EBilling> ActiveBilling(IPRODbContext db, int agentId, int packageId, string subscriptionId, DateTime start, BillingPeriod period = BillingPeriod.Monthly)
    {
        var billing = new EBilling { AgentUserId = agentId, BillingRuleId = packageId, PayPalSubscriptionId = subscriptionId, Amount = 40m, Status = BillingStatus.Active, Period = period, StartDate = start, CreatedAt = start };
        db.Add(billing);
        await db.SaveChangesAsync();
        return billing;
    }

    private static async Task PaidInvoice(IPRODbContext db, int agentId, int billingId, decimal total, string transactions, DateTime issuedAt)
    {
        db.Add(new Invoice
        {
            BillingId = billingId, AgentUserId = agentId, InvoiceNumber = $"T-{Guid.NewGuid():N}"[..20], SubTotal = Math.Round(total / 1.13m, 2),
            TaxAmount = total - Math.Round(total / 1.13m, 2), TaxRate = 0.13m, TaxRegion = "ON HST", Total = total, PayPalTransactionId = transactions,
            IssuedAt = issuedAt, IsPaid = true,
        });
        await db.SaveChangesAsync();
    }

    private static T Admin<T>(T controller) where T : Microsoft.AspNetCore.Mvc.Controller
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "1"), new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "owner") }, "test"))
        };
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = http };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(http, new NullTempData());
        controller.Url = new NoUrls();
        return controller;
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative)).Replace("\r\n", "\n");
    }

    private sealed class Payers : Dictionary<string, string>, IReferralPayerLookup
    {
        public bool Unreachable { get; init; }
        public Task<(bool Reached, string PayerId)> PayerAsync(string subscriptionId) =>
            Task.FromResult(Unreachable ? (false, string.Empty) : (true, TryGetValue(subscriptionId, out var payer) ? payer : string.Empty));
    }

    // PayPal as checkout meets it: a token, products, plans (kept so a plan can be read back for the tax
    // override), subscriptions (kept to be inspected), and cancellations.
    private sealed class ScriptedPayPal : IHttpClientFactory
    {
        public readonly List<JsonElement> PlansCreated = new();
        public readonly List<string> PlanIds = new();
        public readonly List<JsonElement> SubscriptionsCreated = new();
        private readonly Dictionary<string, string> _plans = new();

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler : HttpMessageHandler
        {
            private readonly ScriptedPayPal _paypal;
            public Handler(ScriptedPayPal paypal) => _paypal = paypal;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri!.AbsoluteUri;
                var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                if (url.Contains("/v1/oauth2/token")) return Json("{\"access_token\":\"scripted-token\",\"expires_in\":3600}");
                if (request.Method == HttpMethod.Post && url.EndsWith("/v1/catalogs/products")) return Json("{\"id\":\"PROD-1\"}");
                if (request.Method == HttpMethod.Post && url.EndsWith("/v1/billing/plans"))
                {
                    var id = $"P-REF-{_paypal.PlanIds.Count + 1}";
                    _paypal.PlanIds.Add(id);
                    _paypal.PlansCreated.Add(JsonDocument.Parse(body).RootElement.Clone());
                    _paypal._plans[id] = body;
                    return Json($"{{\"id\":\"{id}\"}}");
                }
                if (request.Method == HttpMethod.Get && url.Contains("/v1/billing/plans/"))
                {
                    var id = url.Split('/')[^1];
                    var cycles = _paypal._plans.TryGetValue(id, out var plan)
                        ? JsonDocument.Parse(plan).RootElement.GetProperty("billing_cycles").GetRawText()
                        : "[{\"sequence\":1,\"tenure_type\":\"REGULAR\",\"pricing_scheme\":{\"fixed_price\":{\"value\":\"60.00\",\"currency_code\":\"CAD\"}}}]";
                    return Json($"{{\"id\":\"{id}\",\"billing_cycles\":{cycles}}}");
                }
                if (request.Method == HttpMethod.Post && url.EndsWith("/v1/billing/subscriptions"))
                {
                    _paypal.SubscriptionsCreated.Add(JsonDocument.Parse(body).RootElement.Clone());
                    var id = $"I-SUB-{_paypal.SubscriptionsCreated.Count}";
                    return Json($"{{\"id\":\"{id}\",\"status\":\"APPROVAL_PENDING\",\"links\":[{{\"rel\":\"approve\",\"href\":\"https://paypal.test/approve/{id}\"}}]}}");
                }
                if (request.Method == HttpMethod.Post && url.Contains("/cancel")) return new HttpResponseMessage(HttpStatusCode.NoContent);
                if (request.Method == HttpMethod.Get && url.Contains("/v1/billing/subscriptions/")) return Json("{\"status\":\"ACTIVE\",\"subscriber\":{\"payer_id\":\"PAYER-1\"}}");
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class RecordingEmail : IEmailService
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = new();
        public Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        {
            Sent.Add((toEmail, subject, htmlBody));
            return Task.FromResult(true);
        }
        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        {
            Sent.Add((toEmail, subject, htmlBody));
            return Task.FromResult(EmailSendResult.Sent());
        }
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
    }

    private sealed class RecordingAudit : IPRO.Business.Interfaces.IAdminAuditLogService
    {
        public List<(string Action, string Details)> Entries { get; } = new();
        public Task LogAsync(int adminUserId, string adminUsername, string action, string details)
        {
            Entries.Add((action, details));
            return Task.CompletedTask;
        }
    }

    private sealed class NullTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(Microsoft.AspNetCore.Http.HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(Microsoft.AspNetCore.Http.HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class NoUrls : Microsoft.AspNetCore.Mvc.IUrlHelper
    {
        public Microsoft.AspNetCore.Mvc.ActionContext ActionContext => new();
        public string? Action(Microsoft.AspNetCore.Mvc.Routing.UrlActionContext actionContext) => "/";
        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) => !string.IsNullOrEmpty(url) && url.StartsWith('/');
        public string? Link(string? routeName, object? values) => "/";
        public string? RouteUrl(Microsoft.AspNetCore.Mvc.Routing.UrlRouteContext routeContext) => "/";
    }
}
