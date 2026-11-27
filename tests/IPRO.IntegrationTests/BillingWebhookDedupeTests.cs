using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
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

// Audit items 422(c) and 422(d), driven through the REAL webhook handlers against the real database.
// The handlers are internal (InternalsVisibleTo) because the public HandleWebhookAsync entry
// performs live PayPal signature verification; everything below that entry is exercised as shipped.
//
// (d) BILLING.SUBSCRIPTION.PAYMENT.FAILED used to mint a fresh numbered unpaid invoice for EVERY
//     delivery -- and PayPal both retries failing payments and redelivers webhooks, so one bad card
//     produced a pile of phantom invoices. Now: one open failure marker per billing, later failures
//     append their transaction ids to it, replays are no-ops.
// (c) PAYMENT.SALE.COMPLETED deduplicated replays only inside a 6-hour absorb window, so a resend
//     arriving later minted a second PAID invoice for the same charge (resends observed 2026-08-10).
//     Now: a transaction id recorded anywhere on the billing makes the delivery a permanent no-op.
public class BillingWebhookDedupeTests
{
    [Fact]
    public async Task Repeated_payment_failures_share_one_marker_invoice_and_replays_change_nothing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: true);
        await using var db = testDb.CreateContext();
        var (billing, service) = await SeedAsync(db, "I-FAILPILE");

        Assert.True(await service.HandleSubscriptionPaymentFailedWebhookAsync("I-FAILPILE", "TX-FAIL-1"));
        Assert.True(await service.HandleSubscriptionPaymentFailedWebhookAsync("I-FAILPILE", "TX-FAIL-2"));
        Assert.True(await service.HandleSubscriptionPaymentFailedWebhookAsync("I-FAILPILE", "TX-FAIL-1")); // replay

        var invoices = await db.Set<Invoice>().Where(i => i.BillingId == billing.Id).ToListAsync();
        var marker = Assert.Single(invoices); // ONE invoice, not three
        Assert.False(marker.IsPaid);
        Assert.Contains("PAYPAL_FAILED:TX-FAIL-1", marker.PayPalTransactionId);
        Assert.Contains("PAYPAL_FAILED:TX-FAIL-2", marker.PayPalTransactionId);
        // The replay appended nothing: TX-FAIL-1 appears exactly once.
        Assert.Equal(1, CountOf(marker.PayPalTransactionId!, "TX-FAIL-1"));
    }

    [Fact]
    public async Task A_success_settles_the_failure_marker_and_a_late_replay_is_a_permanent_noop()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: true);
        await using var db = testDb.CreateContext();
        var (billing, service) = await SeedAsync(db, "I-RETRYOK");

        Assert.True(await service.HandleSubscriptionPaymentFailedWebhookAsync("I-RETRYOK", "TX-F"));
        // The retry succeeds. Gross == net here because the test agent has no province -> no tax.
        Assert.True(await service.HandleSubscriptionPaymentCompletedWebhookAsync("I-RETRYOK", "TX-OK", 67.80m));

        var invoices = await db.Set<Invoice>().Where(i => i.BillingId == billing.Id).ToListAsync();
        var settled = Assert.Single(invoices);
        Assert.True(settled.IsPaid);
        Assert.Contains("PAYPAL_FAILED:TX-F", settled.PayPalTransactionId); // audit trail kept
        Assert.Contains("TX-OK", settled.PayPalTransactionId);

        // The 422(c) core: replay the SAME sale. Before the fix this was only recognised for 6
        // hours; a later redelivery minted a duplicate paid invoice. The transaction-id guard has
        // no clock, so the replay is idempotent forever -- simulate "later" simply by replaying.
        Assert.True(await service.HandleSubscriptionPaymentCompletedWebhookAsync("I-RETRYOK", "TX-OK", 67.80m));
        Assert.True(await service.HandleSubscriptionPaymentCompletedWebhookAsync("I-RETRYOK", "TX-OK", 67.80m));

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Set<Invoice>().CountAsync(i => i.BillingId == billing.Id));
        var after = await db.Set<Invoice>().SingleAsync(i => i.BillingId == billing.Id);
        Assert.Equal(1, CountOf(after.PayPalTransactionId!, "TX-OK")); // recorded once, not re-appended
    }

    [Fact]
    public async Task A_success_settles_the_unpaid_invoice_whose_total_matches_the_charge()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: true);
        await using var db = testDb.CreateContext();
        var (billing, service) = await SeedAsync(db, "I-AMTMATCH");

        // Two unpaid invoices: an older large one and a newer small one (an upgrade proration).
        // Oldest-first alone would have settled the $100 invoice with a $20.30 charge.
        db.Add(NewInvoice(billing, "IPRO-2098-000001", 100.00m, issuedAt: DateTime.UtcNow.AddDays(-3)));
        db.Add(NewInvoice(billing, "IPRO-2098-000002", 20.30m, issuedAt: DateTime.UtcNow.AddDays(-1)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.True(await service.HandleSubscriptionPaymentCompletedWebhookAsync("I-AMTMATCH", "TX-2030", 20.30m));

        db.ChangeTracker.Clear();
        var small = await db.Set<Invoice>().SingleAsync(i => i.InvoiceNumber == "IPRO-2098-000002");
        var large = await db.Set<Invoice>().SingleAsync(i => i.InvoiceNumber == "IPRO-2098-000001");
        Assert.True(small.IsPaid);
        Assert.Contains("TX-2030", small.PayPalTransactionId);
        Assert.False(large.IsPaid); // the unrelated bill was not marked paid by someone else's money
    }

    // B01-01 (audit 2026-11-26). PayPal's BILLING.SUBSCRIPTION.PAYMENT.FAILED carries the SUBSCRIPTION as its resource,
    // so resource.id is the subscription id, the same id the sign-up invoice has carried since the subscription was
    // made. Passed on as the "transaction id", it made every failure look like a replay: nothing was ever recorded.
    private const string PayPalFailedSample = """
        {
          "id": "WH-7CT25911MX1234567-9XJ49371PF012345A",
          "event_type": "BILLING.SUBSCRIPTION.PAYMENT.FAILED",
          "resource_type": "subscription",
          "resource": {
            "id": "I-REALSHAPE",
            "plan_id": "P-5ML4271244454362WXNWU5NQ",
            "status": "ACTIVE",
            "billing_info": {
              "outstanding_balance": { "currency_code": "CAD", "value": "67.80" },
              "failed_payments_count": 1,
              "last_failed_payment": {
                "amount": { "currency_code": "CAD", "value": "67.80" },
                "time": "2026-11-27T10:00:00Z",
                "reason_code": "PAYMENT_DENIED",
                "next_payment_retry_time": "2026-12-02T10:00:00Z"
              }
            }
          }
        }
        """;

    [Fact]
    public void A_failed_payment_is_named_by_its_own_time_never_by_the_subscription()
    {
        using var sample = System.Text.Json.JsonDocument.Parse(PayPalFailedSample);
        var resource = sample.RootElement.GetProperty("resource");
        Assert.Equal("2026-11-27T10:00:00Z", PayPalBillingService.FailedPaymentReference(sample.RootElement, resource, "I-REALSHAPE"));

        // No time in the notice: the event's own id.
        using var bare = System.Text.Json.JsonDocument.Parse("""{ "id": "WH-EVENT-1", "resource": { "id": "I-REALSHAPE" } }""");
        Assert.Equal("WH-EVENT-1", PayPalBillingService.FailedPaymentReference(bare.RootElement, bare.RootElement.GetProperty("resource"), "I-REALSHAPE"));

        // Nothing but the subscription id anywhere: no name at all, rather than the id that matches the sign-up invoice.
        using var none = System.Text.Json.JsonDocument.Parse("""{ "resource": { "id": "I-REALSHAPE" } }""");
        Assert.Equal(string.Empty, PayPalBillingService.FailedPaymentReference(none.RootElement, none.RootElement.GetProperty("resource"), "I-REALSHAPE"));
    }

    [Fact]
    public async Task A_failed_payment_in_PayPals_real_shape_is_recorded_though_the_sign_up_invoice_carries_the_subscription_id()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: true);
        await using var db = testDb.CreateContext();
        var (billing, service) = await SeedAsync(db, "I-REALSHAPE");
        // The sign-up invoice as CreateSubscriptionAsync and the first payment leave it: the subscription id, then the sale.
        var signUp = NewInvoice(billing, "IPRO-2098-000101", 67.80m, issuedAt: DateTime.UtcNow.AddMonths(-1));
        signUp.IsPaid = true;
        signUp.PayPalTransactionId = "I-REALSHAPE, 8AB12345CD678901E";
        db.Add(signUp);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        using var sample = System.Text.Json.JsonDocument.Parse(PayPalFailedSample);
        var reference = PayPalBillingService.FailedPaymentReference(sample.RootElement, sample.RootElement.GetProperty("resource"), "I-REALSHAPE");
        Assert.True(await service.HandleSubscriptionPaymentFailedWebhookAsync("I-REALSHAPE", reference));
        Assert.True(await service.HandleSubscriptionPaymentFailedWebhookAsync("I-REALSHAPE", reference));   // PayPal sends it again

        db.ChangeTracker.Clear();
        var invoices = await db.Set<Invoice>().Where(i => i.BillingId == billing.Id).ToListAsync();
        var marker = Assert.Single(invoices, i => !i.IsPaid);                    // the failure IS recorded, once
        Assert.Equal("PAYPAL_FAILED:2026-11-27T10:00:00Z", marker.PayPalTransactionId);
        Assert.Equal(2, invoices.Count);

        // What went wrong before: with the subscription id as the name, the handler saw a replay and recorded nothing.
        await using var before = await TestDatabase.CreateAsync(applyLedgerGuard: true);
        await using var db2 = before.CreateContext();
        var (billing2, service2) = await SeedAsync(db2, "I-REALSHAPE");
        var signUp2 = NewInvoice(billing2, "IPRO-2098-000102", 67.80m, issuedAt: DateTime.UtcNow.AddMonths(-1));
        signUp2.IsPaid = true;
        signUp2.PayPalTransactionId = "I-REALSHAPE, 8AB12345CD678901E";
        db2.Add(signUp2);
        await db2.SaveChangesAsync();
        db2.ChangeTracker.Clear();
        Assert.True(await service2.HandleSubscriptionPaymentFailedWebhookAsync("I-REALSHAPE", "I-REALSHAPE"));
        Assert.DoesNotContain(await db2.Set<Invoice>().Where(i => i.BillingId == billing2.Id).ToListAsync(), i => !i.IsPaid);
    }

    [Fact]
    public void The_webhook_names_a_failed_payment_with_FailedPaymentReference()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "IPRO.sln"))) dir = System.IO.Path.GetDirectoryName(dir);
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!, "src", "IPRO.Billing", "PayPalBillingService.cs"));
        Assert.Contains("subscriptionId, FailedPaymentReference(document.RootElement, resource, subscriptionId)),", source);
        Assert.DoesNotContain("HandleSubscriptionPaymentFailedWebhookAsync(subscriptionId, transactionId)", source);
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static Invoice NewInvoice(EBilling billing, string number, decimal total, DateTime issuedAt) => new()
    {
        AgentUserId = billing.AgentUserId,
        BillingId = billing.Id,
        InvoiceNumber = number,
        SubTotal = total,
        TaxAmount = 0m,
        TaxRate = 0m,
        TaxRegion = "No tax",
        Total = total,
        Currency = "CAD",
        IssuedAt = issuedAt,
        IsPaid = false
    };

    private static async Task<(EBilling Billing, PayPalBillingService Service)> SeedAsync(IPRODbContext db, string subscriptionId)
    {
        var rule = new BillingRule { PackageName = $"Pkg-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m };
        db.Add(rule);
        await db.SaveChangesAsync();

        var agent = new AgentUser
        {
            UserName = $"wh-{Guid.NewGuid():N}"[..20],
            Email = "wh@example.com",
            FirstName = "Web",
            LastName = "Hook",
            DomainName = $"wh-{Guid.NewGuid():N}"[..24],
            PackageId = rule.Id
            // No Province on purpose: tax resolves to "No tax", so gross == net in these tests.
        };
        db.Add(agent);
        await db.SaveChangesAsync();

        var billing = new EBilling
        {
            AgentUserId = agent.Id,
            BillingRuleId = rule.Id,
            PayPalSubscriptionId = subscriptionId,
            Amount = 67.80m,
            Status = BillingStatus.Active,
            StartDate = DateTime.UtcNow
        };
        db.Add(billing);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = new PayPalBillingService(
            new UnitOfWork(db),
            db,
            new StubHttpClientFactory(),
            new StubEmailService(),
            Options.Create(new PayPalSettings()),
            new ConfigurationBuilder().Build(),
            NullLogger<PayPalBillingService>.Instance);

        return (billing, service);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StubEmailService : IEmailService
    {
        public Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) => Task.FromResult(true);
        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) => Task.FromResult(EmailSendResult.Sent());
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
    }
}
