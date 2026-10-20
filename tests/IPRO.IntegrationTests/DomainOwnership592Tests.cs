using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Scheduler;
using IPRO.Utility;
using IPRO.Web.Controllers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 592 (2026-10-19), found by 591's independent review and confirmed by the owner the same day on a real
// domain: www.411trades.com had belonged to one adviser; after it left that account it was still bound on the
// production web app, its DNS still pointed at iPro, and the owner could attach it to another account. Any
// adviser could have done the same, and the domain's visitors would have seen that adviser's site.
//
//   - Proof of control: a row is connected, secured and served only once its adviser's own TXT record
//     (_ipro-verify, a value made for that row) has been seen. The domain check asks Azure for nothing and the
//     public site serves nothing for an unproven row -- not even through the website's own CustomDomain.
//   - Removal that is not forgotten: the addresses of a removed row (My Website's Remove, SuperAdmin's delete)
//     are written down with the removal and asked of Azure until Azure confirms (DomainCleanups).
//   - Rows that existed at the release count as proven then (the live customers), whoever starts first.
//
// In ConfigHazardTests' collection: DomainCheckService.ResolveHook, DomainOwnershipProof.LookupHook and
// AzureDomainAutomationService.SiteNameProvider are seams for the whole process.
[Collection(ConfigHazardTests.AzureStatics)]
public class DomainOwnership592Tests : IDisposable
{
    private const string Platform = "40.89.19.0";
    private const string Target = "ipro-prod-web.azurewebsites.net";

    public DomainOwnership592Tests()
    {
        DomainCheckService.ResolveHook = (host, ct) => Dns.GetHostAddressesAsync(host, ct);
        DomainOwnershipProof.LookupHook = null;
    }

    public void Dispose()
    {
        DomainCheckService.ResolveHook = (host, ct) => Dns.GetHostAddressesAsync(host, ct);
        DomainOwnershipProof.LookupHook = null;
    }

    // ------------------------------------------------------------- the domain check --

