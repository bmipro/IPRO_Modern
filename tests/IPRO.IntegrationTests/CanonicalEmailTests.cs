using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 443 (2026-08-31). Gmail ignores dots in the local part and everything from '+' on, and
// treats googlemail.com as gmail.com: john.smith+ipro@gmail.com, johnsmith@gmail.com and
// JohnSmith@googlemail.com are ONE mailbox. Found the hard way -- a test sent to
// bahmanmotamed@gmail.com arrived at bahman.motamed@gmail.com.
//
// Our data model does not know that. Client uniqueness is per exact string, and suppression is a
// per-CLIENT-ROW flag (Client.EmailOptOutAt), so one person entered twice under dot variants gets
// every card, letter, newsletter and drip step TWICE, and unsubscribing one row leaves the other
// mailing them. That second half is the CASL exposure: an unsubscribe has to actually stop the
// mail. Duplicate sends are also a direct route to spam complaints, which now feed the 442 quota
// review.
//
// The rule is gmail.com/googlemail.com ONLY. Dot-stripping applied to any other domain would merge
// two genuinely different people (john.smith@ and johnsmith@ at a company are two mailboxes), so
// every other address is just trimmed and lower-cased. Three places consult it: client create/edit
// uniqueness, CSV import de-duplication, and the suppression write so an unsubscribe reaches every
// row that is the same person for that agent.
public class CanonicalEmailTests
{
    // ---- the canonical form -----------------------------------------------------------------

    [Theory]
    [InlineData("john.smith@gmail.com",        "johnsmith@gmail.com")]
    [InlineData("j.o.h.n.smith@gmail.com",     "johnsmith@gmail.com")]
    [InlineData("johnsmith+ipro@gmail.com",    "johnsmith@gmail.com")]
    [InlineData("john.smith+a+b@gmail.com",    "johnsmith@gmail.com")]
    [InlineData("JohnSmith@GoogleMail.com",    "johnsmith@gmail.com")]
    [InlineData("  John.Smith@Gmail.COM  ",    "johnsmith@gmail.com")]
    [InlineData("bahman.motamed@gmail.com",    "bahmanmotamed@gmail.com")]   // the one that started this
    public void Gmail_variants_collapse_to_one_address(string input, string expected)
    {
        Assert.Equal(expected, CanonicalEmail.Canonical(input));
    }

    [Theory]
    [InlineData("john.smith@rogers.com",   "john.smith@rogers.com")]      // dots are significant elsewhere
    [InlineData("john+tag@alladvisers.com", "john+tag@alladvisers.com")]  // so is a plus
    [InlineData("  Support@IPROadvisers.COM ", "support@iproadvisers.com")] // case and whitespace are not
    [InlineData("x@mail.gmail.com",         "x@mail.gmail.com")]          // not gmail.com itself
    public void Every_other_domain_is_only_trimmed_and_lowercased(string input, string expected)
    {
        Assert.Equal(expected, CanonicalEmail.Canonical(input));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("not-an-address", "not-an-address")]
    [InlineData("@gmail.com", "@gmail.com")]
    [InlineData("x@", "x@")]
    public void Garbage_does_not_throw(string? input, string expected)
    {
        Assert.Equal(expected, CanonicalEmail.Canonical(input));
    }

    [Fact]
    public void Same_person_is_agent_agnostic_string_equality_on_the_canonical_form()
    {
        Assert.True(CanonicalEmail.SamePerson("john.smith@gmail.com", "johnsmith+x@googlemail.com"));
        Assert.False(CanonicalEmail.SamePerson("john.smith@rogers.com", "johnsmith@rogers.com"));
        Assert.False(CanonicalEmail.SamePerson("", ""));      // two blanks are not "the same person"
        Assert.False(CanonicalEmail.SamePerson(null, null));
    }

    // ---- an unsubscribe reaches every row that is the same person ---------------------------

    [Fact]
    public async Task Unsubscribing_one_gmail_row_suppresses_its_dot_variant_twin_for_that_agent()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var agent = await SeedAgentAsync(db);
        var other = await SeedAgentAsync(db);

        var primary   = await SeedClientAsync(db, agent.Id, "john.smith@gmail.com");
        var twin      = await SeedClientAsync(db, agent.Id, "johnsmith+ipro@googlemail.com");
        var stranger  = await SeedClientAsync(db, agent.Id, "jane.doe@gmail.com");
        var elsewhere = await SeedClientAsync(db, other.Id, "johnsmith@gmail.com");   // same person, OTHER agent
        db.ChangeTracker.Clear();

