using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// Phase 1 of the launch runway (2026-08-27): H8 — the SendGrid webhook swallowed unsubscribes and
// spam complaints, the one event class with legal weight; M8 — ADMIN-7's revalidation shipped in
// Admin only, so deactivating an AGENT left their 8-hour sliding session alive, including through
// an erasure. Every defect test here was observed RED against the pre-fix code.
public class ConsentAndSessionTests
{
    // ---- H8: a consent event that cannot be processed must NOT be acknowledged ---------------

    [Theory]
    [InlineData("unsubscribe")]
    [InlineData("group_unsubscribe")]
    [InlineData("spamreport")]
    public void H8_consent_bearing_events_are_recognised(string ev)
    {
        // The classifier the webhook branches on. These three are the instruction "stop mailing
        // this person" — in law, not just in product.
        Assert.True(IPRO.Web.Controllers.NewsletterController.IsConsentEvent(ev));
        Assert.True(IPRO.Web.Controllers.NewsletterController.IsConsentEvent(ev.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("delivered")]
    [InlineData("open")]
    [InlineData("click")]
    [InlineData("bounce")]
    [InlineData("dropped")]
    [InlineData("deferred")]
    [InlineData("processed")]
    [InlineData("")]
    public void H8_ordinary_delivery_events_are_not_consent_events(string ev)
    {
        // These keep the JOBS-6 behaviour exactly: one bad event sinks alone and the batch still
        // answers 200. Only consent may hold up the acknowledgement.
        Assert.False(IPRO.Web.Controllers.NewsletterController.IsConsentEvent(ev));
    }

    [Fact]
    public void H8_a_failed_consent_event_is_the_one_thing_that_withholds_the_200()
    {
        // The webhook's contract with SendGrid in one function: 200 means "recorded, never send
        // it again". Pre-fix EVERY failure answered 200 — including a database hiccup while
        // suppressing an unsubscribe, which SendGrid then never retried and nobody could recover.
        // A retry duplicates delivery statistics; that trade is deliberate and stated in the
        // register: recoverable duplicate stats beat an unrecoverable lost opt-out.
        Assert.True(IPRO.Web.Controllers.NewsletterController.ShouldAcknowledge(consentEventFailed: false));
        Assert.False(IPRO.Web.Controllers.NewsletterController.ShouldAcknowledge(consentEventFailed: true));
    }

    // ---- H8 end-to-end: the WIRING, not just the classifier -----------------------------------
    //
    // The C1 lesson, applied before it can bite: a correct guard the caller never consults is worth
    // nothing. These two drive the REAL SendGridEvents action — genuine ECDSA signature, genuine
    // JSON batch, genuine database failure underneath — and read the status code the action
    // actually returns.

    [Fact]
    public async Task H8_a_failed_unsubscribe_withholds_the_acknowledgement_end_to_end()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var recipientId = await SeedNewsletterRecipientAsync(db);

        // Break the table every suppression path must write, so recording this unsubscribe throws
        // from inside the per-event guard — a stand-in for the database hiccup the register
        // describes, made deterministic.
        await db.Database.ExecuteSqlRawAsync("RENAME TABLE `Clients` TO `Clients_h8_gone`");
        try
        {
            var result = await PostEventsAsync(db, "unsubscribe", recipientId);

            // Pre-fix: 200 — SendGrid marks it delivered and never sends it again, and the opt-out
            // is gone with no way to recover it.
            var status = Assert.IsType<Microsoft.AspNetCore.Mvc.StatusCodeResult>(result);
            Assert.Equal(503, status.StatusCode);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("RENAME TABLE `Clients_h8_gone` TO `Clients`");
        }
    }

    [Fact]
    public async Task H8_a_failed_delivery_event_still_acknowledges_the_batch()
    {
        // The JOBS-6 guarantee, unchanged: one bad statistic must not make SendGrid redeliver the
        // whole batch. Only consent gets that power.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var recipientId = await SeedNewsletterRecipientAsync(db);

        await db.Database.ExecuteSqlRawAsync("RENAME TABLE `Clients` TO `Clients_h8_gone2`");
        try
        {
            var result = await PostEventsAsync(db, "open", recipientId);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkResult>(result);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("RENAME TABLE `Clients_h8_gone2` TO `Clients`");
        }
    }

    // ---- M8: an agent's session dies with their account ---------------------------------------

    [Fact]
    public async Task M8_a_deactivated_agents_live_session_is_rejected()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, active: false);

        var verdict = await IPRO.Web.Infrastructure.AgentCookieRevalidator.EvaluateAsync(db, agentId.ToString(), null);