    [Fact]
    public async Task An_unproven_domain_is_never_bound_probed_or_marked_connected()
    {
        // The 411trades.com shape: the name resolves to the platform (the last adviser's DNS is still there), and
        // the new adviser has not published their record.
        Dns592(("www.taken592.example.test", Platform), ("taken592.example.test", Platform), (Target, Platform));
        var txt = Txt592();
        var azure = new ScriptedAzure();
        var domain = NewDomain("taken592.example.test", token: "ipro-0123456789abcdef0123456789abcdef");

        Assert.False(await new DomainCheckService(new NoHttp(), azure, NullLogger<DomainCheckService>.Instance).CheckAsync(domain));

        Assert.Empty(azure.Calls);                                  // nothing bound, ordered or secured
        Assert.Null(domain.OwnershipVerifiedAt);
        Assert.Equal(AgentDomainStatus.PendingDns, domain.DnsStatus);
        Assert.Equal(AgentDomainStatus.BindingPending, domain.AzureBindingStatus);
        Assert.Equal(AgentDomainStatus.BindingPending, domain.SslStatus);
        Assert.False(domain.RootRedirectsToWww);
        Assert.Contains("_ipro-verify", domain.LastError);
        Assert.Contains("ipro-0123456789abcdef0123456789abcdef", domain.LastError);
        Assert.Contains("taken592.example.test", domain.LastError);
        // The adviser reads the instruction itself, not a translation of it.
        Assert.Equal(domain.LastError, DomainErrorTranslator.ToAgentMessage(domain.LastError));
        // The record was looked for where the proof lives: on the short address.
        Assert.Contains(txt.Asked, a => a.Contains("name=_ipro-verify.taken592.example.test&", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_advisers_own_TXT_record_proves_the_domain_and_the_check_goes_on_to_connect_it()
    {
        Dns592(("www.mine592.example.test", Platform), ("mine592.example.test", "76.223.105.230"), (Target, Platform));
        const string token = "ipro-aaaaaaaaaaaaaaaabbbbbbbbbbbbbbbb";
        // Two TXT values on the name, ours split into two quoted strings the way resolvers show long records.
        Txt592(("_ipro-verify.mine592.example.test", new[] { "\"v=spf1 -all\"", "\"ipro-aaaaaaaaaaaaaaaa\" \"bbbbbbbbbbbbbbbb\"" }));
        var azure = new ScriptedAzure();
        var domain = NewDomain("mine592.example.test", token);
        var log = new RecordingLogger();

        await new DomainCheckService(new NoHttp(), azure, log.For<DomainCheckService>()).CheckAsync(domain);

        Assert.NotNull(domain.OwnershipVerifiedAt);
        Assert.Contains("ensure www.mine592.example.test", azure.Calls);
        Assert.Equal(AgentDomainStatus.Bound, domain.AzureBindingStatus);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("_ipro-verify.mine592.example.test", StringComparison.Ordinal));

        // Once proven, the record is not needed for the next checks.
        DomainOwnershipProof.LookupHook = (_, _) => Task.FromResult<string?>("{\"Status\":3}");
        await new DomainCheckService(new NoHttp(), azure, NullLogger<DomainCheckService>.Instance).CheckAsync(domain);
        Assert.NotNull(domain.OwnershipVerifiedAt);
        Assert.Equal(2, azure.Calls.Count(c => c == "ensure www.mine592.example.test"));
    }

    [Fact]
    public async Task Another_rows_value_a_record_in_the_wrong_place_or_no_answer_proves_nothing()
    {
        Dns592(("www.other592.example.test", Platform), ("other592.example.test", Platform), (Target, Platform));
        const string mine = "ipro-11111111111111111111111111111111";
        const string theirs = "ipro-22222222222222222222222222222222";
        var azure = new ScriptedAzure();
        var service = new DomainCheckService(new NoHttp(), azure, NullLogger<DomainCheckService>.Instance);

        // The previous adviser's record left in place: another row's value.
        Txt592(("_ipro-verify.other592.example.test", new[] { "\"" + theirs + "\"" }));
        var domain = NewDomain("other592.example.test", mine);
        await service.CheckAsync(domain);
        Assert.Null(domain.OwnershipVerifiedAt);

        // The right value on the www name instead of the short address.
        Txt592(("_ipro-verify.www.other592.example.test", new[] { "\"" + mine + "\"" }));
        await service.CheckAsync(domain);
        Assert.Null(domain.OwnershipVerifiedAt);

        // A value that only contains ours.
        Txt592(("_ipro-verify.other592.example.test", new[] { "\"x" + mine + "\"" }));
        await service.CheckAsync(domain);
        Assert.Null(domain.OwnershipVerifiedAt);

        // No resolver answers at all.
        DomainOwnershipProof.LookupHook = (_, _) => Task.FromResult<string?>(null);
        await service.CheckAsync(domain);
        Assert.Null(domain.OwnershipVerifiedAt);
        Assert.Empty(azure.Calls);

        // The second resolver is asked when the first one does not have it yet (its "no such record" from before).
        var asked = new List<string>();
        DomainOwnershipProof.LookupHook = (address, _) =>
        {
            asked.Add(address);
            return Task.FromResult<string?>(address.StartsWith("https://cloudflare-dns.com/", StringComparison.Ordinal)
                ? Answer(("_ipro-verify.other592.example.test", new[] { "\"" + mine + "\"" }))
                : "{\"Status\":3}");
        };
        await service.CheckAsync(domain);
        Assert.Equal(2, asked.Count);
        Assert.NotNull(domain.OwnershipVerifiedAt);
    }

    [Fact]
    public async Task A_row_without_a_value_waits_for_My_Website_to_give_it_one_and_a_row_whose_names_disagree_is_never_checked()
    {
        Dns592(("www.blank592.example.test", Platform), ("blank592.example.test", Platform), (Target, Platform));
        var txt = Txt592();
        var azure = new ScriptedAzure();
        var service = new DomainCheckService(new NoHttp(), azure, NullLogger<DomainCheckService>.Instance);

        // The review: a value made by the check could replace the one the adviser is copying from My Website.
        var blank = NewDomain("blank592.example.test", token: "");
        await service.CheckAsync(blank);
        Assert.Equal(string.Empty, blank.VerificationToken);
        Assert.Null(blank.OwnershipVerifiedAt);
        Assert.Contains("open My Website", blank.LastError);
        Assert.Equal(blank.LastError, DomainErrorTranslator.ToAgentMessage(blank.LastError));
        Assert.Empty(txt.Asked);

        // Not asked before the www name resolves: a resolver asked too early remembers "no such record".
        Dns592((Target, Platform));
        var early = NewDomain("early592.example.test", "ipro-55555555555555555555555555555555");
        await service.CheckAsync(early);
        Assert.Empty(txt.Asked);
        Assert.Contains("_ipro-verify", early.LastError);
        Dns592(("www.blank592.example.test", Platform), ("blank592.example.test", Platform), (Target, Platform));

        // A row whose short address is someone else's name: its proof would cover the wrong zone.
        var odd = NewDomain("attacker592.example.test", "ipro-33333333333333333333333333333333");
        odd.RootDomain = "victim592.example.test";
        odd.RootDnsStatus = AgentDomainStatus.Bound;
        Txt592(("_ipro-verify.attacker592.example.test", new[] { "\"ipro-33333333333333333333333333333333\"" }));
        await service.CheckAsync(odd);
        Assert.Null(odd.OwnershipVerifiedAt);
        Assert.Contains("not consistent", odd.LastError);
        Assert.Empty(azure.Calls);
    }

    [Fact]
    public void The_record_name_and_the_value_shown_to_the_adviser()
    {
        var www = NewDomain("firm592.example", "ipro-44444444444444444444444444444444");
        Assert.Equal("firm592.example", DomainOwnershipProof.ProvenName(www));
        Assert.Equal("_ipro-verify.firm592.example", DomainOwnershipProof.RecordName(www));
        Assert.Equal("_ipro-verify", DomainOwnershipProof.NameToType(www));

        var sub = new AgentDomain { DomainName = "clients.firm592.example", RootDomain = "clients.firm592.example", WwwDomain = "clients.firm592.example" };
        Assert.Equal("_ipro-verify.clients.firm592.example", DomainOwnershipProof.RecordName(sub));
        Assert.Equal("_ipro-verify.clients.firm592.example", DomainOwnershipProof.NameToType(sub));

        var tokens = Enumerable.Range(0, 50).Select(_ => DomainOwnershipProof.NewToken()).ToList();
        Assert.Equal(50, tokens.Distinct().Count());
        Assert.All(tokens, t => Assert.Matches("^ipro-[0-9a-f]{32}$", t));
        Assert.False(DomainOwnershipProof.Matches(new[] { "" }, ""));
        Assert.False(DomainOwnershipProof.Matches(new[] { "ipro-1" }, null));
    }

    // ------------------------------------------- the takeover, end to end (411trades.com) --

    [Fact]
    public async Task A_name_another_adviser_left_behind_is_not_served_or_connected_for_the_next_adviser_until_they_prove_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        const string host = "www.trades592.example.test";
        var (aId, aSite) = await AdviserAsync(db);
        var (bId, bSite) = await AdviserAsync(db);

        // Adviser A's domain, proven and live; A removes it while Azure is unreachable: the binding stays.
        var a = Row(aId, aSite, host, "trades592.example.test");
        db.Add(a);
        await db.SaveChangesAsync();
        Assert.IsType<ViewResult>(await PublicSite(db, host).Index());
        var down = new ScriptedAzure { RemoveAnswer = AzureDomainAutomationResult.Failed("Azure cleanup failed: 503 Service Unavailable") };
        await Website(db, aId, down).RemoveDomain(a.Id, host);
        db.ChangeTracker.Clear();

        // Adviser B adds it. Nothing on the public site is B's, through the row or through the website's own field.
        var azure = new ScriptedAzure();
        await Website(db, bId, azure).AddDomain(host);
        db.ChangeTracker.Clear();
        var bRow = await db.AgentDomains.SingleAsync(d => d.AgentUserId == bId);
        Assert.Null(bRow.OwnershipVerifiedAt);
        Assert.Matches("^ipro-[0-9a-f]{32}$", bRow.VerificationToken);
        Assert.Equal(host, (await db.AgentWebsites.SingleAsync(w => w.Id == bSite)).CustomDomain);
        Assert.False(await Serves(db, host, bId));
        Assert.False(await Serves(db, "trades592.example.test", bId));

        // The domain job: nothing asked of Azure for B's row, which waits for B's record.
        Dns592((host, Platform), ("trades592.example.test", Platform), (Target, Platform));
        Txt592();
        var check = new DomainCheckService(new NoHttp(), azure, NullLogger<DomainCheckService>.Instance);
        await new DomainAutomationJob(db, check, new NullEmail(), new ConfigurationBuilder().Build(), NullLogger<DomainAutomationJob>.Instance).RunAsync();
        db.ChangeTracker.Clear();
        Assert.DoesNotContain(azure.Calls, c => c.StartsWith("ensure", StringComparison.Ordinal));
        bRow = await db.AgentDomains.SingleAsync(d => d.AgentUserId == bId);
        Assert.Equal(AgentDomainStatus.BindingPending, bRow.AzureBindingStatus);
        Assert.False(await Serves(db, host, bId));

        // Whoever controls the domain publishes B's value: now it is B's.
        Txt592(("_ipro-verify.trades592.example.test", new[] { "\"" + bRow.VerificationToken + "\"" }));
        var tracked = await db.AgentDomains.SingleAsync(d => d.AgentUserId == bId);
        await check.CheckAsync(tracked);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Contains("ensure " + host, azure.Calls);
        Assert.True(await Serves(db, host, bId));
    }

    [Fact]
    public async Task A_new_domain_from_the_settings_form_starts_unproven_and_the_page_shows_its_record()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await AdviserAsync(db);
        var website = await db.AgentWebsites.AsNoTracking().SingleAsync(w => w.Id == websiteId);

        var controller = Website(db, agentId, new ScriptedAzure());
        await controller.Save(new AgentWebsite { TemplateId = website.TemplateId, SiteTitle = "Mine", CustomDomain = "form592.example.test" }, null);
        db.ChangeTracker.Clear();
        var row = await db.AgentDomains.SingleAsync(d => d.AgentUserId == agentId);
        Assert.Equal("form592.example.test", row.DomainName);
        Assert.Null(row.OwnershipVerifiedAt);
        Assert.Matches("^ipro-[0-9a-f]{32}$", row.VerificationToken);

        // A row saved without a value (the version before 592, during its release) gets one when the page opens.
        await db.Database.ExecuteSqlRawAsync("UPDATE `AgentDomains` SET `VerificationToken` = '' WHERE `Id` = {0}", row.Id);
        Assert.IsType<ViewResult>(await Website(db, agentId, new ScriptedAzure()).Index());
        db.ChangeTracker.Clear();
        Assert.Matches("^ipro-[0-9a-f]{32}$", (await db.AgentDomains.SingleAsync(d => d.Id == row.Id)).VerificationToken);

        var view = Read(@"src\IPRO.Web\Views\Website\Index.cshtml");
        Assert.Contains("@if (IPRO.Utility.DomainOwnershipProof.IsNeeded(domain))", view);
        Assert.Contains("@IPRO.Utility.DomainOwnershipProof.NameToType(domain)", view);
        Assert.Contains("<span class=\"text-muted\">Value&nbsp;</span>@domain.VerificationToken", view);
        Assert.Contains("Nothing is connected until we see it.", view);
    }

    [Fact]
    public async Task SuperAdmin_cannot_mark_an_unproven_domain_bound()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await AdviserAsync(db);
        var row = Row(agentId, websiteId, "www.mark592.example.test", "mark592.example.test");
        row.OwnershipVerifiedAt = null;
        row.VerificationToken = DomainOwnershipProof.NewToken();
        row.DnsStatus = AgentDomainStatus.PendingDns;
        row.AzureBindingStatus = AgentDomainStatus.BindingPending;
        row.SslStatus = AgentDomainStatus.BindingPending;
        db.Add(row);
        await db.SaveChangesAsync();

