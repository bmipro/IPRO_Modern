using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// Security + drip wave (2026-08-26): H4 (SSRF rebinding -> connect-time pinning), M17 (IPv6
// transition prefixes), M18 (orphan report's blind containers), M11 (null dispatch advanced
// enrollments), M12 (consent cancels without CancelledAt; unbounded sweep). Defect tests observed
// RED on pre-fix code; the pinned handler and the batch limit are new enforcement surfaces whose
// contracts are pinned here (the H14/SendAttempts precedent).
public class SecurityDripWaveTests
{
    // ---- M17: every IPv4-embedding transition prefix is unwrapped ----------------------------

    [Theory]
    [InlineData("::127.0.0.1")]                            // IPv4-compatible loopback
    [InlineData("::7f00:1")]                               // same address, hex spelling
    [InlineData("::a00:1")]                                // IPv4-compatible 10.0.0.1
    [InlineData("64:ff9b::7f00:1")]                        // NAT64 loopback
    [InlineData("64:ff9b::a9fe:a9fe")]                     // NAT64 169.254.169.254 (metadata!)
    [InlineData("64:ff9b:1::c0a8:101")]                    // NAT64 local-use 192.168.1.1
    [InlineData("2002:7f00:1::")]                          // 6to4 loopback
    [InlineData("2002:a9fe:a9fe::")]                       // 6to4 metadata endpoint
    [InlineData("2001:0:a9fe:a9fe::")]                     // Teredo, private SERVER v4
    [InlineData("2001:0:203:405:0:0:80ff:fffe")]           // Teredo, client v4 = ~(80ff:fffe) = 127.0.0.1
    public void M17_transition_prefixes_carrying_private_ipv4_are_blocked(string literal)
    {
        // Pre-fix only ::ffff: was unwrapped -- each of these smuggled the same private target
        // past the guard wearing a different IPv6 coat.
        Assert.True(PublicHostGuard.IsBlockedAddress(IPAddress.Parse(literal)), literal);
    }

    [Theory]
    [InlineData("64:ff9b::808:808")]                       // NAT64 8.8.8.8 -- public stays public
    [InlineData("2002:808:808::")]                         // 6to4 8.8.8.8
    [InlineData("2607:f8b0:4004:c07::71")]                 // ordinary public IPv6
    [InlineData("8.8.8.8")]
    public void M17_public_addresses_stay_allowed(string literal)
    {
        Assert.False(PublicHostGuard.IsBlockedAddress(IPAddress.Parse(literal)), literal);
    }

    // ---- H4: the connection itself is the guard ----------------------------------------------

    [Fact]
    public void H4_a_resolve_containing_any_blocked_address_refuses_the_connection_whole()
    {
        // Mixed public+private answers ARE the rebinding smell -- fail closed, never "pick the
        // public one" (the attacker controls the order and the TTL).
        var mixed = new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.1") };
        var ex = Record.Exception(() => PublicHostGuard.FilterForConnect("evil.test", mixed));
        Assert.IsType<HttpRequestException>(ex);
        Assert.Contains("private or internal", ex!.Message);

        var clean = new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("1.1.1.1") };
        Assert.Equal(clean, PublicHostGuard.FilterForConnect("fine.test", clean));