        // Pre-fix IPRO.Web had no ValidatePrincipal at all: the 8-hour sliding cookie kept full
        // portal access until it expired, no matter what the database said.
        Assert.Equal(IPRO.Web.Infrastructure.AgentCookieRevalidator.Verdict.Reject, verdict);
    }

    [Fact]
    public async Task M8_a_deleted_agents_live_session_is_rejected()
    {
        // The erasure case the register calls out by name: rows shredded, cookie still walking
        // around the portal.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var verdict = await IPRO.Web.Infrastructure.AgentCookieRevalidator.EvaluateAsync(db, "999999999", null);
        Assert.Equal(IPRO.Web.Infrastructure.AgentCookieRevalidator.Verdict.Reject, verdict);
    }

    [Fact]
    public async Task M8_an_active_agent_keeps_working()
    {
        // The revalidator runs on EVERY authenticated request. If it were wrong in this direction
        // it would sign the whole customer base out.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, active: true);

        var verdict = await IPRO.Web.Infrastructure.AgentCookieRevalidator.EvaluateAsync(db, agentId.ToString(), null);
        Assert.Equal(IPRO.Web.Infrastructure.AgentCookieRevalidator.Verdict.Ok, verdict);
    }

    [Fact]
    public async Task M8_a_deactivated_team_member_loses_access_even_though_the_agent_is_fine()
    {
        // A team member signs in as themselves and acts AS the agent: NameIdentifier is the
        // AGENT's id, with a TeamMemberId marker claim. Checking only the agent would leave a
        // revoked assistant with a live session on a perfectly healthy account.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, active: true);
        var member = new TeamMember
        {
            AgentUserId = agentId,
            FullName = "Revoked Assistant",
            Email = $"tm-{Guid.NewGuid():N}"[..12] + "@example.test",
            IsActive = false
        };
        db.Add(member);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var verdict = await IPRO.Web.Infrastructure.AgentCookieRevalidator.EvaluateAsync(
            db, agentId.ToString(), member.Id.ToString());
        Assert.Equal(IPRO.Web.Infrastructure.AgentCookieRevalidator.Verdict.Reject, verdict);
    }

    [Fact]
    public async Task M8_an_active_team_member_keeps_working()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, active: true);
        var member = new TeamMember
        {
            AgentUserId = agentId,
            FullName = "Working Assistant",
            Email = $"tm-{Guid.NewGuid():N}"[..12] + "@example.test",
            IsActive = true
        };
        db.Add(member);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var verdict = await IPRO.Web.Infrastructure.AgentCookieRevalidator.EvaluateAsync(
            db, agentId.ToString(), member.Id.ToString());
        Assert.Equal(IPRO.Web.Infrastructure.AgentCookieRevalidator.Verdict.Ok, verdict);
    }

    [Fact]
    public async Task M8_a_team_member_of_a_deactivated_agent_is_rejected()
    {
        // Deactivating the agent must take their whole team with them.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, active: false);
        var member = new TeamMember
        {
            AgentUserId = agentId,
            FullName = "Orphan Assistant",
            Email = $"tm-{Guid.NewGuid():N}"[..12] + "@example.test",
            IsActive = true
        };
        db.Add(member);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var verdict = await IPRO.Web.Infrastructure.AgentCookieRevalidator.EvaluateAsync(
            db, agentId.ToString(), member.Id.ToString());
        Assert.Equal(IPRO.Web.Infrastructure.AgentCookieRevalidator.Verdict.Reject, verdict);
    }

    [Fact]
    public async Task M8_a_team_member_belonging_to_another_agent_is_rejected()
    {
        // Defensive: the marker claim must not be usable to ride someone else's session.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, active: true);
        var otherAgentId = await SeedAgentAsync(db, active: true);
        var member = new TeamMember
        {
            AgentUserId = otherAgentId,
            FullName = "Wrong Owner",
            Email = $"tm-{Guid.NewGuid():N}"[..12] + "@example.test",
            IsActive = true
        };
        db.Add(member);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var verdict = await IPRO.Web.Infrastructure.AgentCookieRevalidator.EvaluateAsync(
            db, agentId.ToString(), member.Id.ToString());
        Assert.Equal(IPRO.Web.Infrastructure.AgentCookieRevalidator.Verdict.Reject, verdict);
    }

    [Fact]
    public void M8_the_agent_cookie_is_actually_wired_to_the_revalidator()
    {
        // The C1 lesson again: EvaluateAsync could be perfect and every test above green while
        // production never calls it. This walks Program.cs and fails if the agent cookie stops
        // pointing at the revalidator, or the revalidator stops being registered for injection.
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var program = System.IO.File.ReadAllText(
            System.IO.Path.Combine(dir!.FullName, "src", "IPRO.Web", "Program.cs"));

        Assert.Contains("o.EventsType = typeof(IPRO.Web.Infrastructure.AgentCookieRevalidator);", program);
        Assert.Contains("AddScoped<IPRO.Web.Infrastructure.AgentCookieRevalidator>();", program);
    }

    // ------------------------------------------------------------------------------ plumbing --

    /// A newsletter recipient tied to a real client — the shape an unsubscribe event addresses.
    private static async Task<int> SeedNewsletterRecipientAsync(IPRODbContext db)
    {
        var agentId = await SeedAgentAsync(db, active: true);
        var client = new Client
        {
            AgentUserId = agentId,
            FirstName = "Opt", LastName = "Out",
            Email = $"oo-{Guid.NewGuid():N}"[..14] + "@example.test",
            IsNewsletterSubscribed = true
        };
        db.Add(client);
        var nl = new NewsLetter { AgentUserId = agentId, Subject = "H8", HtmlBody = "<p>x</p>" };
        db.Add(nl);
        await db.SaveChangesAsync();
        var recipient = new NewsLetterRecipient
        {
            NewsLetterId = nl.Id,
            ClientId = client.Id,
            Email = client.Email,
            RecipientName = "Opt Out",
            UnsubscribeToken = Guid.NewGuid().ToString("N")
        };
        db.Add(recipient);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return recipient.Id;
    }

    /// Drives the REAL SendGridEvents action: a genuine ECDSA P-256 keypair, the public half fed
    /// to the controller through configuration exactly as production does, and a signature over
    /// timestamp+payload the SendGrid validator accepts.
    private static async Task<Microsoft.AspNetCore.Mvc.IActionResult> PostEventsAsync(
        IPRODbContext db, string eventName, int recipientId)
    {
        var body = "[{\"event\":\"" + eventName + "\",\"sg_message_id\":\"msg-h8\"," +
                   "\"timestamp\":1756000000,\"newsletter_recipient_id\":" + recipientId + "}]";
        var timestamp = "1756000000";

        using var ecdsa = System.Security.Cryptography.ECDsa.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        var signature = Convert.ToBase64String(ecdsa.SignData(
            Encoding.UTF8.GetBytes(timestamp + body),
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence));

        var config = new TestConfig(publicKey);

        var uow = new IPRO.DataAccess.Repositories.UnitOfWork(db);
        var consent = new IPRO.Business.Services.EmailConsentService(
            db, config, Microsoft.Extensions.Logging.Abstractions.NullLogger<IPRO.Business.Services.EmailConsentService>.Instance,
            Array.Empty<IPRO.Business.Services.IUnsubscribeNotifier>());
        var newsletters = new IPRO.Business.Services.NewsLetterService(uow, consent, db);

        var controller = new IPRO.Web.Controllers.NewsletterController(
            newsletters, null!, null!, uow, db, null!, null!, null!,
            new IPRO.Business.Services.EmailDeliveryTracker(db,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<IPRO.Business.Services.EmailDeliveryTracker>.Instance, consent),
            consent, config,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IPRO.Web.Controllers.NewsletterController>.Instance);

        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Body = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(body));
        ctx.Request.ContentLength = body.Length;
        ctx.Request.Headers["X-Twilio-Email-Event-Webhook-Signature"] = signature;
        ctx.Request.Headers["X-Twilio-Email-Event-Webhook-Timestamp"] = timestamp;
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = ctx };

        return await controller.SendGridEvents();
    }

    /// The one configuration key the webhook path reads. A tiny hand-rolled IConfiguration keeps
    /// the test free of the Configuration.Binder package the test project does not reference.
    private sealed class TestConfig : Microsoft.Extensions.Configuration.IConfiguration
    {
        private readonly string _publicKey;
        public TestConfig(string publicKey) => _publicKey = publicKey;
        public string? this[string key]
        {
            get => key == "Email:SendGridEventWebhookPublicKey" ? _publicKey : null;
            set { }
        }
        public IEnumerable<Microsoft.Extensions.Configuration.IConfigurationSection> GetChildren()
            => Array.Empty<Microsoft.Extensions.Configuration.IConfigurationSection>();
        public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken()
            => new Microsoft.Extensions.Primitives.CancellationChangeToken(System.Threading.CancellationToken.None);
        public Microsoft.Extensions.Configuration.IConfigurationSection GetSection(string key)
            => throw new NotSupportedException();
    }

    private static async Task<int> SeedAgentAsync(IPRODbContext db, bool active)
    {
        var rule = new BillingRule { PackageName = $"CS-{Guid.NewGuid():N}"[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"cs-{Guid.NewGuid():N}"[..20],
            Email = $"cs-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Consent", LastName = "Session",
            DomainName = $"cs-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario",
            IsActive = active,
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return agent.Id;
    }
}
