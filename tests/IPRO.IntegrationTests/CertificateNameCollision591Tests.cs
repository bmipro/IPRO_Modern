using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Utility;
using IPRO.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// 591 (2026-10-18), found by 589's independent review; older than 589. The managed certificate for a name
// was "managed-" + the name with its dots as dashes, cut at 80 characters, and that is not one name per
// certificate: www.abc-def.com and www-abc.def.com share one, and so do two long names that agree on their
// first 72 dashed characters. An adviser could add a name that shares another adviser's certificate name and
// press Remove: the binding DELETE missed (a different name), the certificate DELETE named the other
// adviser's certificate. And connecting such a name ordered that certificate again for itself.
//
//   - Before any certificate PUT or DELETE the automation asks Azure what the name holds (GET), and touches it
//     only when it is missing (an order) or was ordered for this very host (canonicalName). A certificate of
//     another host is left alone, with a Warning.
//   - New certificates get a name of their own (the readable start, then 16 hex digits of the host's SHA-256),
//     so a name can no longer be taken ahead of its owner; a certificate ordered before 591 keeps its old name.
//   - Removing a domain sends Azure nothing for a host another domain row still names.
//   - ops/sql/certificate-name-collisions.sql lists the pairs already in a database (read-only).
//
// In ConfigHazardTests' collection: AzureDomainAutomationService.SiteNameProvider is a seam for the whole
// process.
[Collection(ConfigHazardTests.AzureStatics)]
public class CertificateNameCollision591Tests : IDisposable
{
    public CertificateNameCollision591Tests() => AzureDomainAutomationService.SiteNameProvider = () => "ipro-prod-web";
    public void Dispose() => AzureDomainAutomationService.SiteNameProvider = () => Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");

    // The two shapes of the collision, each a victim's name and a name another adviser could type.
    private const string Victim = "www.abc591-def.example.test";   // managed-www-abc591-def-example-test
    private const string Twin = "www-abc591.def.example.test";     // the same
    private static readonly string LongPrefix = new string('a', 63) + "." + new string('b', 20);
    private static readonly string LongVictim = LongPrefix + ".victim591.example.test";
    private static readonly string LongTwin = LongPrefix + ".twin591.example.test";

    [Fact]
    public void The_two_shapes_really_share_the_old_name()
    {
        // The premise: without it every test below would pass for the wrong reason.
        Assert.Equal(ManagedCertificateNames.Legacy(Victim), ManagedCertificateNames.Legacy(Twin));
        Assert.Equal("managed-www-abc591-def-example-test", ManagedCertificateNames.Legacy(Victim));
        Assert.Equal(ManagedCertificateNames.Legacy(LongVictim), ManagedCertificateNames.Legacy(LongTwin));
        Assert.Equal(80, ManagedCertificateNames.Legacy(LongVictim).Length);
        foreach (var name in new[] { Victim, Twin, LongVictim, LongTwin })
        {
            Assert.True(PlatformHostNames.TryCanonicalHost(name, out _), name);
            Assert.False(PlatformHostNames.IsPlatformHost(name), name);
        }
    }

    // ------------------------------------------------------------------- removing --