        Assert.IsType<HttpRequestException>(Record.Exception(() =>
            PublicHostGuard.FilterForConnect("ghost.test", Array.Empty<IPAddress>())));
    }

    [Fact]
    public async Task H4_the_pinned_handler_refuses_a_name_that_resolves_private_at_connect_time()
    {
        // The rebinding shape end-to-end: whatever any EARLIER check concluded, the resolve the
        // CONNECTION uses comes back private -- and the pinned handler refuses to dial it. This
        // is the atomic resolve-validate-connect that leaves no second resolve to win.
        var original = PublicHostGuard.ResolveHook;
        try
        {
            PublicHostGuard.ResolveHook = (_, _) => Task.FromResult(new[] { IPAddress.Parse("127.0.0.1") });
            using var client = new HttpClient(PublicHostGuard.CreatePinnedHandler()) { Timeout = TimeSpan.FromSeconds(5) };
            var ex = await Record.ExceptionAsync(() => client.GetAsync("http://rebound-to-loopback.test/"));
            Assert.NotNull(ex);
            Assert.Contains("private or internal", FlattenMessages(ex!));
        }
        finally
        {
            PublicHostGuard.ResolveHook = original;
        }
    }

    [Fact]
    public async Task H4_the_pinned_handler_refuses_blocked_ip_literals_too()
    {
        using var client = new HttpClient(PublicHostGuard.CreatePinnedHandler()) { Timeout = TimeSpan.FromSeconds(5) };
        var ex = await Record.ExceptionAsync(() => client.GetAsync("http://169.254.169.254/latest/meta-data/"));
        Assert.NotNull(ex);
        Assert.Contains("private or internal", FlattenMessages(ex!));
    }

    private static string FlattenMessages(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null; e = e.InnerException) parts.Add(e.Message);
        return string.Join(" | ", parts);
    }

    // ---- M18: the orphan report can no longer be blind to a container the app uploads to -----

    [Fact]
    public void M18_every_container_the_code_uploads_to_is_enumerated_by_the_registry()
    {
        // Source-walking guard (the CheckoutHostPreservationTests pattern): every
        // `...Container = "name"` constant in src/ must appear in BlobReferences.Containers, or
        // the orphan report silently never scans it -- exactly how ecard-art and starter-content
        // stayed invisible.
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var pattern = new System.Text.RegularExpressions.Regex(
            "Container\\s*=\\s*\"(?<name>[a-z0-9-]+)\"");
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in System.IO.Directory.EnumerateFiles(
                     System.IO.Path.Combine(dir!.FullName, "src"), "*.cs", System.IO.SearchOption.AllDirectories))
        {
            foreach (System.Text.RegularExpressions.Match m in pattern.Matches(System.IO.File.ReadAllText(file)))
            {
                declared.Add(m.Groups["name"].Value);
            }
        }
        Assert.NotEmpty(declared);
        var enumerated = new HashSet<string>(BlobReferences.Containers.Select(c => c.Container), StringComparer.OrdinalIgnoreCase);
        var missing = declared.Where(d => !enumerated.Contains(d)).ToList();
        Assert.True(missing.Count == 0,
            $"container(s) the code uploads to but the orphan report never scans: {string.Join(", ", missing)}");
    }

    // ---- M11: a null dispatch never advances the enrollment ----------------------------------

    [Fact]
    public async Task M11_a_null_dispatch_neither_advances_nor_stamps_the_enrollment()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedDripAsync(db, suppressed: false);

        var job = new IPRO.Scheduler.DripCampaignJob(
            new UnitOfWork(db), db,
            new NullDispatcher(db),
            new PassthroughConsent(), NullLogger<IPRO.Scheduler.DripCampaignJob>.Instance);
        await job.RunAsync();
        db.ChangeTracker.Clear();

        // Pre-fix a null fell into the SUCCESS path: LastSentAt stamped for mail that never went
        // out, and the index advanced past the step. Now it is a bounded transient failure.
        var after = await db.DripCampaignEnrollments.AsNoTracking().SingleAsync(e => e.Id == seed.EnrollmentId);
        Assert.Null(after.LastSentAt);
        Assert.Equal(0, after.NextStepIndex);
        Assert.Equal(DripCampaignEnrollmentStatus.Active, after.Status);
        Assert.Equal(1, after.SendAttempts);
        Assert.True(after.NextSendAt > DateTime.UtcNow, "backoff must move it off the batch head");
    }

    // ---- M12: consent cancels answer "when did we stop mailing this person" ------------------

    [Fact]
    public async Task M12_the_sweep_stamps_cancelled_at_and_honors_its_batch_limit()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var a = await SeedDripAsync(db, suppressed: true);
        var b = await SeedDripAsync(db, suppressed: true);
        var c = await SeedDripAsync(db, suppressed: true);

        var consent = new IPRO.Business.Services.EmailConsentService(
            db, new ConfigurationBuilder().Build(),
            NullLogger<IPRO.Business.Services.EmailConsentService>.Instance,
            Array.Empty<IPRO.Business.Services.IUnsubscribeNotifier>());

        // Bounded: the sweep runs every hourly tick; the remainder is next tick's work.
        Assert.Equal(2, await consent.CancelSuppressedDripEnrollmentsAsync(batchLimit: 2));
        db.ChangeTracker.Clear();
        Assert.Equal(1, await consent.CancelSuppressedDripEnrollmentsAsync(batchLimit: 2));
        db.ChangeTracker.Clear();

        // Pre-fix the rows went Cancelled with CancelledAt NULL -- the CASL "when did we stop
        // mailing this person" question had no answer, on any of the three cancel paths.
        foreach (var id in new[] { a.EnrollmentId, b.EnrollmentId, c.EnrollmentId })
        {
            var row = await db.DripCampaignEnrollments.AsNoTracking().SingleAsync(e => e.Id == id);
            Assert.Equal(DripCampaignEnrollmentStatus.Cancelled, row.Status);
            Assert.NotNull(row.CancelledAt);
        }
    }

    [Fact]
    public async Task M12_suppress_all_stamps_cancelled_at_on_the_enrollments_it_cancels()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedDripAsync(db, suppressed: false);

        var consent = new IPRO.Business.Services.EmailConsentService(
            db, new ConfigurationBuilder().Build(),
            NullLogger<IPRO.Business.Services.EmailConsentService>.Instance,
            Array.Empty<IPRO.Business.Services.IUnsubscribeNotifier>());
        var client = await db.Clients.SingleAsync(cl => cl.Id == seed.ClientId);
        await consent.SuppressAllAsync(client, "test-unsubscribe");
        db.ChangeTracker.Clear();

        var row = await db.DripCampaignEnrollments.AsNoTracking().SingleAsync(e => e.Id == seed.EnrollmentId);
        Assert.Equal(DripCampaignEnrollmentStatus.Cancelled, row.Status);
        Assert.NotNull(row.CancelledAt);
    }

    // ------------------------------------------------------------------------------ plumbing --

    private sealed record DripSeed(int AgentId, int ClientId, int EnrollmentId);

    private static async Task<DripSeed> SeedDripAsync(IPRODbContext db, bool suppressed)
    {
        var rule = new BillingRule { PackageName = $"SD-{Guid.NewGuid():N}"[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"sd-{Guid.NewGuid():N}"[..20],
            Email = $"sd-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Sec", LastName = "Drip",
            DomainName = $"sd-{Guid.NewGuid():N}"[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var campaign = new DripCampaign { AgentUserId = agent.Id, Name = "SD", IsActive = true };
        db.Add(campaign);
        await db.SaveChangesAsync();
        db.Add(new DripCampaignStep { DripCampaignId = campaign.Id, SortOrder = 0, Subject = "S1", HtmlBody = "<p>x</p>", DelayDays = 7 });
        var client = new Client
        {
            AgentUserId = agent.Id,
            FirstName = "C", LastName = "D",
            Email = $"sd-{Guid.NewGuid():N}"[..14] + "@example.test",
            IsNewsletterSubscribed = !suppressed,
            EmailOptOutAt = suppressed ? DateTime.UtcNow.AddDays(-3) : null
        };
        db.Add(client);
        await db.SaveChangesAsync();
        var enrollment = new DripCampaignEnrollment
        {
            AgentUserId = agent.Id,
            DripCampaignId = campaign.Id,
            ClientId = client.Id,
            Status = DripCampaignEnrollmentStatus.Active,
            NextStepIndex = 0,
            StartedAt = DateTime.UtcNow.AddDays(-1),
            NextSendAt = DateTime.UtcNow.AddMinutes(-30),
            UnsubscribeToken = Guid.NewGuid().ToString("N")
        };
        db.Add(enrollment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new DripSeed(agent.Id, client.Id, enrollment.Id);
    }

    /// The M11 race, made deterministic: the dispatcher's own re-read finds nothing to send.
    private sealed class NullDispatcher : NewsLetterDispatcher
    {
        public NullDispatcher(IPRODbContext db) : base(
            new UnitOfWork(db), db, new StubEmail(), new ConfigurationBuilder().Build(),
            NullLogger<NewsLetterDispatcher>.Instance)
        { }

        public override Task<EmailSendResult?> DispatchDripStepAsync(int campaignId, int stepIndex, string toEmail, string toName, string? unsubscribeToken = null, int enrollmentId = 0)
            => Task.FromResult<EmailSendResult?>(null);
    }

    private sealed class PassthroughConsent : IPRO.Business.Services.IEmailConsentService
    {
        public bool IsSuppressed(Client client, IPRO.Business.Services.EmailChannel channel, bool designSurvivesOptOut = false) => false;
        public Task<IPRO.Business.Services.SuppressionResult> SuppressAllAsync(Client client, string source) => throw new NotSupportedException();
        public Task ResubscribeAsync(Client client) => throw new NotSupportedException();
        public Task<int> CancelSuppressedDripEnrollmentsAsync(int batchLimit = 500) => Task.FromResult(0);
        public Task<string> GetOrCreateTokenAsync(Client client) => Task.FromResult("tok");
        public string BuildPreferencesUrl(string token) => $"https://example.test/prefs/{token}";
    }

    private sealed class StubEmail : IEmailService
    {
        public Task<bool> SendAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(true);
        public Task<EmailSendResult> SendDetailedAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(EmailSendResult.Sent());
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
    }
}