        var notifier = new CountingNotifier();
        var consent = new EmailConsentService(db, new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<EmailConsentService>.Instance, new IUnsubscribeNotifier[] { notifier });

        var tracked = await db.Clients.SingleAsync(c => c.Id == primary.Id);
        var result = await consent.SuppressAllAsync(tracked, "test");
        db.ChangeTracker.Clear();

        Assert.False(result.WasAlreadySuppressed);

        var rows = await db.Clients.AsNoTracking().ToDictionaryAsync(c => c.Id);
        Assert.NotNull(rows[primary.Id].EmailOptOutAt);
        Assert.NotNull(rows[twin.Id].EmailOptOutAt);          // the whole point
        Assert.False(rows[twin.Id].IsNewsletterSubscribed);
        Assert.Null(rows[stranger.Id].EmailOptOutAt);         // a different person is untouched
        Assert.Null(rows[elsewhere.Id].EmailOptOutAt);        // suppression is per AGENT: another
                                                              // adviser's consent is not ours to revoke

        // One person unsubscribed, so the agent hears about it ONCE, not once per row.
        Assert.Equal(1, notifier.Calls);
    }

    [Fact]
    public async Task A_second_unsubscribe_on_the_same_person_is_still_idempotent()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var agent = await SeedAgentAsync(db);
        var primary = await SeedClientAsync(db, agent.Id, "john.smith@gmail.com");
        await SeedClientAsync(db, agent.Id, "johnsmith@gmail.com");
        db.ChangeTracker.Clear();

        var notifier = new CountingNotifier();
        var consent = new EmailConsentService(db, new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<EmailConsentService>.Instance, new IUnsubscribeNotifier[] { notifier });

        await consent.SuppressAllAsync(await db.Clients.SingleAsync(c => c.Id == primary.Id), "test");
        db.ChangeTracker.Clear();
        var second = await consent.SuppressAllAsync(await db.Clients.SingleAsync(c => c.Id == primary.Id), "test");

        Assert.True(second.WasAlreadySuppressed);
        Assert.Equal(1, notifier.Calls);
    }

    // ---- the two entry points that create rows consult the same rule -------------------------
    //
    // ClientsController has no test harness (every dependency is real); these pin that both the
    // create/edit uniqueness check and the CSV import de-duplication go through CanonicalEmail,
    // the same division the rest of this suite uses for controller-side rules.

    [Fact]
    public void Create_edit_uniqueness_and_csv_import_both_use_the_canonical_form()
    {
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\ClientsController.cs"));

        var unique = Slice(src, "private async Task ValidateUniqueEmailAsync(", "\n    }");
        Assert.Contains("CanonicalEmail", unique);

        var import = Slice(src, "public async Task<IActionResult> ImportCsv(", "\n    }");
        Assert.Contains("CanonicalEmail", import);
    }

    [Fact]
    public void The_suppression_write_consults_the_canonical_form()
    {
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Business\Services\EmailConsentService.cs"));
        var body = Slice(src, "public async Task<SuppressionResult> SuppressAllAsync(", "\n    }");
        Assert.Contains("CanonicalEmail", body);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static string Slice(string src, string start, string end)
    {
        var i = src.IndexOf(start, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{start}' moved; this pin needs updating");
        var j = src.IndexOf(end, i, StringComparison.Ordinal);
        Assert.True(j > i);
        return src[i..j];
    }

    private static async Task<AgentUser> SeedAgentAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = $"Pkg-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m };
        db.Add(rule);
        await db.SaveChangesAsync();

        var agent = new AgentUser
        {
            UserName = $"canon-{Guid.NewGuid():N}"[..20],
            Email = $"canon.{Guid.NewGuid():N}"[..20] + "@example.com",   // AgentUsers.Email is unique
            FirstName = "Canon",
            LastName = "Test",
            DomainName = $"canon-{Guid.NewGuid():N}"[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent;
    }

    private static async Task<Client> SeedClientAsync(IPRODbContext db, int agentId, string email)
    {
        var client = new Client
        {
            AgentUserId = agentId,
            FirstName = "Person",
            LastName = "Canon",
            Email = email,
            IsNewsletterSubscribed = true
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client;
    }

    private sealed class CountingNotifier : IUnsubscribeNotifier
    {
        public int Calls;
        public Task NotifyAgentAsync(Client client) { Calls++; return Task.CompletedTask; }
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