    [Fact]
    public async Task Removing_a_name_that_shares_a_certificate_name_leaves_the_other_advisers_certificate_alone()
    {
        foreach (var (victim, twin) in new[] { (Victim, Twin), (LongVictim, LongTwin) })
        {
            var arm = new FakeArm();
            arm.Certificates[ManagedCertificateNames.Legacy(victim)] = new(victim, "VICTIMTHUMB");
            var log = new RecordingLogger();

            var result = await Service(arm, log).RemoveDomainAsync(twin);

            // The twin's own binding goes (it is the twin's name); nothing is deleted under the victim's certificate name.
            Assert.True(result.Success, result.Message);
            Assert.Contains(arm.Requests, r => r.Method == HttpMethod.Delete && r.Url.Contains("/hostNameBindings/" + twin + "?", StringComparison.Ordinal));
            Assert.DoesNotContain(arm.Requests, r => r.Method == HttpMethod.Delete && r.Url.Contains("/certificates/", StringComparison.Ordinal));
            Assert.Equal(victim, arm.Certificates[ManagedCertificateNames.Legacy(victim)].CanonicalName);
            Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning
                && l.Text.Contains(ManagedCertificateNames.Legacy(victim), StringComparison.Ordinal)
                && l.Text.Contains("ordered for " + victim, StringComparison.Ordinal)
                && l.Text.Contains(twin, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task An_advisers_own_certificate_still_goes_with_their_domain_under_its_old_name_or_its_new_one()
    {
        var arm = new FakeArm();
        const string old = "www.old591.example.test";
        const string fresh = "www.fresh591.example.test";
        const string never = "www.never591.example.test";
        arm.Certificates[ManagedCertificateNames.Legacy(old)] = new(old, "OLDTHUMB");
        // Azure echoes the name as it was ordered; capitals do not make it someone else's.
        arm.Certificates[ManagedCertificateNames.For(fresh)] = new(fresh.ToUpperInvariant(), "FRESHTHUMB");
        var service = Service(arm, new RecordingLogger());

        Assert.True((await service.RemoveDomainAsync(old)).Success);
        Assert.True((await service.RemoveDomainAsync(fresh)).Success);
        Assert.Empty(arm.Certificates);
        Assert.Contains(arm.Requests, r => r.Method == HttpMethod.Delete && r.Url.Contains("/certificates/" + ManagedCertificateNames.Legacy(old) + "?", StringComparison.Ordinal));
        Assert.Contains(arm.Requests, r => r.Method == HttpMethod.Delete && r.Url.Contains("/certificates/" + ManagedCertificateNames.For(fresh) + "?", StringComparison.Ordinal));

        // A name with no certificate at all: the binding goes, and no certificate DELETE is sent blind.
        arm.Requests.Clear();
        Assert.True((await service.RemoveDomainAsync(never)).Success);
        Assert.Contains(arm.Requests, r => r.Method == HttpMethod.Delete && r.Url.Contains("/hostNameBindings/" + never + "?", StringComparison.Ordinal));
        Assert.DoesNotContain(arm.Requests, r => r.Method == HttpMethod.Delete && r.Url.Contains("/certificates/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_certificate_with_no_canonical_name_is_nobodys_to_delete()
    {
        // A certificate someone uploaded by hand under a managed- name says no host: it is not this adviser's.
        var arm = new FakeArm();
        const string host = "www.upload591.example.test";
        arm.Certificates[ManagedCertificateNames.Legacy(host)] = new(null, "UPLOADTHUMB");
        var log = new RecordingLogger();

        Assert.True((await Service(arm, log).RemoveDomainAsync(host)).Success);
        Assert.True(arm.Certificates.ContainsKey(ManagedCertificateNames.Legacy(host)));
        Assert.DoesNotContain(arm.Requests, r => r.Method == HttpMethod.Delete && r.Url.Contains("/certificates/", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains(ManagedCertificateNames.Legacy(host), StringComparison.Ordinal));
    }

    // ----------------------------------------------------------------- connecting --

    [Fact]
    public async Task Connecting_a_name_that_shares_a_certificate_name_never_orders_over_the_other_advisers_certificate()
    {
        foreach (var (victim, twin) in new[] { (Victim, Twin), (LongVictim, LongTwin) })
        {
            var arm = new FakeArm { IssueOnPut = true };
            arm.Certificates[ManagedCertificateNames.Legacy(victim)] = new(victim, "VICTIMTHUMB");
            var log = new RecordingLogger();

            var result = await Service(arm, log).EnsureDomainAsync(twin);

            Assert.True(result.Success, result.Message);
            Assert.DoesNotContain(arm.Requests, r => r.Method == HttpMethod.Put && r.Url.Contains("/certificates/" + ManagedCertificateNames.Legacy(victim) + "?", StringComparison.Ordinal));
            Assert.Equal(new CertificateState(victim, "VICTIMTHUMB"), arm.Certificates[ManagedCertificateNames.Legacy(victim)]);
            // The twin gets a certificate of its own, under its own name, and never the victim's thumbprint.
            var own = arm.Requests.Single(r => r.Method == HttpMethod.Put && r.Url.Contains("/certificates/", StringComparison.Ordinal));
            Assert.Contains("/certificates/" + ManagedCertificateNames.For(twin) + "?", own.Url);
            Assert.Contains("\"canonicalName\":\"" + twin + "\"", own.Body);
            Assert.DoesNotContain(arm.Requests, r => r.Body.Contains("VICTIMTHUMB", StringComparison.Ordinal));
            Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning
                && l.Text.Contains(ManagedCertificateNames.Legacy(victim), StringComparison.Ordinal)
                && l.Text.Contains("ordered for " + victim, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_name_connected_first_cannot_take_the_certificate_name_of_one_connected_later()
    {
        // The squat: the twin is connected before the victim. Each still ends with a certificate for itself.
        foreach (var (victim, twin) in new[] { (Victim, Twin), (LongVictim, LongTwin) })
        {
            var arm = new FakeArm();
            var service = Service(arm, new RecordingLogger());

            Assert.True((await service.EnsureDomainAsync(twin)).Success);
            Assert.True((await service.EnsureDomainAsync(victim)).Success);

            Assert.Equal(twin, arm.Certificates[ManagedCertificateNames.For(twin)].CanonicalName);
            Assert.Equal(victim, arm.Certificates[ManagedCertificateNames.For(victim)].CanonicalName);
            Assert.Equal(2, arm.Certificates.Count);
        }
    }

    [Fact]
    public async Task A_certificate_ordered_before_591_keeps_its_name_through_both_passes()
    {
        // In flight at the release: ordered under the old name, not issued yet, and visible to a read (as the fake
        // has it; should Azure answer 404 until the order completes, one more certificate of this host's own is
        // ordered under its new name, and removal deletes both). It is not ordered a second time here.
        const string host = "www.inflight591.example.test";
        var legacy = ManagedCertificateNames.Legacy(host);
        var arm = new FakeArm();
        arm.Certificates[legacy] = new(host, null);
        var service = Service(arm, new RecordingLogger());

        var first = await service.EnsureDomainAsync(host);
        Assert.True(first.Success);
        Assert.False(first.SslBound);
        Assert.Equal(legacy, Assert.Single(arm.Certificates.Keys));

        arm.Certificates[legacy] = new(host, "ISSUED591");
        var second = await service.EnsureDomainAsync(host);
        Assert.True(second.SslBound);
        Assert.Contains(arm.Requests, r => r.Method == HttpMethod.Put && r.Url.Contains("/hostNameBindings/" + host + "?", StringComparison.Ordinal) && r.Body.Contains("\"thumbprint\":\"ISSUED591\"", StringComparison.Ordinal));
        Assert.DoesNotContain(arm.Requests, r => r.Url.Contains("/certificates/" + ManagedCertificateNames.For(host) + "?", StringComparison.Ordinal) && r.Method != HttpMethod.Get);
        Assert.Equal(legacy, Assert.Single(arm.Certificates.Keys));
    }

    [Fact]
    public async Task A_new_name_held_by_another_hosts_certificate_is_refused_not_ordered_over()
    {
        // Sixteen hex digits make this all but impossible; if it ever happens, nothing is ordered over it.
        const string host = "www.held591.example.test";
        var arm = new FakeArm();
        arm.Certificates[ManagedCertificateNames.For(host)] = new("www.someone591.example.test", "SOMEONETHUMB");
        var log = new RecordingLogger();

        var result = await Service(arm, log).EnsureDomainAsync(host);

        Assert.False(result.Success);
        Assert.True(result.BindingCreated);
        Assert.DoesNotContain(arm.Requests, r => r.Method == HttpMethod.Put && r.Url.Contains("/certificates/", StringComparison.Ordinal));
        Assert.DoesNotContain(arm.Requests, r => r.Body.Contains("SOMEONETHUMB", StringComparison.Ordinal));
        Assert.Equal("www.someone591.example.test", arm.Certificates[ManagedCertificateNames.For(host)].CanonicalName);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("ordered for www.someone591.example.test", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_order_answered_for_another_host_is_never_bound()
    {
        // The GET said the name was free; the order came back for another host (a race, or Azure keeping what
        // the name held). Its thumbprint is not put on this host's binding.
        const string host = "www.race591.example.test";
        var arm = new FakeArm { IssueOnPut = true, AnswerPutsFor = "www.other591.example.test" };

        var result = await Service(arm, new RecordingLogger()).EnsureDomainAsync(host);

        Assert.False(result.Success);
        Assert.False(result.SslBound);
        Assert.DoesNotContain(arm.Requests, r => r.Url.Contains("/hostNameBindings/", StringComparison.Ordinal) && r.Body.Contains("thumbprint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_Azure_cannot_say_what_a_certificate_name_holds_nothing_is_ordered_or_deleted()
    {
        const string host = "www.unsure591.example.test";
        var arm = new FakeArm { FailCertificateReads = HttpStatusCode.InternalServerError };
        arm.Certificates[ManagedCertificateNames.Legacy(host)] = new(host, "UNSURETHUMB");
        var service = Service(arm, new RecordingLogger());

        var removed = await service.RemoveDomainAsync(host);
        Assert.False(removed.Success);
        var connected = await service.EnsureDomainAsync(host);
        Assert.False(connected.Success);
        Assert.True(connected.BindingCreated);

        Assert.DoesNotContain(arm.Requests, r => r.Method != HttpMethod.Get && r.Url.Contains("/certificates/", StringComparison.Ordinal));
        Assert.True(arm.Certificates.ContainsKey(ManagedCertificateNames.Legacy(host)));
    }

    [Fact]
    public void A_new_certificate_name_is_the_hosts_alone_and_fits_Azure()
    {
        foreach (var (a, b) in new[] { (Victim, Twin), (LongVictim, LongTwin) })
            Assert.NotEqual(ManagedCertificateNames.For(a), ManagedCertificateNames.For(b));

        foreach (var host in new[] { Victim, Twin, LongVictim, LongTwin, "a.b", "www.example.test" })
        {
            var name = ManagedCertificateNames.For(host);
            Assert.Matches("^managed--[a-z0-9][a-z0-9-]*-[0-9a-f]{16}$", name);
            Assert.True(name.Length <= ManagedCertificateNames.MaxLength, name);
            Assert.NotEqual(ManagedCertificateNames.Legacy(host), name);
            Assert.Equal(name, ManagedCertificateNames.For(host));
        }

        // Still readable in the portal: the dashed name follows "managed--".
        Assert.StartsWith("managed--www-abc591-def-example-test-", ManagedCertificateNames.For(Victim));
        Assert.StartsWith("managed--" + new string('a', 40), ManagedCertificateNames.For(LongVictim));
    }

    [Fact]
    public async Task A_name_built_to_match_a_new_name_under_the_old_scheme_cannot_block_its_owner()
    {
        // The review's case. Had the new names begun "managed-" like the old ones, a host built as the victim's
        // first characters + "." + the victim's hash + a domain of the attacker's would have an OLD name equal to
        // the victim's NEW one -- and the code still running until the release orders certificates under old
        // names. Planted there, it would have refused the victim's certificate on every pass. "managed--" is a
        // start no old name has (a host's first label never begins with a hyphen).
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(LongVictim)))[..16].ToLowerInvariant();
        var planted = LongVictim[..55] + "." + hash + ".attacker591.example.test";
        Assert.True(PlatformHostNames.TryCanonicalHost(planted, out _));
        Assert.Equal("managed-" + new string('a', 55) + "-" + hash, ManagedCertificateNames.Legacy(planted));   // the premise
        Assert.NotEqual(ManagedCertificateNames.Legacy(planted), ManagedCertificateNames.For(LongVictim));

        var arm = new FakeArm { IssueOnPut = true };
        arm.Certificates[ManagedCertificateNames.Legacy(planted)] = new(planted, "PLANTEDTHUMB");
        var result = await Service(arm, new RecordingLogger()).EnsureDomainAsync(LongVictim);

        Assert.True(result.Success, result.Message);
        Assert.True(result.SslBound);
        Assert.Equal(LongVictim, arm.Certificates[ManagedCertificateNames.For(LongVictim)].CanonicalName);
        Assert.Equal(planted, arm.Certificates[ManagedCertificateNames.Legacy(planted)].CanonicalName);

        // And in general: an old name has a letter or digit after "managed-", a new one a hyphen.
        foreach (var host in new[] { Victim, Twin, LongVictim, LongTwin, planted, "a.b", "0.example.test" })
        {
            Assert.NotEqual('-', ManagedCertificateNames.Legacy(host)[8]);
            Assert.Equal('-', ManagedCertificateNames.For(host)[8]);
        }
    }

    [Fact]
    public async Task A_short_address_typed_to_share_a_www_names_certificate_cannot_touch_it()
    {
        // The cheapest shape: www-short591.example.test, connected as a short address (an A record), has the old
        // name of www.short591.example.test.
        const string victim = "www.short591.example.test";
        const string twin = "www-short591.example.test";
        Assert.Equal(ManagedCertificateNames.Legacy(victim), ManagedCertificateNames.Legacy(twin));
        var arm = new FakeArm { IssueOnPut = true };
        arm.Certificates[ManagedCertificateNames.Legacy(victim)] = new(victim, "VICTIMTHUMB");
        var service = Service(arm, new RecordingLogger());

        var connected = await service.EnsureRootDomainAsync(twin);
        var removed = await service.RemoveDomainAsync(twin);

        Assert.True(connected.Success, connected.Message);
        Assert.True(removed.Success, removed.Message);
        Assert.Equal(new CertificateState(victim, "VICTIMTHUMB"), arm.Certificates[ManagedCertificateNames.Legacy(victim)]);
        Assert.DoesNotContain(arm.Requests, r => r.Method != HttpMethod.Get && r.Url.Contains("/certificates/" + ManagedCertificateNames.Legacy(victim) + "?", StringComparison.Ordinal));
        Assert.DoesNotContain(arm.Requests, r => r.Body.Contains("VICTIMTHUMB", StringComparison.Ordinal));
        Assert.Contains(arm.Requests, r => r.Method == HttpMethod.Put && r.Url.Contains("/hostNameBindings/" + twin + "?", StringComparison.Ordinal) && r.Body.Contains("\"customHostNameDnsRecordType\":\"A\"", StringComparison.Ordinal));
        Assert.False(arm.Certificates.ContainsKey(ManagedCertificateNames.For(twin)));   // its own came and went
    }

    [Fact]
    public async Task The_stuck_certificate_alert_names_both_names_the_certificate_may_have()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, _, siteId) = await AdviserAsync(db);
        const string host = "www.alert591.example.test";
        var row = Row(agentId, siteId, host, "alert591.example.test", host);
        row.SslStatus = AgentDomainStatus.BindingPending;
        row.CreatedAt = DateTime.UtcNow.AddHours(-5);
        row.OwnershipVerifiedAt = DateTime.UtcNow.AddHours(-5);   // 595: the wait counts from when it was proven and connected
        db.Add(row);
        await db.SaveChangesAsync();

        var email = new RecordingEmail();
        var log = new RecordingLogger();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Email:FromEmail"] = "ops591@example.test" }).Build();
        await new IPRO.Scheduler.DomainAutomationJob(db, new ScriptedCheck(), email, config, log.For<IPRO.Scheduler.DomainAutomationJob>()).RunAsync();

        var (_, _, html) = Assert.Single(email.Sent);
        Assert.Contains("/certificates/" + ManagedCertificateNames.For(host) + " --query", html);
        Assert.Contains("<code>" + ManagedCertificateNames.Legacy(host) + "</code>", html);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains(ManagedCertificateNames.For(host), StringComparison.Ordinal) && l.Text.Contains(ManagedCertificateNames.Legacy(host), StringComparison.Ordinal));
    }

    [Fact]
    public void Only_the_name_helpers_spell_a_certificate_name()
    {
        // A second spelling of "managed-" + dashes would bring the old, shared name back; the alert names the
        // certificate through the same helpers.
        var root = SolutionRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => !f.EndsWith("ManagedCertificateNames.cs", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), "\"managed-\"|managed-\\{"))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        Assert.Empty(offenders);
        Assert.Contains("ManagedCertificateNames.For(", File.ReadAllText(Path.Combine(root, @"src\IPRO.Scheduler\DomainAutomationJob.cs")));
    }

    // --------------------------------------------- removing while another row names the host --

    [Fact]
    public async Task Removing_a_domain_sends_Azure_nothing_for_a_host_another_row_still_names()
    {
        // A row from before the claim check (2026-08-05): adviser B's domain is the very name adviser A's short
        // address is bound as. B's removal must not delete A's binding and certificate.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (aId, _, aSite) = await AdviserAsync(db);
        var (bId, _, bSite) = await AdviserAsync(db);
        var a = Row(aId, aSite, "www.shared591.example.test", "shared591.example.test", "www.shared591.example.test");
        a.RootDnsStatus = AgentDomainStatus.Bound;
        a.RootAzureBindingStatus = AgentDomainStatus.Bound;
        var b = Row(bId, bSite, "shared591.example.test", "shared591.example.test", "shared591.example.test");
        var bSolo = Row(bId, bSite, "www.solo591.example.test", "solo591.example.test", "www.solo591.example.test");
        bSolo.IsPrimary = false;
        db.AddRange(a, b, bSolo);
        await db.SaveChangesAsync();

        var azure = new ScriptedAzure();
        var log = new RecordingLogger();
        await Controller(db, bId, azure, log).RemoveDomain(b.Id, "shared591.example.test");
        await Controller(db, bId, azure, log).RemoveDomain(bSolo.Id, "www.solo591.example.test");

        Assert.DoesNotContain("remove shared591.example.test", azure.Calls);
        Assert.Contains("remove www.solo591.example.test", azure.Calls);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("shared591.example.test", StringComparison.Ordinal) && l.Text.Contains("another domain row", StringComparison.Ordinal));
        db.ChangeTracker.Clear();
        Assert.False(await db.AgentDomains.AnyAsync(d => d.AgentUserId == bId));
        Assert.True(await db.AgentDomains.AnyAsync(d => d.AgentUserId == aId));

        // The same check, any spelling the database stores: capitals do not make it another name.
        Assert.True(await AgentDomainHosts.StillNamedAsync(db, "SHARED591.example.test"));
        Assert.True(await AgentDomainHosts.StillNamedAsync(db, "www.shared591.example.test"));
        Assert.False(await AgentDomainHosts.StillNamedAsync(db, "www.solo591.example.test"));
    }

    // ----------------------------------------------------------- the owner's SELECT --

    [Fact]
    public async Task The_owners_SELECT_lists_the_pairs_that_share_a_certificate_name_and_nothing_else()
    {
        var sql = File.ReadAllText(Path.Combine(SolutionRoot(), @"ops\sql\certificate-name-collisions.sql"));
        // Read-only, and one statement he can paste.
        var code = string.Join("\n", sql.Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        Assert.DoesNotMatch(new Regex(@"\b(INSERT|UPDATE|DELETE|DROP|ALTER|CREATE|TRUNCATE|GRANT|SET|CALL|LOAD|RENAME|LOCK|HANDLER)\b|\bREPLACE\s+INTO\b", RegexOptions.IgnoreCase), code);
        Assert.Equal(1, code.Count(c => c == ';'));

        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (aId, _, aSite) = await AdviserAsync(db);
        var (bId, _, bSite) = await AdviserAsync(db);
        db.AddRange(
            Row(aId, aSite, Victim, "abc591-def.example.test", Victim),
            Row(bId, bSite, Twin, Twin, Twin),
            Row(aId, aSite, LongVictim, LongVictim, LongVictim),
            Row(bId, bSite, LongTwin, LongTwin, LongTwin),
            // Stored with a trailing dot before 589: the old code named its certificate without it.
            Row(aId, aSite, "www.dot591-x.example.test", "dot591-x.example.test", "www.dot591-x.example.test"),
            Row(bId, bSite, "www-dot591.x.example.test.", "www-dot591.x.example.test.", "www-dot591.x.example.test."),
            // The very same address in two rows (A's short address, B's domain).
            Row(aId, aSite, "www.same591.example.test", "same591.example.test", "www.same591.example.test"),
            Row(bId, bSite, "same591.example.test", "same591.example.test", "same591.example.test"),
            // Innocent: a www name and its short address, a name of its own, a row with no short address.
            Row(aId, aSite, "www.alone591.example.test", "alone591.example.test", "www.alone591.example.test"),
            Row(bId, bSite, "shop.other591.example.test", "", "shop.other591.example.test"));
        await db.SaveChangesAsync();

        var rows = new List<Dictionary<string, object?>>();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i)));
        }

        string Pair(Dictionary<string, object?> r) => r["host_1"] + " | " + r["host_2"] + " | " + r["why"];
        Assert.Equal(
            new[]
            {
                LongTwin + " | " + LongVictim + " | two addresses share one certificate name",
                "same591.example.test | same591.example.test | two rows name the same address",
                "www-dot591.x.example.test | www.dot591-x.example.test | two addresses share one certificate name",
                Twin + " | " + Victim + " | two addresses share one certificate name",
            }.OrderBy(s => s, StringComparer.Ordinal),
            rows.Select(Pair).OrderBy(s => s, StringComparer.Ordinal));

        var shortPair = rows.Single(r => (string)r["host_1"]! == Twin);
        Assert.Equal(ManagedCertificateNames.Legacy(Victim), shortPair["certificate_name"]);
        Assert.Equal(bId, Convert.ToInt32(shortPair["adviser_1"], CultureInfo.InvariantCulture));
        Assert.Equal(aId, Convert.ToInt32(shortPair["adviser_2"], CultureInfo.InvariantCulture));
        Assert.Equal("DomainName+RootDomain+WwwDomain", shortPair["fields_1"]);
        Assert.Equal("DomainName+WwwDomain", shortPair["fields_2"]);
        Assert.NotNull(shortPair["username_1"]);
        var same = rows.Single(r => (string)r["host_1"]! == "same591.example.test");
        Assert.Equal(new[] { "DomainName+RootDomain+WwwDomain", "RootDomain" }, new[] { (string)same["fields_1"]!, (string)same["fields_2"]! }.OrderBy(f => f, StringComparer.Ordinal));
        Assert.NotEqual(same["adviser_1"], same["adviser_2"]);
    }

    // ------------------------------------------------------------------- plumbing --

    private static AzureDomainAutomationService Service(FakeArm arm, RecordingLogger log) => new(
        arm,
        Options.Create(new AzureDomainAutomationOptions
        {
            Enabled = true,
            TenantId = "tenant", ClientId = "client", ClientSecret = "secret",
            SubscriptionId = "sub", ResourceGroup = "rg",
            WebAppName = "ipro-prod-web",
            AppServicePlanResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/serverfarms/plan",
        }),
        log.For<AzureDomainAutomationService>(),
        new ConfigurationBuilder().Build());

    private static AgentDomain Row(int agentId, int websiteId, string name, string root, string www) => new()
    {
        AgentUserId = agentId, AgentWebsiteId = websiteId, DomainName = name, RootDomain = root, WwwDomain = www,
        DnsTarget = "ipro-prod-web.azurewebsites.net", IsPrimary = true,
        DnsStatus = AgentDomainStatus.Bound, AzureBindingStatus = AgentDomainStatus.Bound, SslStatus = AgentDomainStatus.Bound,
        OwnershipVerifiedAt = DateTime.UtcNow,   // 592: a live domain is a proven one
    };

    // An adviser whose package has a website and extra domains (no limit), with a saved website.
    private static async Task<(int AgentId, int TemplateId, int WebsiteId)> AdviserAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T591-{Guid.NewGuid():N}")[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        var template = new WebsiteTemplate { Name = "Modern", TemplateKey = ($"t591-{Guid.NewGuid():N}")[..16], BusinessType = "All", IsActive = true };
        db.AddRange(rule, template);
        await db.SaveChangesAsync();
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.InstantWebsite, FeatureName = "Website", IsIncluded = true });
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.MultiDomainSupport, FeatureName = "Domains", IsIncluded = true });
        var agent = new AgentUser
        {
            UserName = ($"t591-{Guid.NewGuid():N}")[..20], Email = ($"t591-{Guid.NewGuid():N}")[..14] + "@example.test",
            FirstName = "Sam", LastName = "Smith", CompanyName = "Smith Advisory",
            DomainName = ($"t591-{Guid.NewGuid():N}")[..24], Country = "Canada", Province = "Ontario",
            PackageId = rule.Id, TrialEndsAt = null,
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        db.Add(new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 60m, Status = BillingStatus.Active,
            Period = BillingPeriod.Monthly, StartDate = DateTime.UtcNow.AddDays(-40), NextBillingDate = DateTime.UtcNow.AddDays(20),
        });
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, SiteTitle = "Mine" };
        db.Add(website);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (agent.Id, template.Id, website.Id);
    }

    private static WebsiteController Controller(IPRODbContext db, int agentId, IAzureDomainAutomationService azure, RecordingLogger log)
    {
        var controller = new WebsiteController(
            new WebsiteService(new UnitOfWork(db)), new NullBlob(), new PackageEntitlementService(new UnitOfWork(db), db),
            new AgentService(new UnitOfWork(db), new PasswordHasher<AgentUser>()), new ConfigurationBuilder().Build(), db,
            new ScriptedCheck(), azure, log.For<WebsiteController>());
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString(CultureInfo.InvariantCulture)) }, "test"))
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

    public sealed record CertificateState(string? CanonicalName, string? Thumbprint);

    // Stands in for login.microsoftonline.com and management.azure.com, holding certificates the way Azure
    // does: one per name, GET 404 for a name that holds none, a PUT that replaces what the name held (the
    // hole: another adviser's canonicalName over it, its thumbprint kept), DELETE 404 for nothing there.
    private sealed class FakeArm : HttpMessageHandler, IHttpClientFactory
    {
        public Dictionary<string, CertificateState> Certificates { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(HttpMethod Method, string Url, string Body)> Requests { get; } = new();
        public bool IssueOnPut { get; init; }
        public HttpStatusCode? FailCertificateReads { get; init; }
        public string? AnswerPutsFor { get; init; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("login.microsoftonline.com", StringComparison.Ordinal))
                return Json("{\"access_token\":\"token\",\"expires_in\":3600}");

            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, url, body));
            const string marker = "/providers/Microsoft.Web/certificates/";
            var path = request.RequestUri.AbsolutePath;
            var at = path.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return Json("{}");   // a host-name binding, PUT or DELETE: accepted
            var name = Uri.UnescapeDataString(path[(at + marker.Length)..]);

            if (request.Method == HttpMethod.Get)
            {
                if (FailCertificateReads is { } status)
                    return Json("{\"error\":{\"code\":\"InternalServerError\",\"message\":\"try again later\"}}", status);
                return Certificates.TryGetValue(name, out var held) ? Json(Describe(name, held)) : Missing(name);
            }

            if (request.Method == HttpMethod.Put)
            {
                using var json = JsonDocument.Parse(body);
                var canonical = json.RootElement.GetProperty("properties").GetProperty("canonicalName").GetString();
                var thumbprint = Certificates.TryGetValue(name, out var existing) ? existing.Thumbprint : null;
                Certificates[name] = new(canonical, thumbprint ?? (IssueOnPut ? "T-" + canonical : null));
                return Json(Describe(name, AnswerPutsFor == null ? Certificates[name] : Certificates[name] with { CanonicalName = AnswerPutsFor }));
            }

            if (request.Method == HttpMethod.Delete)
                return Certificates.Remove(name) ? Json("{}") : Missing(name);

            throw new InvalidOperationException("591: unexpected " + request.Method + " " + url);
        }

        private static string Describe(string name, CertificateState state)
        {
            var properties = new Dictionary<string, object?>();
            if (state.CanonicalName != null) properties["canonicalName"] = state.CanonicalName;
            if (state.Thumbprint != null) properties["thumbprint"] = state.Thumbprint;
            return JsonSerializer.Serialize(new { name, type = "Microsoft.Web/certificates", properties });
        }

        private static HttpResponseMessage Missing(string name) =>
            Json("{\"error\":{\"code\":\"ResourceNotFound\",\"message\":\"The Resource 'Microsoft.Web/certificates/" + name + "' under resource group 'rg' was not found.\"}}", HttpStatusCode.NotFound);

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class ScriptedAzure : IAzureDomainAutomationService
    {
        public bool IsConfigured => true;
        public List<string> Calls { get; } = new();

        public Task<AzureDomainAutomationResult> EnsureDomainAsync(string hostName, CancellationToken cancellationToken = default) => Record("ensure " + hostName);
        public Task<AzureDomainAutomationResult> EnsureRootDomainAsync(string hostName, CancellationToken cancellationToken = default) => Record("ensure root " + hostName);
        public Task<AzureDomainAutomationResult> RemoveDomainAsync(string hostName, CancellationToken cancellationToken = default) => Record("remove " + hostName);

        private Task<AzureDomainAutomationResult> Record(string call)
        {
            Calls.Add(call);
            return Task.FromResult(new AzureDomainAutomationResult { Success = true });
        }
    }

    private sealed class ScriptedCheck : IDomainCheckService
    {
        public Task<bool> CheckAsync(AgentDomain domain, CancellationToken cancellationToken = default) => Task.FromResult(false);
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

    private sealed class NullTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
