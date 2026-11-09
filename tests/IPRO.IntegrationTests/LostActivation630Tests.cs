using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using IPRO.Billing;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// 630 (2026-11-09). Two PayPal gaps.
// (1) A checkout the buyer finished at PayPal whose activation never reached iPro (the buyer closed the tab
//     instead of coming back, and PayPal's ACTIVATED notice was lost) sat Pending for good: PayPal billed,
//     the agent had no access, a "payment pending" email went out after 24 hours, and the 48-hour sweep only
//     logged it. The hourly job now asks PayPal and activates it.
// (2) Every PayPal call fetched a new access token first. One token now serves the app until shortly
//     before it expires.
public class LostActivation630Tests
{
    [Fact]
    public async Task A_checkout_PayPal_reports_active_is_activated_once_an_hour_has_passed()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedCheckoutAsync(db, "I-630LOST", createdHoursAgo: 2);
        var paypal = new ScriptedPayPal();
        paypal.Status["I-630LOST"] = "ACTIVE";

        var activated = await NewService(db, paypal).CatchUpLostActivationsAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(1, activated);
        var billing = await db.Billings.AsNoTracking().SingleAsync(b => b.Id == seed.BillingId);
        Assert.Equal(BillingStatus.Active, billing.Status);
        var change = await db.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.Id == seed.ChangeId);
        Assert.Equal(SubscriptionChangeStatus.Applied, change.Status);
        Assert.Equal(seed.RuleId, (await db.AgentUsers.AsNoTracking().SingleAsync(a => a.Id == seed.AgentId)).PackageId);
        // Access now; the money is still recorded only when PayPal's sale notice confirms it, as on the
        // ACTIVATED notice's own path.
        Assert.False((await db.Invoices.AsNoTracking().SingleAsync(i => i.BillingId == seed.BillingId)).IsPaid);
        Assert.Empty(paypal.Cancelled);
    }

    [Fact]
    public async Task A_checkout_under_an_hour_old_or_over_30_days_old_is_not_asked_about()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var young = await SeedCheckoutAsync(db, "I-630YOUNG", createdHoursAgo: 0.25);
        var old = await SeedCheckoutAsync(db, "I-630OLD", createdHoursAgo: 31 * 24);
        var paypal = new ScriptedPayPal();
        paypal.Status["I-630YOUNG"] = "ACTIVE";
        paypal.Status["I-630OLD"] = "ACTIVE";

        Assert.Equal(0, await NewService(db, paypal).CatchUpLostActivationsAsync());
        db.ChangeTracker.Clear();

        Assert.Empty(paypal.Asked);
        Assert.Equal(BillingStatus.Pending, (await db.Billings.AsNoTracking().SingleAsync(b => b.Id == young.BillingId)).Status);
        Assert.Equal(BillingStatus.Pending, (await db.Billings.AsNoTracking().SingleAsync(b => b.Id == old.BillingId)).Status);
    }

    [Fact]
    public async Task Any_answer_but_active_leaves_the_checkout_alone_and_one_failure_does_not_stop_the_rest()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var paypal = new ScriptedPayPal();
        var untouched = new List<int>();
        foreach (var (id, status) in new[]
                 {
                     ("I-630WAIT", "APPROVAL_PENDING"), ("I-630APPR", "APPROVED"), ("I-630GONE", "CANCELLED"),
                     ("I-630NONE", (string?)null), ("I-630BOOM", "THROW")
                 })
        {
            untouched.Add((await SeedCheckoutAsync(db, id, createdHoursAgo: 3)).BillingId);
            if (status != null) paypal.Status[id] = status;
        }
        var last = await SeedCheckoutAsync(db, "I-630LAST", createdHoursAgo: 3);
        paypal.Status["I-630LAST"] = "ACTIVE";

        Assert.Equal(1, await NewService(db, paypal).CatchUpLostActivationsAsync());
        db.ChangeTracker.Clear();

        foreach (var billingId in untouched)
        {
            Assert.Equal(BillingStatus.Pending, (await db.Billings.AsNoTracking().SingleAsync(b => b.Id == billingId)).Status);
        }
        Assert.Equal(BillingStatus.Active, (await db.Billings.AsNoTracking().SingleAsync(b => b.Id == last.BillingId)).Status);
        Assert.Empty(paypal.Cancelled);
    }

    [Fact]
    public async Task An_agent_whose_newer_plan_is_already_active_is_left_for_a_person()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedCheckoutAsync(db, "I-630STALE", createdHoursAgo: 5);
        var newer = await AddBillingAsync(db, seed.AgentId, seed.RuleId, "I-630CURRENT", BillingStatus.Active, createdHoursAgo: 1);
        var paypal = new ScriptedPayPal();
        paypal.Status["I-630STALE"] = "ACTIVE";
        paypal.Status["I-630CURRENT"] = "ACTIVE";

        Assert.Equal(0, await NewService(db, paypal).CatchUpLostActivationsAsync());
        db.ChangeTracker.Clear();

        // Activating the old checkout would cancel the plan the agent is on now.
        Assert.Equal(BillingStatus.Pending, (await db.Billings.AsNoTracking().SingleAsync(b => b.Id == seed.BillingId)).Status);
        Assert.Equal(BillingStatus.Active, (await db.Billings.AsNoTracking().SingleAsync(b => b.Id == newer)).Status);
        Assert.Empty(paypal.Cancelled);
    }

    [Fact]
    public async Task A_lost_upgrade_replaces_the_older_plan_as_the_lost_notice_would_have()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var rule = await SeedRuleAsync(db);
        var agentId = await SeedAgentAsync(db, rule);
        var older = await AddBillingAsync(db, agentId, rule, "I-630BEFORE", BillingStatus.Active, createdHoursAgo: 24 * 60);
        var seed = await SeedCheckoutAsync(db, "I-630UPGRADE", createdHoursAgo: 2, agentId: agentId, ruleId: rule);
        var paypal = new ScriptedPayPal();
        paypal.Status["I-630UPGRADE"] = "ACTIVE";

        Assert.Equal(1, await NewService(db, paypal).CatchUpLostActivationsAsync());
        db.ChangeTracker.Clear();

        Assert.Equal(BillingStatus.Active, (await db.Billings.AsNoTracking().SingleAsync(b => b.Id == seed.BillingId)).Status);
        Assert.Equal(BillingStatus.Cancelled, (await db.Billings.AsNoTracking().SingleAsync(b => b.Id == older)).Status);
        Assert.Equal(new[] { "I-630BEFORE" }, paypal.Cancelled.ToArray());
    }

    [Fact]
    public async Task Without_PayPal_settings_nothing_is_asked()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedCheckoutAsync(db, "I-630NOSET", createdHoursAgo: 2);
        var paypal = new ScriptedPayPal();
        paypal.Status["I-630NOSET"] = "ACTIVE";

        var service = new PayPalBillingService(new UnitOfWork(db), db, paypal, new StubEmailService(),
            Options.Create(new PayPalSettings()), new ConfigurationBuilder().Build(), NullLogger<PayPalBillingService>.Instance);
        Assert.Equal(0, await service.CatchUpLostActivationsAsync());
        Assert.Empty(paypal.Asked);
        Assert.Equal(0, paypal.TokenRequests);
    }

    // ---- the access token -------------------------------------------------------------------------

    [Fact]
    public async Task One_token_serves_a_whole_run_and_the_app_until_shortly_before_it_expires()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        foreach (var id in new[] { "I-630T1", "I-630T2", "I-630T3" })
        {
            await SeedCheckoutAsync(db, id, createdHoursAgo: 2);
        }
        var paypal = new ScriptedPayPal { ExpiresIn = 3600 };
        foreach (var id in new[] { "I-630T1", "I-630T2", "I-630T3" }) paypal.Status[id] = "APPROVAL_PENDING";
        var clock = DateTime.UtcNow;
        var shared = new PayPalAccessTokens(() => clock);

        await NewService(db, paypal, shared).CatchUpLostActivationsAsync();
        Assert.Equal(3, paypal.Asked.Count);
        Assert.Equal(1, paypal.TokenRequests);                       // was one per subscription

        await NewService(db, paypal, shared).CatchUpLostActivationsAsync();   // another request, same app
        Assert.Equal(1, paypal.TokenRequests);

        clock = clock.AddSeconds(3600) - PayPalAccessTokens.RenewBefore;     // the renewal point
        await NewService(db, paypal, shared).CatchUpLostActivationsAsync();
        Assert.Equal(2, paypal.TokenRequests);
    }

    [Fact]
    public async Task A_token_is_kept_per_PayPal_address_and_client_and_a_short_lived_one_is_not_kept_long()
    {
        var clock = DateTime.UtcNow;
        var tokens = new PayPalAccessTokens(() => clock);
        var fetches = 0;
        Task<(string, int)> Fetch(string token, int expiresIn) { fetches++; return Task.FromResult((token, expiresIn)); }

        Assert.Equal("sandbox-1", await tokens.GetAsync("https://api-m.sandbox.paypal.com|abc", () => Fetch("sandbox-1", 32400)));
        Assert.Equal("sandbox-1", await tokens.GetAsync("https://api-m.sandbox.paypal.com|abc", () => Fetch("unused", 32400)));
        Assert.Equal("live-1", await tokens.GetAsync("https://api-m.paypal.com|abc", () => Fetch("live-1", 32400)));
        Assert.Equal("other-client", await tokens.GetAsync("https://api-m.paypal.com|xyz", () => Fetch("other-client", 32400)));
        Assert.Equal(3, fetches);                                     // a different key never reuses the other's token

        // No expires_in: used once, asked again next time.
        Assert.Equal("bare", await tokens.GetAsync("k", () => Fetch("bare", 0)));
        Assert.Equal("bare-2", await tokens.GetAsync("k", () => Fetch("bare-2", 0)));
        // A two-minute token is kept for a minute at most.
        Assert.Equal("short", await tokens.GetAsync("k", () => Fetch("short", 120)));
        Assert.Equal("short", await tokens.GetAsync("k", () => Fetch("other", 120)));
        clock = clock.AddSeconds(60);
        Assert.Equal("after", await tokens.GetAsync("k", () => Fetch("after", 32400)));
        // An empty answer is never kept.
        Assert.Equal("", await tokens.GetAsync("e", () => Fetch("", 32400)));
        Assert.Equal("filled", await tokens.GetAsync("e", () => Fetch("filled", 32400)));
    }

    [Fact]
    public async Task Callers_at_the_same_moment_share_one_token_request()
    {
        var tokens = new PayPalAccessTokens();
        var fetches = 0;
        var gate = new TaskCompletionSource();
        async Task<(string, int)> SlowFetch() { System.Threading.Interlocked.Increment(ref fetches); await gate.Task; return ("one", 32400); }

        var callers = Enumerable.Range(0, 8).Select(_ => tokens.GetAsync("k", SlowFetch)).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(callers);

        Assert.All(results, t => Assert.Equal("one", t));
        Assert.Equal(1, fetches);
    }

    [Fact]
    public void Both_apps_register_one_shared_token_and_the_service_reads_it()
    {
        foreach (var program in new[] { @"src\IPRO.Web\Program.cs", @"src\IPRO.Admin\Program.cs" })
        {
            Assert.Contains("builder.Services.AddSingleton<PayPalAccessTokens>();", System.IO.File.ReadAllText(RepoFile(program)));
        }
        var service = System.IO.File.ReadAllText(RepoFile(@"src\IPRO.Billing\PayPalBillingService.cs"));
        Assert.Contains("_accessTokens.GetAsync(", service);
        // One door to PayPal's token address, and only the cache calls it.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(service, @"/v1/oauth2/token"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(service, @"RequestPayPalAccessTokenAsync\(\)"));
    }

    // ------------------------------------------------------------------------------ plumbing --

    private sealed record Seed(int AgentId, int RuleId, int BillingId, int ChangeId);

    private static PayPalBillingService NewService(IPRODbContext db, ScriptedPayPal paypal, PayPalAccessTokens? tokens = null) => new(
        new UnitOfWork(db), db, paypal, new StubEmailService(),
        Options.Create(new PayPalSettings { ClientId = "test-client", ClientSecret = "test-secret" }),
        new ConfigurationBuilder().Build(),
        NullLogger<PayPalBillingService>.Instance,
        tokens);

    private static async Task<int> SeedRuleAsync(IPRODbContext db)
    {
        var rule = new BillingRule
        {
            PackageName = $"L630-{Guid.NewGuid():N}"[..20],
            MonthlyPrice = 50m, AnnualPrice = 500m, SetupFee = 0m, IsActive = true,
            PayPalMonthlyPlanId = $"P-{Guid.NewGuid():N}"[..12], PayPalAnnualPlanId = $"P-{Guid.NewGuid():N}"[..12]
        };
        db.Add(rule);
        await db.SaveChangesAsync();
        return rule.Id;
    }

    private static async Task<int> SeedAgentAsync(IPRODbContext db, int packageId)
    {
        var agent = new AgentUser
        {
            UserName = $"l630-{Guid.NewGuid():N}"[..20],
            Email = $"l630-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Lost", LastName = "Activation",
            DomainName = $"l630-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario", PackageId = packageId
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<int> AddBillingAsync(IPRODbContext db, int agentId, int ruleId, string subscriptionId, BillingStatus status, double createdHoursAgo)
    {
        var billing = new IPRO.Entities.Billing
        {
            AgentUserId = agentId,
            BillingRuleId = ruleId,
            PayPalSubscriptionId = subscriptionId,
            Amount = 50m,
            Currency = "CAD",
            Status = status,
            Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddHours(-createdHoursAgo),
            NextBillingDate = DateTime.UtcNow.AddHours(-createdHoursAgo).AddMonths(1),
            CreatedAt = DateTime.UtcNow.AddHours(-createdHoursAgo)
        };
        db.Add(billing);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return billing.Id;
    }

    // A sign-up checkout as SubscribeAsync leaves it: a Pending billing with its PayPal subscription,
    // a Pending Subscribe change and an unpaid invoice.
    private static async Task<Seed> SeedCheckoutAsync(IPRODbContext db, string subscriptionId, double createdHoursAgo, int? agentId = null, int? ruleId = null)
    {
        var rule = ruleId ?? await SeedRuleAsync(db);
        var agent = agentId ?? await SeedAgentAsync(db, await SeedRuleAsync(db));   // on another package until activated
        var billingId = await AddBillingAsync(db, agent, rule, subscriptionId, BillingStatus.Pending, createdHoursAgo);
        var change = new SubscriptionChange
        {
            AgentUserId = agent,
            BillingId = billingId,
            CurrentBillingRuleId = null,
            RequestedBillingRuleId = rule,
            ChangeType = SubscriptionChangeType.Subscribe,
            Status = SubscriptionChangeStatus.Pending,
            ProratedCredit = 0m,
            Period = BillingPeriod.Monthly,
            EffectiveDate = DateTime.UtcNow.AddHours(-createdHoursAgo),
            CreatedAt = DateTime.UtcNow.AddHours(-createdHoursAgo)
        };
        db.Add(change);
        db.Add(new Invoice
        {
            BillingId = billingId, AgentUserId = agent,
            InvoiceNumber = $"L630-{billingId}", SubTotal = 50m, TaxRate = 0.13m, TaxAmount = 6.50m, Total = 56.50m,
            TaxRegion = "ON 13% HST",
            PayPalTransactionId = subscriptionId,
            IssuedAt = DateTime.UtcNow.AddHours(-createdHoursAgo),
            IsPaid = false
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seed(agent, rule, billingId, change.Id);
    }

    private static string RepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "IPRO.sln")))
            dir = System.IO.Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return System.IO.Path.Combine(dir!, relative);
    }

    // PayPal as these tests need it: a token, a subscription's status (or no answer, or a thrown error),
    // and cancel. Records what was asked.
    private sealed class ScriptedPayPal : IHttpClientFactory
    {
        public readonly Dictionary<string, string> Status = new();
        public readonly ConcurrentQueue<string> Asked = new();
        public readonly ConcurrentQueue<string> Cancelled = new();
        public int ExpiresIn = 32400;
        public int TokenRequests;
        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler : HttpMessageHandler
        {
            private readonly ScriptedPayPal _owner;
            public Handler(ScriptedPayPal owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken ct)
            {
                var url = request.RequestUri!.AbsoluteUri;
                if (url.Contains("/v1/oauth2/token"))
                {
                    System.Threading.Interlocked.Increment(ref _owner.TokenRequests);
                    return Task.FromResult(Json($"{{\"access_token\":\"token-{_owner.TokenRequests}\",\"expires_in\":{_owner.ExpiresIn}}}"));
                }
                if (request.Method == HttpMethod.Post && url.EndsWith("/cancel"))
                {
                    _owner.Cancelled.Enqueue(url.Split('/')[^2]);
                    return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
                }
                if (request.Method == HttpMethod.Get && url.Contains("/v1/billing/subscriptions/"))
                {
                    var id = url.Split('/')[^1];
                    _owner.Asked.Enqueue(id);
                    if (_owner.Status.TryGetValue(id, out var status))
                    {
                        if (status == "THROW") throw new HttpRequestException("scripted outage");
                        return Task.FromResult(Json($"{{\"status\":\"{status}\"}}"));
                    }
                }
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            }

            private static HttpResponseMessage Json(string body) => new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StubEmailService : IPRO.Email.IEmailService
    {
        public Task<bool> SendAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(true);
        public Task<IPRO.Email.EmailSendResult> SendDetailedAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(IPRO.Email.EmailSendResult.Sent());
        public Task<bool> SendBulkAsync(IEnumerable<IPRO.Email.EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
    }
}