        var admin = Domains(db);
        await admin.MarkBound(row.Id);
        db.ChangeTracker.Clear();
        Assert.Equal(AgentDomainStatus.BindingPending, (await db.AgentDomains.SingleAsync(d => d.Id == row.Id)).AzureBindingStatus);
        Assert.Contains("_ipro-verify.mark592.example.test", admin.TempData["Error"] as string);

        await db.Database.ExecuteSqlRawAsync("UPDATE `AgentDomains` SET `OwnershipVerifiedAt` = UTC_TIMESTAMP(6) WHERE `Id` = {0}", row.Id);
        db.ChangeTracker.Clear();
        await Domains(db).MarkBound(row.Id);
        db.ChangeTracker.Clear();
        Assert.Equal(AgentDomainStatus.Bound, (await db.AgentDomains.SingleAsync(d => d.Id == row.Id)).AzureBindingStatus);
    }

    // ------------------------------------------------------------ removal, until done --

    [Fact]
    public async Task A_removal_Azure_does_not_confirm_is_kept_and_asked_again_until_it_is()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await AdviserAsync(db);
        var row = Row(agentId, websiteId, "www.retry592.example.test", "retry592.example.test");
        row.RootDnsStatus = AgentDomainStatus.Bound;               // 553: the short address pointed at the platform
        row.RootAzureBindingStatus = AgentDomainStatus.Bound;
        db.Add(row);
        await db.SaveChangesAsync();

        // Switched off, refused, unreachable: the answer used to be dropped and the binding forgotten.
        var azure = new ScriptedAzure { RemoveAnswer = AzureDomainAutomationResult.Skipped("Azure domain automation is disabled. Set AzureDomainAutomation__Enabled=true.") };
        await Website(db, agentId, azure).RemoveDomain(row.Id, "www.retry592.example.test");
        Assert.Equal(new[] { "remove www.retry592.example.test", "remove retry592.example.test" }, azure.Calls);
        db.ChangeTracker.Clear();
        Assert.False(await db.AgentDomains.AnyAsync(d => d.Id == row.Id));
        var pending = await db.DomainCleanups.Where(c => c.FormerAgentUserId == agentId).OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(new[] { "www.retry592.example.test", "retry592.example.test" }, pending.Select(c => c.HostName));
        Assert.All(pending, c =>
        {
            Assert.Null(c.DoneAt);
            Assert.Equal(1, c.Attempts);
            Assert.Equal(DomainCleanupReasons.RemovedByAdviser, c.Reason);
            Assert.Contains("disabled", c.LastError);
            Assert.NotNull(c.NextAttemptAt);
        });

        // Not due yet: the job leaves them.
        var later = new ScriptedAzure();
        await Job(db, later).RunAsync();
        Assert.Empty(later.Calls);

        // Due: the job asks again, and Azure confirms.
        await db.Database.ExecuteSqlRawAsync("UPDATE `DomainCleanups` SET `NextAttemptAt` = UTC_TIMESTAMP(6) - INTERVAL 1 MINUTE WHERE `FormerAgentUserId` = {0}", agentId);
        await Job(db, later).RunAsync();
        Assert.Equal(new[] { "remove www.retry592.example.test", "remove retry592.example.test" }, later.Calls);
        db.ChangeTracker.Clear();
        Assert.All(await db.DomainCleanups.Where(c => c.FormerAgentUserId == agentId).ToListAsync(), c =>
        {
            Assert.NotNull(c.DoneAt);
            Assert.Equal(DomainCleanupOutcomes.Removed, c.Outcome);
            Assert.Equal(2, c.Attempts);
        });

        // Done is done.
        await Job(db, later).RunAsync();
        Assert.Equal(2, later.Calls.Count);

        // A throw is a failed try too, never a lost record.
        Assert.Equal(TimeSpan.FromMinutes(5), DomainCleanupQueue.RetryDelay(1));
        Assert.Equal(TimeSpan.FromHours(1), DomainCleanupQueue.RetryDelay(13));
        Assert.Equal(TimeSpan.FromDays(1), DomainCleanupQueue.RetryDelay(40));
    }

    [Fact]
    public async Task iPros_own_names_and_names_another_row_holds_are_never_removed()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (aId, aSite) = await AdviserAsync(db);
        db.Add(Row(aId, aSite, "www.held592.example.test", "held592.example.test"));
        var cleanups = new[]
        {
            new DomainCleanup { HostName = "app.iproadvisers.com", FormerAgentUserId = 1, Reason = DomainCleanupReasons.RemovedByAdviser },
            new DomainCleanup { HostName = "www.held592.example.test", FormerAgentUserId = 2, Reason = DomainCleanupReasons.RemovedByAdviser },
            new DomainCleanup { HostName = "bad host.example.test", FormerAgentUserId = 3, Reason = DomainCleanupReasons.AgentDeleted },
            // An adviser's setup address set outside the free sites' zone before 592: still served, so kept.
            new DomainCleanup { HostName = "setup592.example.test", FormerAgentUserId = 4, Reason = DomainCleanupReasons.RemovedByAdviser },
        };
        db.DomainCleanups.AddRange(cleanups);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("UPDATE `AgentUsers` SET `DomainName` = 'setup592.example.test' WHERE `Id` = {0}", aId);

        var azure = new ScriptedAzure();
        var log = new RecordingLogger();
        await DomainCleanupQueue.RunDueAsync(db, azure, new ConfigurationBuilder().Build(), log.For<DomainOwnership592Tests>());

        Assert.Empty(azure.Calls);
        db.ChangeTracker.Clear();
        var done = await db.DomainCleanups.ToDictionaryAsync(c => c.HostName);
        Assert.Equal(DomainCleanupOutcomes.Refused, done["app.iproadvisers.com"].Outcome);
        Assert.Equal(DomainCleanupOutcomes.KeptForAnotherRow, done["www.held592.example.test"].Outcome);
        Assert.Equal(DomainCleanupOutcomes.Refused, done["bad host.example.test"].Outcome);
        Assert.Equal(DomainCleanupOutcomes.KeptForAnotherRow, done["setup592.example.test"].Outcome);
        Assert.All(done.Values, c => Assert.NotNull(c.DoneAt));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("another domain row still names it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuperAdmins_delete_writes_the_addresses_down_with_the_shred_and_the_job_finishes_them()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, agentSite) = await AdviserAsync(db);
        db.Add(new AgentDomain
        {
            AgentUserId = agentId, AgentWebsiteId = agentSite, DomainName = "www.gone592.example.test", WwwDomain = "www.gone592.example.test", RootDomain = "gone592.example.test",
            DnsTarget = Target, DnsStatus = AgentDomainStatus.Bound, AzureBindingStatus = AgentDomainStatus.Bound, SslStatus = AgentDomainStatus.Bound,
            RootDnsStatus = AgentDomainStatus.DnsReady, RootRedirectsToWww = true,   // forwarded: nothing of the short address in Azure
            OwnershipVerifiedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // The admin app could not reach the automation at all: before 592 the domain was simply never unbound.
        var report = await AgentDataEraser.EraseAsync(db, agentId, eraseFinancialRecords: true);
        Assert.False(report.RetentionViolated);
        var written = await db.DomainCleanups.AsNoTracking().Where(c => c.FormerAgentUserId == agentId).ToListAsync();
        Assert.Equal(new[] { "www.gone592.example.test" }, written.Select(c => c.HostName));
        Assert.Equal(DomainCleanupReasons.AgentDeleted, written[0].Reason);
        Assert.Null(written[0].DoneAt);

        var azure = new ScriptedAzure();
        await Job(db, azure).RunAsync();
        Assert.Equal(new[] { "remove www.gone592.example.test" }, azure.Calls);
        Assert.NotNull((await db.DomainCleanups.AsNoTracking().SingleAsync(c => c.FormerAgentUserId == agentId)).DoneAt);

        // The preview writes nothing down.
        var (other, otherSite) = await AdviserAsync(db);
        db.Add(new AgentDomain { AgentUserId = other, AgentWebsiteId = otherSite, DomainName = "www.kept592.example.test", WwwDomain = "www.kept592.example.test", RootDomain = "kept592.example.test", DnsTarget = Target, OwnershipVerifiedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await AgentDataEraser.PreviewAsync(db, other);
        Assert.False(await db.DomainCleanups.AnyAsync(c => c.FormerAgentUserId == other));

        var source = Read(@"src\IPRO.Admin\Controllers\AgentsController.cs");
        Assert.Contains("await IPRO.Scheduler.DomainCleanupQueue.AttemptAsync(_db, _azureDomains, _services.GetService<IConfiguration>(), _logger, released);", source);
        Assert.DoesNotContain("the dangling-binding cleanup is manual by design", source);
    }

    // --------------------------------------------------- the rows there at the release --

    [Fact]
    public async Task Domains_connected_before_the_cut_off_count_as_proven_and_nothing_else_does()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await AdviserAsync(db);
        var before = StartupSchemaRepair.DomainOwnershipCutoffUtc.AddDays(-30);
        AgentDomain Old(string root, string azure, string ssl, DateTime created)
        {
            var row = Row(agentId, websiteId, "www." + root, root);
            row.IsPrimary = false;
            row.AzureBindingStatus = azure;
            row.SslStatus = ssl;
            row.CreatedAt = created;
            row.OwnershipVerifiedAt = null;
            return row;
        }
        var rows = new Dictionary<string, AgentDomain>
        {
            ["live"] = Old("live592.example.test", AgentDomainStatus.Bound, AgentDomainStatus.Bound, before),
            // A live site whose last probe failed: the binding reads pending for a moment, its certificate is in place.
            ["blip"] = Old("blip592.example.test", AgentDomainStatus.BindingPending, AgentDomainStatus.Bound, before),
            ["legacy"] = Old("legacy592.example.test", AgentDomainStatus.Bound, "SslBound", before),
            // Never connected (a squatter's row, or one whose owner never set up DNS).
            ["waiting"] = Old("wait592.example.test", AgentDomainStatus.BindingPending, AgentDomainStatus.BindingPending, before),
            ["exhausted"] = Old("tired592.example.test", AgentDomainStatus.Failed, AgentDomainStatus.BindingPending, before),
            // Connected after the cut-off: the owner's own 411trades.com test was such a row.
            ["late"] = Old("late592.example.test", AgentDomainStatus.Bound, AgentDomainStatus.Bound, StartupSchemaRepair.DomainOwnershipCutoffUtc.AddHours(19)),
        };
        db.AddRange(rows.Values);
        await db.SaveChangesAsync();

        // The database as it is before the release.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE `AgentDomains` DROP COLUMN `OwnershipVerifiedAt`, DROP COLUMN `VerificationToken`");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE `DomainCleanups`");

        // The owner's check before the release names exactly the rows that will need the record.
        var needing = File.ReadAllText(Path.Combine(SolutionRoot(), @"ops\sql\domains-needing-proof.sql"));
        AssertReadOnlyStatement(needing);
        Assert.Contains("'" + StartupSchemaRepair.DomainOwnershipCutoffUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "'", needing);
        var listed = (await RunAsync(db, needing)).Where(r => Convert.ToInt32(r["AgentUserId"], CultureInfo.InvariantCulture) == agentId)
            .Select(r => (string)r["DomainName"]! + " | " + r["why"]).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(new[]
        {
            "www.late592.example.test | added on or after 15 Oct 2026",
            "www.tired592.example.test | never connected",
            "www.wait592.example.test | never connected",
        }, listed);

        await StartupSchemaRepair.EnsureAgentDomainSchemaAsync(db);
        db.ChangeTracker.Clear();
        var first = await db.AgentDomains.AsNoTracking().Where(d => d.AgentUserId == agentId).ToDictionaryAsync(d => d.DomainName);
        await StartupSchemaRepair.EnsureAgentDomainSchemaAsync(db);      // the other app's start, or the next one
        db.ChangeTracker.Clear();
        var after = await db.AgentDomains.AsNoTracking().Where(d => d.AgentUserId == agentId).ToDictionaryAsync(d => d.DomainName);

        foreach (var key in new[] { "live", "blip", "legacy" })
        {
            var row = after[rows[key].DomainName];
            Assert.NotNull(row.OwnershipVerifiedAt);
            Assert.Equal(string.Empty, row.VerificationToken);
            Assert.Equal(first[rows[key].DomainName].OwnershipVerifiedAt, row.OwnershipVerifiedAt);   // once
        }
        foreach (var key in new[] { "waiting", "exhausted", "late" })
            Assert.Null(after[rows[key].DomainName].OwnershipVerifiedAt);

        // The owner's check after the release names the never-connected rows only (the late one needs its record).
        var afterSql = File.ReadAllText(Path.Combine(SolutionRoot(), @"ops\sql\domain-ownership-after-release.sql"));
        AssertReadOnlyStatement(afterSql);
        var findings = (await RunAsync(db, afterSql)).Where(r => r["DomainName"] is string name && name.Contains("592.", StringComparison.Ordinal) || r["DomainName"] == null)
            .Select(r => r["finding"] + " | " + r["DomainName"]).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(new[]
        {
            "unproven, added before 15 Oct | www.tired592.example.test",
            "unproven, added before 15 Oct | www.wait592.example.test",
        }, findings);

        // The late row was connected by the old code: it reads as not connected until it is proven.
        var late = after[rows["late"].DomainName];
        Assert.Equal((AgentDomainStatus.PendingDns, AgentDomainStatus.BindingPending, AgentDomainStatus.BindingPending),
            (late.DnsStatus, late.AzureBindingStatus, late.SslStatus));
        Assert.Equal(AgentDomainStatus.Bound, after[rows["live"].DomainName].AzureBindingStatus);

        // A row proven by its record later keeps its proof, even if a release step has to run again (a start that
        // stopped before the default went back to NULL), and so do the live customers.
        var recorded = Old("recorded592.example.test", AgentDomainStatus.BindingPending, AgentDomainStatus.BindingPending, DateTime.UtcNow);
        recorded.VerificationToken = DomainOwnershipProof.NewToken();
        recorded.OwnershipVerifiedAt = DateTime.UtcNow;
        db.Add(recorded);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE `AgentDomains` ALTER COLUMN `OwnershipVerifiedAt` SET DEFAULT (CURRENT_TIMESTAMP(6))");
        await StartupSchemaRepair.EnsureAgentDomainSchemaAsync(db);
        db.ChangeTracker.Clear();
        Assert.NotNull((await db.AgentDomains.AsNoTracking().SingleAsync(d => d.Id == recorded.Id)).OwnershipVerifiedAt);
        Assert.NotNull((await db.AgentDomains.AsNoTracking().SingleAsync(d => d.DomainName == "www.live592.example.test")).OwnershipVerifiedAt);

        // A row written afterwards, by anyone, starts unproven.
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO `AgentDomains` (`AgentUserId`, `AgentWebsiteId`, `DomainName`, `RootDomain`, `WwwDomain`, `DnsTarget`, `DnsStatus`, `AzureBindingStatus`, `SslStatus`, `IsPrimary`, `LastError`, " +
            "`RetryCount`, `AutoRetryExhausted`, `RootDnsStatus`, `RootRedirectsToWww`, `RootLastError`, `RootAzureBindingStatus`, `RootSslStatus`, `CaaBlockingName`, `CreatedAt`, `UpdatedAt`) " +
            "VALUES ({0}, {1}, 'www.raw592.example.test', 'raw592.example.test', 'www.raw592.example.test', '', 'PendingDns', 'BindingPending', 'BindingPending', 0, '', " +
            "0, 0, 'PendingDns', 0, '', 'NotConfigured', 'NotConfigured', '', {2}, {2})",
            agentId, websiteId, before);
        await StartupSchemaRepair.EnsureAgentDomainSchemaAsync(db);
        Assert.Null((await db.AgentDomains.AsNoTracking().SingleAsync(d => d.DomainName == "www.raw592.example.test")).OwnershipVerifiedAt);
        Assert.True(await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS `Value` FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND LOWER(TABLE_NAME) = 'domaincleanups'").SingleAsync() == 1);

        // Without the record of removals the shred stops before anything is deleted.
        await db.Database.ExecuteSqlRawAsync("DROP TABLE `DomainCleanups`");
        await Assert.ThrowsAsync<InvalidOperationException>(() => AgentDataEraser.EraseAsync(db, agentId, eraseFinancialRecords: true));
        Assert.True(await db.AgentDomains.AnyAsync(d => d.AgentUserId == agentId));
        Assert.True(await db.AgentUsers.AnyAsync(u => u.Id == agentId));
    }

    // ----------------------------------------------------- the review's further cases --

    [Fact]
    public async Task A_name_held_unproven_moves_only_to_whoever_publishes_their_record_and_a_proven_one_never_moves()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (squatter, squatterSite) = await AdviserAsync(db);
        var (owner, _) = await AdviserAsync(db);
        AgentDomain Claim(int agentId, int siteId, string root, bool proven)
        {
            var row = Row(agentId, siteId, "www." + root, root);
            row.OwnershipVerifiedAt = proven ? DateTime.UtcNow.AddDays(-40) : null;
            row.VerificationToken = proven ? "" : DomainOwnershipProof.NewToken();
            row.AzureBindingStatus = proven ? AgentDomainStatus.Bound : AgentDomainStatus.BindingPending;
            row.CreatedAt = DateTime.UtcNow.AddMinutes(-5);   // age makes no difference any more
            return row;
        }
        var squatted = Claim(squatter, squatterSite, "squat592.example.test", proven: false);
        var theirs = Claim(squatter, squatterSite, "theirs592.example.test", proven: false);
        var held = Claim(squatter, squatterSite, "held592.example.test", proven: true);
        theirs.IsPrimary = held.IsPrimary = false;
        db.AddRange(squatted, theirs, held);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("UPDATE `AgentWebsites` SET `CustomDomain` = 'www.squat592.example.test' WHERE `Id` = {0}", squatterSite);
        Txt592();
        var azure = new ScriptedAzure();
        var log = new RecordingLogger();
        WebsiteController Owner()
        {
            var controller = new WebsiteController(
                new WebsiteService(new UnitOfWork(db)), new NullBlob(), new PackageEntitlementService(new UnitOfWork(db), db),
                new AgentService(new UnitOfWork(db), new PasswordHasher<AgentUser>()), new ConfigurationBuilder().Build(), db,
                new DomainCheckService(new NoHttp(), azure, NullLogger<DomainCheckService>.Instance), azure, log.For<WebsiteController>());
            Sign(controller, owner);
            return controller;
        }

        // Nothing published: refused, and told exactly which record moves it here. The squatter keeps the row.
        var first = Owner();
        await first.AddDomain("www.squat592.example.test");
        var refusal = first.TempData["Error"] as string ?? "";
        var ownerValue = await DomainClaimTokens.GetOrCreateAsync(db, owner, "squat592.example.test", "unused");
        Assert.Contains("has not proven it is theirs", refusal);
        Assert.Contains("Name: _ipro-verify", refusal);
        Assert.Contains(ownerValue, refusal);
        Assert.True(await db.AgentDomains.AnyAsync(d => d.Id == squatted.Id));

        // The owner publishes their value: the name moves, created proven; the squatter's site stops naming it.
        Txt592(("_ipro-verify.squat592.example.test", new[] { "\"" + ownerValue + "\"" }));
        await Owner().AddDomain("www.squat592.example.test");
        db.ChangeTracker.Clear();
        var moved = await db.AgentDomains.SingleAsync(d => d.DomainName == "www.squat592.example.test");
        Assert.Equal(owner, moved.AgentUserId);
        Assert.NotNull(moved.OwnershipVerifiedAt);
        Assert.Equal(ownerValue, moved.VerificationToken);
        Assert.Contains((await db.AgentWebsites.SingleAsync(w => w.Id == squatterSite)).CustomDomain, new[] { "www.theirs592.example.test", "www.held592.example.test" });
        Assert.True(await db.DomainCleanups.AnyAsync(c => c.FormerAgentUserId == squatter && c.HostName == "www.squat592.example.test" && c.Reason == DomainCleanupReasons.UnprovenClaimMoved));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("moved to agent", StringComparison.Ordinal));
        // ... its cleanup waits ten minutes, and then the job keeps the address in Azure for the new proven row.
        var waiting = await db.DomainCleanups.SingleAsync(c => c.FormerAgentUserId == squatter && c.HostName == "www.squat592.example.test");
        Assert.True(waiting.NextAttemptAt > DateTime.UtcNow.AddMinutes(9));
        await db.Database.ExecuteSqlRawAsync("UPDATE `DomainCleanups` SET `NextAttemptAt` = UTC_TIMESTAMP(6) - INTERVAL 1 MINUTE WHERE `Id` = {0}", waiting.Id);
        db.ChangeTracker.Clear();
        await Job(db, azure).RunAsync();
        Assert.DoesNotContain("remove www.squat592.example.test", azure.Calls);
        Assert.Equal(DomainCleanupOutcomes.KeptForAnotherRow, (await db.DomainCleanups.AsNoTracking().SingleAsync(c => c.Id == waiting.Id)).Outcome);

        // The holder's own record is up: the domain is the holder's. Proven now, and the newcomer refused.
        Txt592(("_ipro-verify.theirs592.example.test", new[] { "\"" + theirs.VerificationToken + "\"" }));
        var second = Owner();
        await second.AddDomain("www.theirs592.example.test");
        Assert.Equal("That custom domain is already connected to another IPRO account.", second.TempData["Error"]);
        db.ChangeTracker.Clear();
        Assert.NotNull((await db.AgentDomains.SingleAsync(d => d.Id == theirs.Id)).OwnershipVerifiedAt);

        // A proven row never moves, whatever the newcomer publishes.
        var heldValue = await DomainClaimTokens.GetOrCreateAsync(db, owner, "held592.example.test", DomainOwnershipProof.NewToken());
        Txt592(("_ipro-verify.held592.example.test", new[] { "\"" + heldValue + "\"" }));
        var third = Owner();
        await third.AddDomain("www.held592.example.test");
        Assert.Equal("That custom domain is already connected to another IPRO account.", third.TempData["Error"]);
        db.ChangeTracker.Clear();
        Assert.Equal(squatter, (await db.AgentDomains.SingleAsync(d => d.Id == held.Id)).AgentUserId);
    }

    [Fact]
    public async Task Removing_a_domain_forgets_its_value_so_an_old_record_proves_nothing_even_for_the_same_adviser()
    {
        // The review (round 3): A connects the firm's domain, leaves the firm, removes it; the firm's DNS still holds
        // A's record and still points at iPro. A adding it again from his own account must not get it back.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, _) = await AdviserAsync(db);
        var (other, _) = await AdviserAsync(db);

        await Website(db, agentId, new ScriptedAzure()).AddDomain("www.firm592.example.test");
        db.ChangeTracker.Clear();
        var row = await db.AgentDomains.SingleAsync(d => d.AgentUserId == agentId);
        var old = row.VerificationToken;
        Assert.Matches("^ipro-[0-9a-f]{32}$", old);
        Assert.Equal(old, await DomainClaimTokens.GetOrCreateAsync(db, agentId, "firm592.example.test", "unused"));

        await Website(db, agentId, new ScriptedAzure()).RemoveDomain(row.Id, "www.firm592.example.test");
        db.ChangeTracker.Clear();
        Assert.False(await db.DomainClaimTokens.AnyAsync(t => t.AgentUserId == agentId && t.ProvenName == "firm592.example.test"));

        await Website(db, agentId, new ScriptedAzure()).AddDomain("firm592.example.test");   // the short form: the same domain
        db.ChangeTracker.Clear();
        var again = await db.AgentDomains.SingleAsync(d => d.AgentUserId == agentId);
        Assert.NotEqual(old, again.VerificationToken);

        // The old record is still in the firm's DNS: it proves nothing.
        Dns592(("www.firm592.example.test", Platform), ("firm592.example.test", Platform), (Target, Platform));
        Txt592(("_ipro-verify.firm592.example.test", new[] { "\"" + old + "\"" }));
        var azure = new ScriptedAzure();
        await new DomainCheckService(new NoHttp(), azure, NullLogger<DomainCheckService>.Instance).CheckAsync(again);
        Assert.Null(again.OwnershipVerifiedAt);
        Assert.Empty(azure.Calls);

        // Another adviser's value for the same name is their own; values are erased with the adviser.
        Assert.NotEqual(again.VerificationToken, await DomainClaimTokens.GetOrCreateAsync(db, other, "firm592.example.test", DomainOwnershipProof.NewToken()));
        Assert.Contains("(\"DomainClaimTokens\",", Read(@"src\IPRO.DataAccess\AgentDataEraser.cs"));

        // A value no row took (a refused takeover) lapses after 30 days.
        var shown = await DomainClaimTokens.GetOrCreateAsync(db, other, "unused592.example.test", DomainOwnershipProof.NewToken());
        await db.Database.ExecuteSqlRawAsync("UPDATE `DomainClaimTokens` SET `CreatedAt` = UTC_TIMESTAMP(6) - INTERVAL 31 DAY WHERE `AgentUserId` = {0} AND `ProvenName` = 'unused592.example.test'", other);
        Assert.NotEqual(shown, await DomainClaimTokens.GetOrCreateAsync(db, other, "unused592.example.test", DomainOwnershipProof.NewToken()));
        // ... but not one a row carries, however old.
        await db.Database.ExecuteSqlRawAsync("UPDATE `DomainClaimTokens` SET `CreatedAt` = UTC_TIMESTAMP(6) - INTERVAL 31 DAY WHERE `AgentUserId` = {0} AND `ProvenName` = 'firm592.example.test'", agentId);
        Assert.Equal(again.VerificationToken, await DomainClaimTokens.GetOrCreateAsync(db, agentId, "firm592.example.test", DomainOwnershipProof.NewToken()));

        // An adviser's looks at DNS for names held by others are bounded.
        var now = DateTime.UtcNow;
        var id = 900000 + Random.Shared.Next(1, 99999);
        for (var i = 0; i < WebsiteController.TakeoverLooks.PerWindow; i++) Assert.True(WebsiteController.TakeoverLooks.Allow(id, now));
        Assert.False(WebsiteController.TakeoverLooks.Allow(id, now));
        Assert.True(WebsiteController.TakeoverLooks.Allow(id, now + WebsiteController.TakeoverLooks.Window + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Only_a_proven_row_keeps_an_address_in_Azure_and_every_address_check_wants_the_proof()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await AdviserAsync(db);
        var (other, otherSite) = await AdviserAsync(db);
        var unproven = Row(other, otherSite, "www.loose592.example.test", "loose592.example.test");
        unproven.OwnershipVerifiedAt = null;   // bound by the old code during the release, say
        db.Add(unproven);
        db.DomainCleanups.Add(new DomainCleanup { HostName = "www.loose592.example.test", FormerAgentUserId = agentId, Reason = DomainCleanupReasons.RemovedByAdviser });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("UPDATE `AgentWebsites` SET `CustomDomain` = 'www.loose592.example.test' WHERE `Id` = {0}", otherSite);

        var azure = new ScriptedAzure();
        await DomainCleanupQueue.RunDueAsync(db, azure, new ConfigurationBuilder().Build(), NullLogger<DomainOwnership592Tests>.Instance);
        Assert.Equal(new[] { "remove www.loose592.example.test" }, azure.Calls);

        // The address checks that read "Bound" ask for the proof too.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:TemporarySiteRootDomain"] = "247advisers.com" }).Build();
        Assert.Null(await IPRO.Web.Infrastructure.ClientPortalSignInScope.AdviserForHostAsync(db, config, "www.loose592.example.test"));
        Assert.Equal("temp592.247advisers.com", await AgentSiteAddress.HostAsync(db, other, "temp592.247advisers.com"));
        Assert.False(await Serves(db, "www.loose592.example.test", other));
        await db.Database.ExecuteSqlRawAsync("UPDATE `AgentDomains` SET `OwnershipVerifiedAt` = UTC_TIMESTAMP(6) WHERE `Id` = {0}", unproven.Id);
        db.ChangeTracker.Clear();
        Assert.Equal(other, await IPRO.Web.Infrastructure.ClientPortalSignInScope.AdviserForHostAsync(db, config, "www.loose592.example.test"));
        Assert.Equal("www.loose592.example.test", await AgentSiteAddress.HostAsync(db, other, "temp592.247advisers.com"));
        Assert.True(await Serves(db, "www.loose592.example.test", other));

        // A name only a website's own field holds (no domain row) is nobody's address; the free site still is.
        await db.Database.ExecuteSqlRawAsync("UPDATE `AgentWebsites` SET `CustomDomain` = 'www.fieldonly592.example.test' WHERE `Id` = {0}", websiteId);
        Assert.False(await Serves(db, "www.fieldonly592.example.test", agentId));
        Assert.Null(await IPRO.Web.Infrastructure.ClientPortalSignInScope.AdviserForHostAsync(db, config, "www.fieldonly592.example.test"));
        var freeSite = (await db.AgentUsers.AsNoTracking().SingleAsync(u => u.Id == agentId)).DomainName;
        Assert.True(await Serves(db, freeSite, agentId));

        foreach (var file in new[]
                 {
                     @"src\IPRO.Web\Infrastructure\PortalUrlHelper.cs", @"src\IPRO.Web\Infrastructure\ClientPortalUrls.cs",
                     @"src\IPRO.Web\Infrastructure\ShortAddressRedirect.cs", @"src\IPRO.Web\Infrastructure\ClientPortalSignInScope.cs",
                     @"src\IPRO.Web\Controllers\PollVoteController.cs", @"src\IPRO.Web\Controllers\PublicWebsiteController.cs",
                     @"src\IPRO.DataAccess\AgentSiteAddress.cs",
                 })
        {
            // Every query of these files that trusts a bound row asks for the proof beside it.
            Assert.True(CountOf(Read(file), "OwnershipVerifiedAt != null") >= 1, file);
        }
    }

    [Fact]
    public async Task SuperAdmin_cannot_set_a_setup_domain_outside_the_free_site_zone()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, _) = await AdviserAsync(db);
        var (legacyId, _) = await AdviserAsync(db);
        await db.Database.ExecuteSqlRawAsync("UPDATE `AgentUsers` SET `DomainName` = 'legacy592.example.test' WHERE `Id` = {0}", legacyId);

        async Task<(IActionResult Result, IPRO.Admin.Controllers.AgentsController Controller)> EditAsync(int id, string domain)
        {
            db.ChangeTracker.Clear();
            var agent = await db.AgentUsers.AsNoTracking().SingleAsync(u => u.Id == id);
            var uow = new UnitOfWork(db);
            var controller = new IPRO.Admin.Controllers.AgentsController(
                new AgentService(uow, new PasswordHasher<AgentUser>()), new WebsiteService(uow), uow, null!,
                new PasswordHasher<AgentUser>(), NullLogger<IPRO.Admin.Controllers.AgentsController>.Instance, new NullAudit(), db,
                new ServiceCollection().BuildServiceProvider());
            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "1"), new Claim("Role", "SuperAdmin") }, "test"))
            };
            controller.ControllerContext = new ControllerContext { HttpContext = context };
            controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NullTempData());
            var model = new IPRO.Admin.Models.AgentEditViewModel
            {
                Id = id, UserName = agent.UserName, Email = agent.Email, FirstName = agent.FirstName, LastName = agent.LastName,
                CompanyName = agent.CompanyName, City = "Toronto", Province = agent.Province, PostalCode = "M5V 1A1", Country = agent.Country,
                Phone = "416-555-0100", BusinessType = "Insurance", PackageId = agent.PackageId, DomainName = domain, IsActive = true,
            };
            return (await controller.Edit(id, model), controller);
        }

        var (refused, refusing) = await EditAsync(agentId, "www.victim592.example.test");
        Assert.IsType<ViewResult>(refused);
        Assert.Contains(refusing.ModelState.Values.SelectMany(v => v.Errors), e => e.ErrorMessage.Contains("must be under 247advisers.com", StringComparison.Ordinal));
        Assert.EndsWith(".247advisers.com", (await db.AgentUsers.AsNoTracking().SingleAsync(u => u.Id == agentId)).DomainName);

        // An older record keeps its value through an unrelated edit.
        var (kept, keeping) = await EditAsync(legacyId, "legacy592.example.test");
        Assert.DoesNotContain(keeping.ModelState.Values.SelectMany(v => v.Errors), e => e.ErrorMessage.Contains("must be under", StringComparison.Ordinal));
    }

    // ---------------------------------------------------- the owner's read-only checks --

    [Fact]
    public async Task The_owners_SELECT_lists_domains_that_changed_adviser_or_were_removed_and_nothing_else()
    {
        var sql = File.ReadAllText(Path.Combine(SolutionRoot(), @"ops\sql\domain-ownership-history.sql"));
        AssertReadOnlyStatement(sql);

        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (aId, aSite) = await AdviserAsync(db);
        var (bId, bSite) = await AdviserAsync(db);
        var long_ago = DateTime.UtcNow.AddDays(-30);
        // Served for A, and now B's row: changed adviser.
        db.Add(Row(bId, bSite, "www.moved592.example.test", "moved592.example.test"));
        // Served for A, no row now: removed.
        // Served for A and still A's: innocent. Served on A's free site address: innocent.
        db.Add(Row(aId, aSite, "www.stays592.example.test", "stays592.example.test"));
        // A row whose website is another adviser's: re-parented.
        var crossed = Row(bId, aSite, "www.crossed592.example.test", "crossed592.example.test");
        crossed.IsPrimary = false;
        db.Add(crossed);
        await db.SaveChangesAsync();
        foreach (var (site, sourceDomain) in new[] { (aSite, "www.moved592.example.test"), (aSite, "www.removed592.example.test"), (aSite, "www.stays592.example.test"), (aSite, "someone592.247advisers.com") })
            db.Add(new WebsitePageView { AgentWebsiteId = site, SourceDomain = sourceDomain, Path = "/", VisitorHash = "v", CreatedAt = long_ago });
        db.Add(new DomainCleanup { HostName = "www.released592.example.test", FormerAgentUserId = aId, Reason = DomainCleanupReasons.RemovedByAdviser, Attempts = 3, LastError = "503" });
        db.Add(new DomainCleanup { HostName = "www.dotted592.example.test.", FormerAgentUserId = aId, Reason = DomainCleanupReasons.RemovedByAdviser, Attempts = 1, DoneAt = DateTime.UtcNow, Outcome = DomainCleanupOutcomes.Refused });
        await db.SaveChangesAsync();

        var rows = await RunAsync(db, sql);
        string Line(Dictionary<string, object?> r) => r["host"] + " | " + r["finding"];
        var lines = rows.Select(Line).Where(l => l.Contains("592.", StringComparison.Ordinal)).OrderBy(l => l, StringComparer.Ordinal).ToList();
        Assert.Equal(new[]
        {
            "www.crossed592.example.test | row's website belongs to another adviser",
            "www.dotted592.example.test. | removal refused: not a plain host name",
            "www.moved592.example.test | served for one adviser, now another adviser's row",
            "www.released592.example.test | removed, Azure not yet confirmed",
            "www.removed592.example.test | served for an adviser, no row names it now",
        }, lines);
        var moved = rows.Single(r => (string)r["host"]! == "www.moved592.example.test");
        Assert.Equal(aId, Convert.ToInt32(moved["earlier_adviser"], CultureInfo.InvariantCulture));
        Assert.Equal(bId, Convert.ToInt32(moved["adviser_now"], CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task The_orphan_binding_check_lists_bound_names_no_row_or_website_names()
    {
        var script = File.ReadAllText(Path.Combine(SolutionRoot(), @"ops\domain-ownership\orphan-bindings.sh")).Replace("\r\n", "\n");
        // Read-only against Azure: it lists, it never changes.
        Assert.Contains("az webapp config hostname list", script);
        Assert.DoesNotMatch(new Regex(@"\baz\b[^\n]*\b(delete|add|create|update|set|bind|unbind|remove)\b", RegexOptions.IgnoreCase), script);

        var sql = File.ReadAllText(Path.Combine(SolutionRoot(), @"ops\sql\orphan-bindings.sql"));
        AssertReadOnlyStatement(sql);

        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (aId, aSite) = await AdviserAsync(db);
        db.Add(Row(aId, aSite, "www.lavenuebakery.com", "lavenuebakery.com"));
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("UPDATE `AgentWebsites` SET `CustomDomain` = 'www.4ipro.com' WHERE `Id` = {0}", aSite);
        var (bId, _) = await AdviserAsync(db);
        await db.Database.ExecuteSqlRawAsync("UPDATE `AgentUsers` SET `DomainName` = 'www.drhug.ca' WHERE `Id` = {0}", bId);

        var found = (await RunAsync(db, sql)).Select(r => (string)r["host"]! + " | " + r["finding"]).ToList();
        Assert.Contains("www.411trades.com | bound in Azure, no domain row or website names it", found);
        Assert.Contains("www.4ipro.com | bound in Azure, named only by a website's own field", found);    // no longer served (592)
        Assert.Contains("www.drhug.ca | bound in Azure, named only by a setup address", found);
        Assert.DoesNotContain(found, f => f.StartsWith("www.lavenuebakery.com |", StringComparison.Ordinal));   // a row names it
        Assert.DoesNotContain(found, f => f.StartsWith("lavenuebakery.com |", StringComparison.Ordinal));       // that row's short address
        Assert.DoesNotContain(found, f => f.StartsWith("app.iproadvisers.com |", StringComparison.Ordinal));    // iPro's own
        Assert.DoesNotContain(found, f => f.StartsWith("*.247advisers.com |", StringComparison.Ordinal));
    }

    [Fact]
    public void The_guides_say_how_to_prove_a_domain_and_what_happens_when_one_is_removed()
    {
        var domains = Read(@"DOCS\05_DOMAINS_AND_LEADS.md");
        Assert.Contains("### Prove the domain is yours (TXT record)", domains);
        Assert.Contains("`_ipro-verify`", domains);
        Assert.Contains("### What happens when a domain is removed", domains);

        var admin = Read(@"DOCS\07_SUPER_ADMIN.md");
        Assert.DoesNotContain("The agent's custom domain is not touched by deletion.", admin);
        Assert.Contains("Not proven yet", admin);
    }

    // ------------------------------------------------------------------- plumbing --

    private static void Dns592(params (string Host, string Address)[] records)
    {
        var table = records.ToDictionary(r => r.Host, r => new[] { IPAddress.Parse(r.Address) }, StringComparer.OrdinalIgnoreCase);
        DomainCheckService.ResolveHook = (host, ct) => Task.FromResult(table.TryGetValue(host, out var found) ? found : Array.Empty<IPAddress>());
    }

    private sealed class TxtLog { public List<string> Asked { get; } = new(); }

    // Fake DNS-over-HTTPS: the TXT values each name holds; every other name has none.
    private static TxtLog Txt592(params (string Name, string[] Values)[] records)
    {
        var log = new TxtLog();
        DomainOwnershipProof.LookupHook = (address, _) =>
        {
            log.Asked.Add(address);
            var match = records.FirstOrDefault(r => address.Contains("name=" + Uri.EscapeDataString(r.Name) + "&", StringComparison.Ordinal));
            return Task.FromResult<string?>(match.Name == null ? "{\"Status\":3}" : Answer(match));
        };
        return log;
    }

    private static string Answer((string Name, string[] Values) record) =>
        "{\"Status\":0,\"Answer\":[" + string.Join(",", record.Values.Select(v =>
            "{\"name\":\"" + record.Name + ".\",\"type\":16,\"TTL\":300,\"data\":" + System.Text.Json.JsonSerializer.Serialize(v) + "}")) + "]}";

    private static AgentDomain NewDomain(string root, string token) => new()
    {
        DomainName = "www." + root, WwwDomain = "www." + root, RootDomain = root, DnsTarget = Target, VerificationToken = token,
    };

    private static AgentDomain Row(int agentId, int websiteId, string name, string root) => new()
    {
        AgentUserId = agentId, AgentWebsiteId = websiteId, DomainName = name, WwwDomain = name, RootDomain = root,
        DnsTarget = Target, IsPrimary = true, OwnershipVerifiedAt = DateTime.UtcNow.AddDays(-60),
        DnsStatus = AgentDomainStatus.Bound, AzureBindingStatus = AgentDomainStatus.Bound, SslStatus = AgentDomainStatus.Bound,
    };

    private static void AssertReadOnlyStatement(string sql)
    {
        var code = string.Join("\n", sql.Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        Assert.DoesNotMatch(new Regex(@"\b(INSERT|UPDATE|DELETE|DROP|ALTER|CREATE|TRUNCATE|GRANT|SET|CALL|LOAD|RENAME|LOCK|HANDLER)\b|\bREPLACE\s+INTO\b", RegexOptions.IgnoreCase), code);
        Assert.Equal(1, code.Count(c => c == ';'));
    }

    private static async Task<List<Dictionary<string, object?>>> RunAsync(IPRODbContext db, string sql)
    {
        var rows = new List<Dictionary<string, object?>>();
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i)));
        return rows;
    }

    // Whether the public site on this host is that adviser's.
    private static async Task<bool> Serves(IPRODbContext db, string host, int agentId) =>
        await PublicSite(db, host).Index() is ViewResult { Model: IPRO.Web.Models.PublicWebsiteViewModel model } && model.Website.AgentUserId == agentId;

    // A paying adviser with a published website and a home page, whose package has extra domains.
    private static async Task<(int AgentId, int WebsiteId)> AdviserAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T592-{Guid.NewGuid():N}")[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        var template = new WebsiteTemplate { Name = "Modern", TemplateKey = ($"t592-{Guid.NewGuid():N}")[..16], BusinessType = "All", IsActive = true };
        db.AddRange(rule, template);
        await db.SaveChangesAsync();
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.InstantWebsite, FeatureName = "Website", IsIncluded = true });
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.MultiDomainSupport, FeatureName = "Domains", IsIncluded = true });
        var agent = new AgentUser
        {
            UserName = ($"t592-{Guid.NewGuid():N}")[..20], Email = ($"t592-{Guid.NewGuid():N}")[..14] + "@example.test",
            FirstName = "Sam", LastName = "Smith", CompanyName = "Smith Trades",
            DomainName = ($"t592-{Guid.NewGuid():N}")[..24] + ".247advisers.com", Country = "Canada", Province = "Ontario",
            PackageId = rule.Id, TrialEndsAt = null,
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        db.Add(new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 60m, Status = BillingStatus.Active,
            Period = BillingPeriod.Monthly, StartDate = DateTime.UtcNow.AddDays(-40), NextBillingDate = DateTime.UtcNow.AddDays(20),
        });
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, SiteTitle = "Mine", IsPublished = true };
        db.Add(website);
        await db.SaveChangesAsync();
        db.Add(new WebsitePage { AgentWebsiteId = website.Id, Title = "Home", Slug = "home", NavigationLabel = "Home", IsHomePage = true, IsPublished = true });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (agent.Id, website.Id);
    }

    private static DomainAutomationJob Job(IPRODbContext db, IAzureDomainAutomationService azure) =>
        new(db, new NeverCheck(), new NullEmail(), new ConfigurationBuilder().Build(), NullLogger<DomainAutomationJob>.Instance, azure);

    private static PublicWebsiteController PublicSite(IPRODbContext db, string host)
    {
        var controller = new PublicWebsiteController(
            db, new PackageEntitlementService(new UnitOfWork(db), db), new NullEmail(),
            NullLogger<PublicWebsiteController>.Instance, new ConfigurationBuilder().Build(),
            DataProtectionProvider.Create($"t592-{Guid.NewGuid():N}"), new NullBlob(),
            new MemoryCache(new MemoryCacheOptions()), new NullConsent());
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NullTempData());
        return controller;
    }

    private static WebsiteController Website(IPRODbContext db, int agentId, IAzureDomainAutomationService azure)
    {
        var controller = new WebsiteController(
            new WebsiteService(new UnitOfWork(db)), new NullBlob(), new PackageEntitlementService(new UnitOfWork(db), db),
            new AgentService(new UnitOfWork(db), new PasswordHasher<AgentUser>()), new ConfigurationBuilder().Build(), db,
            new NeverCheck(), azure, NullLogger<WebsiteController>.Instance);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString(CultureInfo.InvariantCulture)) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NullTempData());
        return controller;
    }

    private static IPRO.Admin.Controllers.DomainsController Domains(IPRODbContext db)
    {
        var controller = new IPRO.Admin.Controllers.DomainsController(
            db, Microsoft.Extensions.Options.Options.Create(new AzureDomainAutomationOptions()), new ScriptedAzure(), new NeverCheck(),
            NullLogger<IPRO.Admin.Controllers.DomainsController>.Instance, new NullAudit());
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "1") }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NullTempData());
        return controller;
    }

    private static string SolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static void Sign(Controller controller, int agentId)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString(CultureInfo.InvariantCulture)) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NullTempData());
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(SolutionRoot(), relative)).Replace("\r\n", "\n");

    private sealed class ScriptedAzure : IAzureDomainAutomationService
    {
        public bool IsConfigured => true;
        public List<string> Calls { get; } = new();
        public AzureDomainAutomationResult RemoveAnswer { get; set; } = new() { Success = true, Message = "removed" };

        // The www name comes back "bound, certificate on its way": the check then never needs its HTTP probe.
        public Task<AzureDomainAutomationResult> EnsureDomainAsync(string hostName, CancellationToken cancellationToken = default)
        {
            Calls.Add("ensure " + hostName);
            return Task.FromResult(new AzureDomainAutomationResult { Success = true, BindingCreated = true, Message = "issuing" });
        }

        public Task<AzureDomainAutomationResult> EnsureRootDomainAsync(string hostName, CancellationToken cancellationToken = default)
        {
            Calls.Add("ensure-root " + hostName);
            return Task.FromResult(new AzureDomainAutomationResult { Success = true, BindingCreated = true, Message = "issuing" });
        }

        public Task<AzureDomainAutomationResult> RemoveDomainAsync(string hostName, CancellationToken cancellationToken = default)
        {
            Calls.Add("remove " + hostName);
            return Task.FromResult(RemoveAnswer);
        }
    }

    private sealed class NeverCheck : IDomainCheckService
    {
        public Task<bool> CheckAsync(AgentDomain domain, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class NoHttp : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("these checks are settled by fake DNS and a fake Azure, never by fetching");
    }

    private sealed class NullAudit : IAdminAuditLogService
    {
        public Task LogAsync(int adminUserId, string adminUsername, string action, string details) => Task.CompletedTask;
    }

    private sealed class NullEmail : IPRO.Email.IEmailService
    {
        public Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) => Task.FromResult(true);
        public Task<IPRO.Email.EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) => Task.FromResult(IPRO.Email.EmailSendResult.Sent());
        public Task<bool> SendBulkAsync(IEnumerable<IPRO.Email.EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
    }

    private sealed class NullBlob : IBlobStorageService
    {
        public Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate) => Task.FromResult("https://blob.example.test/x");
        public Task<bool> DeleteAsync(string blobUrl) => Task.FromResult(true);
        public Task<Stream?> DownloadAsync(string blobUrl) => Task.FromResult<Stream?>(null);
        public Task<List<string>> ListAsync(string containerName) => Task.FromResult(new List<string>());
        public string GetPublicUrl(string containerName, string fileName) => $"https://blob.example.test/{containerName}/{fileName}";
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => Task.CompletedTask;
    }

    private sealed class NullConsent : IEmailConsentService
    {
        public bool IsSuppressed(Client client, EmailChannel channel, bool designSurvivesOptOut = false) => false;
        public Task<SuppressionResult> SuppressAllAsync(Client client, string source) => throw new NotSupportedException();
        public Task ResubscribeAsync(Client client) => throw new NotSupportedException();
        public bool LiftBounceSuppression(Client client) => throw new NotSupportedException();
        public Task<int> CancelSuppressedDripEnrollmentsAsync(int batchLimit = 500) => Task.FromResult(0);
        public Task<string> GetOrCreateTokenAsync(Client client) => Task.FromResult("tok");
        public string BuildPreferencesUrl(string token) => $"https://example.test/prefs/{token}";
    }

    private sealed class NullTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
