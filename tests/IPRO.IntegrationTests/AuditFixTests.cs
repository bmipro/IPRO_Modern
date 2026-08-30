using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Communication.Email;
using IPRO.Billing;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// Pre-launch adversarial audit, 2026-08-30 (22 days out). Six independent auditors swept the
// codebase and every serious candidate was attacked by a verifier whose default was to refute;
// these pin the findings that survived. Every test here was observed RED against the pre-fix code.
public class AuditFixTests
{
    // ---- 1. "Revoke portal access" did not revoke anything -----------------------------------
    //
    // The client-portal cookie was issued once and never re-checked. RevokePortal nulled the
    // password hash and reported success while an already-signed-in client kept reading documents
    // -- including ones the agent uploaded AFTER the revocation -- for the life of an 8-hour
    // SLIDING cookie, i.e. indefinitely for anyone who kept clicking. The agent scheme got exactly
    // this fix on 2026-08-27 (AgentCookieRevalidator) and Admin on 2026-08-20 (ADMIN-7); the
    // client scheme is the third instance of the same pattern and was simply never carried across.

    [Fact]
    public async Task A_revoked_client_portal_session_is_rejected_on_the_next_request()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, clientId) = await SeedPortalClientAsync(db, agentActive: true, portalEnabled: false);

        var verdict = await IPRO.Web.Infrastructure.ClientPortalCookieRevalidator
            .EvaluateAsync(db, clientId.ToString(), agentId.ToString());

        Assert.Equal(IPRO.Web.Infrastructure.ClientPortalCookieRevalidator.Verdict.Reject, verdict);
    }

    [Fact]
    public async Task A_deleted_client_cannot_keep_using_the_portal()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var verdict = await IPRO.Web.Infrastructure.ClientPortalCookieRevalidator
            .EvaluateAsync(db, "999999999", "1");

        Assert.Equal(IPRO.Web.Infrastructure.ClientPortalCookieRevalidator.Verdict.Reject, verdict);
    }

    [Fact]
    public async Task A_clients_portal_access_dies_with_their_agents_account()
    {
        // The client's access is the agent's, exactly as a team member's access is the agent's:
        // deactivating or erasing an agent must not leave that agent's clients reading documents.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, clientId) = await SeedPortalClientAsync(db, agentActive: false, portalEnabled: true);

        var verdict = await IPRO.Web.Infrastructure.ClientPortalCookieRevalidator
            .EvaluateAsync(db, clientId.ToString(), agentId.ToString());

        Assert.Equal(IPRO.Web.Infrastructure.ClientPortalCookieRevalidator.Verdict.Reject, verdict);
    }

    [Fact]
    public async Task A_marker_claim_cannot_be_used_to_ride_another_agents_client_session()
    {
        // Defensive, mirroring AgentCookieRevalidator's team-member guard: the agent id in the
        // cookie must still be the client's real owner.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, clientId) = await SeedPortalClientAsync(db, agentActive: true, portalEnabled: true);

        var verdict = await IPRO.Web.Infrastructure.ClientPortalCookieRevalidator
            .EvaluateAsync(db, clientId.ToString(), (agentId + 12345).ToString());

        Assert.Equal(IPRO.Web.Infrastructure.ClientPortalCookieRevalidator.Verdict.Reject, verdict);
    }

    [Fact]
    public async Task An_active_portal_client_keeps_working()
    {
        // This runs on EVERY authenticated portal request. Wrong in this direction and it locks
        // every client out of their own portal.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, clientId) = await SeedPortalClientAsync(db, agentActive: true, portalEnabled: true);

        var verdict = await IPRO.Web.Infrastructure.ClientPortalCookieRevalidator
            .EvaluateAsync(db, clientId.ToString(), agentId.ToString());

        Assert.Equal(IPRO.Web.Infrastructure.ClientPortalCookieRevalidator.Verdict.Ok, verdict);
    }

    [Fact]
    public void The_client_portal_scheme_actually_wires_the_revalidator()
    {
        // A revalidator nobody registered is a comment. The agent scheme's EventsType line is the
        // shape to match, on the ClientPortal scheme.
        var program = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Program.cs"));
        var clientScheme = program[program.IndexOf("AddCookie(\"ClientPortal\"", StringComparison.Ordinal)..];
        clientScheme = clientScheme[..clientScheme.IndexOf("});", StringComparison.Ordinal)];
        Assert.Contains("EventsType", clientScheme);
        Assert.Contains("ClientPortalCookieRevalidator", clientScheme);
        Assert.Contains("AddScoped<IPRO.Web.Infrastructure.ClientPortalCookieRevalidator>", program);
    }

    // ---- 2. Bulk email on the Azure provider disclosed every recipient ------------------------
    //
    // SendGrid's CreateSingleEmailToMultipleRecipients defaults showAllRecipients to FALSE: one
    // personalization per recipient, each seeing only themselves. The ACS port collapsed the whole
    // list into ONE message's To collection, so retiring a template used by 12 agents would mail
    // all 12 competing advisers a copy listing each other's names and addresses -- unrecallable,
    // and customer contact data under PIPEDA. The provider swap advertised itself as
    // contract-preserving; this was the one place it silently was not.

    [Fact]
    public async Task Bulk_send_never_discloses_one_recipient_to_another()
    {
        var sent = new List<EmailMessage>();
        var service = BuildAzureEmail(m => { sent.Add(m); return Task.FromResult("op"); });

        var ok = await service.SendBulkAsync(new[]
        {
            new EmailRecipient("a@example.com", "A"),
            new EmailRecipient("b@example.com", "B"),
            new EmailRecipient("c@example.com", "C"),
        }, "Subject", "<p>x</p>");

        Assert.True(ok);
        // One message per recipient, each addressed only to that recipient.
        Assert.Equal(3, sent.Count);
        Assert.All(sent, m => Assert.Single(m.Recipients.To));
        Assert.Equal(new[] { "a@example.com", "b@example.com", "c@example.com" },
            sent.Select(m => m.Recipients.To.Single().Address).OrderBy(x => x));
        // And nobody is smuggled in through CC/BCC either.
        Assert.All(sent, m => Assert.Empty(m.Recipients.CC));
        Assert.All(sent, m => Assert.Empty(m.Recipients.BCC));
    }

    [Fact]
    public async Task Bulk_send_reports_failure_when_a_recipient_is_rejected()
    {
        // With a per-recipient loop, one bad address must not silently swallow the whole batch.
        var service = BuildAzureEmail(m =>
            m.Recipients.To.Single().Address == "bad@example.com"
                ? throw new RequestFailedException(400, "bad address")
                : Task.FromResult("op"));

        var ok = await service.SendBulkAsync(new[]
        {
            new EmailRecipient("good@example.com", "G"),
            new EmailRecipient("bad@example.com", "B"),
        }, "s", "<p>x</p>");

        Assert.False(ok);
    }

    // ---- 3. Support-role admins could deactivate any agent ------------------------------------
    //
    // Activate/Deactivate carried only [HttpPost, ValidateAntiForgeryToken] while their sensitive
    // siblings (Delete, ResetPassword, ErasurePreview, both Rebuilds) carry the SuperAdmin policy.
    // The class-level AdminAccess policy is literally "is signed in". Worse than a crafted request:
    // Details.cshtml rendered the button unconditionally, so a Support admin saw a live control --
    // and one click signs the agent AND their whole team out instantly, now that the agent
    // revalidator rejects on the next request.

    [Theory]
    [InlineData("Activate")]
    [InlineData("Deactivate")]
    public void Flipping_an_agents_active_flag_is_superadmin_only(string action)
    {
        var method = typeof(IPRO.Admin.Controllers.AgentsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.Name == action);

        var policies = method.GetCustomAttributes<AuthorizeAttribute>()
            .Select(a => a.Policy)
            .ToList();

        Assert.Contains("SuperAdmin", policies);
    }

    [Fact]
    public void The_agent_details_view_does_not_offer_the_button_to_a_support_admin()
    {
        // RebuildResources directly below it already disables itself for non-SuperAdmins; the
        // activate/deactivate control must not stay the odd one out.
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\Agents\Details.cshtml"));
        // Both forms must sit behind the same SuperAdmin claim check the rebuild actions use.
        foreach (var action in new[] { "Deactivate", "Activate" })
        {
            var index = view.IndexOf($"asp-action=\"{action}\"", StringComparison.Ordinal);
            Assert.True(index > 0, $"the {action} form moved; this pin needs updating");
            // A gate immediately above it, not merely one somewhere in the file. (Proximity
            // rather than brace-matching: Razor nests @if blocks and a parser here would pin the
            // formatting, not the rule. The server-side [Authorize] is the real defence and is
            // pinned by the test above; this stops the button being SHOWN to someone who would
            // then be refused.)
            var window = view[Math.Max(0, index - 900)..index];
            Assert.Contains("AdminRoles.SuperAdmin", window);
        }
    }

    // ---- 4. Quarterly checkout invoiced more than PayPal charged ------------------------------
    //
    // IsPeriodOfferable is meant to block Quarterly. Its guard is "price > 0 AND a plan id exists",
    // and its comment plus BillingPeriodGuardTests both rest on the belief that QuarterlyPrice is
    // always 0 -- which the seeder falsifies (Silver/Gold/Platinum ship 120/180/270). Meanwhile
    // GetPayPalPlanId had no Quarterly arm and fell through to the MONTHLY plan id. So a posted
    // BillingPeriod=Quarterly invoiced the quarterly amount while PayPal charged monthly, and the
    // settlement match then marked the too-large invoice PAID -- the ledger permanently recording
    // money that never moved.

    [Fact]
    public void Quarterly_is_not_offerable_even_when_the_seeder_gave_it_a_price()
    {
        var package = new BillingRule
        {
            PackageName = "Gold",
            MonthlyPrice = 60m,
            QuarterlyPrice = 180m,               // exactly what PackageEntitlementSeeder inserts
            PayPalMonthlyPlanId = "P-MONTHLY-1", // and a real monthly plan exists
        };

        Assert.False(PayPalBillingService.IsPeriodOfferable(package, BillingPeriod.Quarterly));
        // The periods that ARE sold must keep working.
        Assert.True(PayPalBillingService.IsPeriodOfferable(package, BillingPeriod.Monthly));
    }

    // ---- 5. A stranger could undo someone else's unsubscribe ----------------------------------
    //
    // SubmitLead matched an existing contact by EMAIL ALONE and, if they had opted out, called
    // ResubscribeAsync -- clearing the global opt-out and restoring newsletters, e-cards,
    // e-letters, polls, DYK and drip. No token, no confirmation, no proof the submitter owns the
    // address. The code 140 lines below names this exact hazard for the DidYouKnow queue ("this
    // queue emails whatever address the visitor typed, with no verification that they own it").
    // A recorded "stop" must survive an anonymous form post.

    [Theory]
    [InlineData(true, false)]   // already opted out -> the stop stands
    [InlineData(false, true)]   // never opted out   -> ordinary signup still subscribes
    public void A_public_form_post_can_never_lift_a_recorded_opt_out(bool hasOptOut, bool expectSubscribe)
    {
        var decision = IPRO.Web.Controllers.PublicWebsiteController.DecidePublicNewsletterConsent(hasOptOut);
        Assert.Equal(expectSubscribe, decision == IPRO.Web.Controllers.PublicWebsiteController.PublicNewsletterConsent.Subscribe);
    }

    [Fact]
    public void The_public_lead_path_no_longer_calls_resubscribe()
    {
        var controller = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\PublicWebsiteController.cs"));
        Assert.DoesNotContain("_consent.ResubscribeAsync", controller);
    }

    // ---- 6. The Email Setup diagnostic still reported only SendGrid --------------------------
    //
    // The one screen built to diagnose email config never read Email:Provider. After the ACS
    // cutover it would tell a SuperAdmin -- mid-outage -- that the problem is a SendGrid key that
    // no longer matters, and point them at the wrong settings on both apps.

    [Fact]
    public void The_email_setup_screen_reports_the_provider_that_is_actually_sending()
    {
        var controller = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Controllers\EmailSetupController.cs"));
        Assert.Contains("Email:Provider", controller);
        Assert.Contains("AzureCommunicationConnectionString", controller);
    }

    // ---- 7. Quebec renewal invoices were a penny short, every month --------------------------
    //
    // One Math.Round missing MidpointRounding.AwayFromZero, which every other tax computation in
    // the money path pins. 14.975% on $60 lands exactly on a half-cent: PayPal was charged $68.99
    // while every renewal invoice said $68.98, breaking the invariant the surrounding code asserts
    // by name ("the invoice total equals what PayPal charged") and understating the QST remittance
    // line for the life of the subscription.

    [Theory]
    [InlineData(60.00, 0.14975, 8.99)]   // Quebec GST+QST -- the exact half-cent case
    [InlineData(40.00, 0.13, 5.20)]      // Ontario HST
    [InlineData(90.00, 0.15, 13.50)]     // Atlantic HST
    public void Tax_always_rounds_away_from_zero_whichever_path_computes_it(decimal net, decimal rate, decimal expected)
    {
        Assert.Equal(expected, PayPalBillingService.RoundTax(net, rate));
    }

    [Fact]
    public void The_renewal_override_path_uses_the_shared_rounding_helper()
    {
        // The defect was one call site diverging from the rest. Pinning the helper's use keeps
        // them from drifting apart again.
        var service = File.ReadAllText(FindRepoFile(@"src\IPRO.Billing\PayPalBillingService.cs"));
        Assert.DoesNotContain("Math.Round(subtotal * taxRateOverride.Value, 2)", service);
        Assert.Contains("RoundTax(subtotal, taxRateOverride.Value)", service);
    }

    // ---- 8. A tampered return path 500s the visitor after the lead was saved ------------------
    //
    // NormalizeReturnPath rejected a leading "//" but not "/\", which Url.IsLocalUrl DOES reject --
    // so LocalRedirect threw, after the client row was written and the agent's notification had
    // gone out. The visitor saw an error page instead of the confirmation, and a retry inside five
    // minutes was swallowed by the duplicate guard. Not an open redirect: the framework guard held.

    [Theory]
    [InlineData(@"/\evil.example.com", "/")]
    [InlineData("//evil.example.com", "/")]
    [InlineData(@"/\/evil.example.com", "/")]
    [InlineData("https://evil.example.com", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    [InlineData("/contact", "/contact")]
    [InlineData("/contact?utm=x#frag", "/contact")]
    public void A_return_path_is_either_a_local_path_or_the_home_page(string? input, string expected)
    {
        Assert.Equal(expected, IPRO.Web.Controllers.PublicWebsiteController.NormalizeReturnPath(input));
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static AzureEmailService BuildAzureEmail(Func<EmailMessage, Task<string>> sendCore)
    {
        var settings = Options.Create(new EmailSettings
        {
            Provider = "Azure",
            AzureCommunicationConnectionString = "endpoint=https://x.canada.communication.azure.com/;accesskey=abc"
        });
        var service = new AzureEmailService(settings, NullLogger<AzureEmailService>.Instance)
        {
            ClientFactory = _ => new StubEmailClient(sendCore)
        };
        return service;
    }

    private sealed class StubEmailClient : EmailClient
    {
        private readonly Func<EmailMessage, Task<string>> _sendCore;
        public StubEmailClient(Func<EmailMessage, Task<string>> sendCore) => _sendCore = sendCore;

        public override async Task<EmailSendOperation> SendAsync(WaitUntil wait, EmailMessage message, CancellationToken cancellationToken = default)
            => new StubOperation(await _sendCore(message));

        private sealed class StubOperation : EmailSendOperation
        {
            private readonly string _id;
            public StubOperation(string id) => _id = id;
            public override string Id => _id;
        }
    }

    private static async Task<(int AgentId, int ClientId)> SeedPortalClientAsync(IPRODbContext db, bool agentActive, bool portalEnabled)
    {
        var rule = new BillingRule { PackageName = $"AF-{Guid.NewGuid():N}"[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();

        var agent = new AgentUser
        {
            UserName = $"af-{Guid.NewGuid():N}"[..20],
            Email = $"af-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Audit", LastName = "Fix",
            DomainName = $"af-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario",
            IsActive = agentActive,
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();

        var client = new Client
        {
            AgentUserId = agent.Id,
            FirstName = "Portal", LastName = "Client",
            Email = $"pc-{Guid.NewGuid():N}"[..12] + "@example.test",
            PortalPasswordHash = portalEnabled ? "hashed" : null,
            PortalActivatedAt = portalEnabled ? DateTime.UtcNow.AddDays(-3) : null
        };
        db.Add(client);
        await db.SaveChangesAsync();

        return (agent.Id, client.Id);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
