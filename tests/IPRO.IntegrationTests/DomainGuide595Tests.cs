using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
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
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static IPRO.Utility.DomainSetupGuide;

namespace IPRO.IntegrationTests;

// 595 (2026-10-21). The owner connected a customer's new domain (www.maison1850.com, for the L'Avenue bakery) himself
// and found it confusing even as a developer: the adviser already had a connected domain, so the registrar steps were
// folded away behind "Show the setup steps (for adding another domain)"; only the 592 proof record showed beside the
// new name; "Check now" said to wait. He asked for it to be "very smooth and idiot proof":
//
//   - a numbered guide opens for the domain just added, from step 1, whatever else is connected: prove it's yours,
//     point www at iPro, the short address (optional), iPro finishes -- one step open at a time, ticks as each
//     record is seen;
//   - wording for the domain's own DNS host (from its NS records), with what goes in the Name box;
//   - the page looks again on its own about every 20 seconds, asking the zone's own name servers (a public resolver
//     asked before a record exists keeps saying "no" for the zone's negative TTL: ten minutes at GoDaddy);
//   - "I need help" opens a support ticket with what iPro sees for each record; SuperAdmin shows the same step;
//   - an email when the domain is live, and one if it is stuck a day later with what is missing.
//
// In ConfigHazardTests' collection: DomainCheckService.ResolveHook, DomainOwnershipProof.LookupHook and ZoneDns.QueryHook
// are seams for the whole process.
[Collection(ConfigHazardTests.AzureStatics)]
public class DomainGuide595Tests : IDisposable
{
    private const string Platform = "40.89.19.0";
    private const string Target = "ipro-prod-web.azurewebsites.net";
    private const string Asuid = "C6D5FC2CBD50987D0FF7FE648BC710D4B2454FA211AE9094BB2816A6505E8572";
    private const string Token = "ipro-0123456789abcdef0123456789abcdef";
    private static readonly Settings Expected = Settings.From(Target, Platform, Asuid);

    public DomainGuide595Tests() => Reset();
    public void Dispose() => Reset();

    private static void Reset()
    {
        DomainCheckService.ResolveHook = (host, ct) => Dns.GetHostAddressesAsync(host, ct);
        DomainOwnershipProof.LookupHook = null;
        ZoneDns.QueryHook = null;
        DomainCheckService.ForgetZones();
    }

    // ------------------------------------------------------------------ the DNS host --

    [Theory]
    [InlineData("ns57.domaincontrol.com.", "godaddy", "GoDaddy")]
    [InlineData("dns1.registrar-servers.com", "namecheap", "Namecheap")]
    [InlineData("pdns1.registrar-servers.com", "namecheap", "Namecheap")]
    [InlineData("KATE.NS.CLOUDFLARE.COM", "cloudflare", "Cloudflare")]
    [InlineData("ns-cloud-a1.googledomains.com", "squarespace", "Squarespace")]
    [InlineData("ns1.wixdns.net", "wix", "Wix")]
    [InlineData("ns1045.ui-dns.com", "ionos", "IONOS")]
    [InlineData("ns1.hover.com", "hover", "Hover")]
    [InlineData("ns1.systemdns.com", "opensrs", "Tucows")]
    [InlineData("ns1.hostpapa.com", "cpanel", "HostPapa")]
    public void The_DNS_host_is_recognised_from_its_name_servers(string nameServer, string key, string name)
    {
        var host = DnsHosts.Recognise(new[] { nameServer });
        Assert.Equal(key, host.Key);
        Assert.Contains(name, host.Name, StringComparison.Ordinal);
        Assert.StartsWith("https://", host.HelpUrl, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(host.Where));
    }

    [Fact]
    public void An_unknown_host_is_named_by_its_name_server_and_gets_the_wording_that_fits_everywhere()
    {
        var host = DnsHosts.Recognise(new[] { "ns1.someprovider.example.", "ns2.someprovider.example" });
        Assert.Equal("generic", host.Key);
        Assert.Contains("ns1.someprovider.example", host.Name, StringComparison.Ordinal);
        Assert.Contains("DNS", host.Where, StringComparison.Ordinal);
        Assert.Equal("unknown", DnsHosts.Recognise(Array.Empty<string>()).Key);
        Assert.Equal("unknown", DnsHosts.Recognise(null).Key);
    }

    [Fact]
    public void Every_known_hosts_help_page_is_its_own_https_page()
    {
        foreach (var ns in new[] { "ns1.domaincontrol.com", "dns1.registrar-servers.com", "a.ns.cloudflare.com", "ns-cloud-a1.googledomains.com",
                     "ns1.wixdns.net", "ns1.ui-dns.com", "ns1.hover.com", "ns1.systemdns.com", "ns1.hostpapa.com" })
        {
            var host = DnsHosts.Recognise(new[] { ns });
            Assert.True(Uri.TryCreate(host.HelpUrl, UriKind.Absolute, out var help) && help.Scheme == "https", host.Key);
        }
    }

    [Fact]
    public void The_name_box_says_what_the_host_wants_relative_to_the_zone()
    {
        var godaddy = DnsHosts.Recognise(new[] { "ns57.domaincontrol.com" });
        Assert.Equal("_ipro-verify", DnsHosts.NameInBox(godaddy, "_ipro-verify.drhug.ca", "drhug.ca"));
        Assert.Equal("www", DnsHosts.NameInBox(godaddy, "www.drhug.ca", "drhug.ca"));
        Assert.Equal("@", DnsHosts.NameInBox(godaddy, "drhug.ca", "drhug.ca"));
        Assert.Equal("asuid", DnsHosts.NameInBox(godaddy, "asuid.drhug.ca", "drhug.ca"));
        // A sub-domain added as the address itself: its records are named inside the zone.
        Assert.Equal("_ipro-verify.clients", DnsHosts.NameInBox(godaddy, "_ipro-verify.clients.firm.ca", "firm.ca"));
        Assert.Equal("clients", DnsHosts.NameInBox(godaddy, "clients.firm.ca", "firm.ca"));
        // Zone unknown: the full name (the guide then says what to do if the screen adds the domain itself).
        Assert.Equal("_ipro-verify.clients.firm.ca", DnsHosts.NameInBox(godaddy, "_ipro-verify.clients.firm.ca", null));
    }

    // ------------------------------------------------------------------ the guide --

    [Fact]
    public void A_domain_just_added_opens_on_step_1_with_every_record_it_will_need()
    {
        var guide = Describe(Fresh("maison595.test"), look: null, Expected, DateTime.UtcNow);

        Assert.Equal(1, guide.CurrentStep);
        Assert.False(guide.Live);
        Assert.Equal("Step 1 of 4: Prove it's yours", guide.Heading);
        Assert.Equal(new[] { "Prove it's yours", "Point www at iPro", "The short address (optional, recommended)", "iPro finishes" }, guide.Steps.Select(s => s.Title));
        Assert.Equal(new[] { StepState.Current, StepState.Upcoming, StepState.Upcoming, StepState.Upcoming }, guide.Steps.Select(s => s.State));

        var proof = Assert.Single(guide.Steps[0].Records);
        Assert.Equal(("TXT", "_ipro-verify", "_ipro-verify.maison595.test", Token), (proof.Type, proof.Name, proof.FullName, proof.Value));
        var www = Assert.Single(guide.Steps[1].Records);
        Assert.Equal(("CNAME", "www", Target), (www.Type, www.Name, www.Value));
        Assert.Equal(new[] { ("A", "@", Platform), ("TXT", "asuid", Asuid) }, guide.Steps[2].Records.Select(r => (r.Type, r.Name, r.Value)));
        Assert.Empty(guide.Steps[3].Records);
    }

    [Fact]
    public void What_iPro_sees_is_said_in_plain_words_beside_what_it_expects()
    {
        var domain = Fresh("maison595.example.test");

        var wrongValue = Describe(domain, Look(proof: new[] { "ipro-ffffffffffffffffffffffffffffffff" }), Expected, DateTime.UtcNow);
        Assert.Equal(1, wrongValue.CurrentStep);
        Assert.False(wrongValue.Steps[0].SeenRight);
        Assert.Contains("ipro-ffffffffffffffffffffffffffffffff", wrongValue.Steps[0].Seen);
        Assert.Contains(Token, wrongValue.Steps[0].Seen);

        var nothing = Describe(domain, Look(proof: Array.Empty<string>()), Expected, DateTime.UtcNow);
        Assert.Contains("don't see", nothing.Steps[0].Seen);

        var noAnswer = Describe(domain, Look(proof: null), Expected, DateTime.UtcNow);
        Assert.Contains("could not check", noAnswer.Steps[0].Seen);

        // Seen: step 1 ticks at once, step 2 opens and says exactly what www points at today, and what to change it to.
        var elsewhere = Describe(domain, Look(proof: new[] { Token }, wwwCname: "lavenuebakery.myshopify.com", wwwAddresses: new[] { "23.227.38.65" }), Expected, DateTime.UtcNow);
        Assert.Equal(StepState.Done, elsewhere.Steps[0].State);
        Assert.Equal(2, elsewhere.CurrentStep);
        Assert.Equal("Step 2 of 4: Point www at iPro", elsewhere.Heading);
        Assert.Contains("lavenuebakery.myshopify.com", elsewhere.Steps[1].Seen);
        Assert.Contains(Target, elsewhere.Steps[1].Seen);
        Assert.Contains("Edit", elsewhere.Steps[1].Seen);

        var addressRecord = Describe(domain, Look(proof: new[] { Token }, wwwAddresses: new[] { "198.51.100.7" }), Expected, DateTime.UtcNow);
        Assert.Contains("198.51.100.7", addressRecord.Steps[1].Seen);
        Assert.Contains("A record", addressRecord.Steps[1].Seen);
    }

    [Fact]
    public void The_short_address_says_what_is_in_the_way_and_never_touches_email()
    {
        var domain = Proven(Fresh("maison595.example.test"));

        // An old host's record left beside ours (GoDaddy's own forwarding beside ours is its own case, below).
        var mixed = Describe(domain, Look(proof: new[] { Token }, wwwCname: Target, root: new[] { Platform, "76.223.105.230" }, asuid: new[] { Asuid }), Expected, DateTime.UtcNow);
        Assert.Equal(3, mixed.CurrentStep);
        Assert.Contains("76.223.105.230", mixed.Steps[2].Seen);
        Assert.Contains("only one", mixed.Steps[2].Seen);

        var godaddyForwarding = Describe(domain, Look(proof: new[] { Token }, wwwCname: Target, root: new[] { "15.197.225.128", "3.33.251.168" }, asuid: Array.Empty<string>(), ns: "ns57.domaincontrol.com"),
            Expected, DateTime.UtcNow);
        Assert.Contains("forwarding", godaddyForwarding.Steps[2].Seen, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GoDaddy", godaddyForwarding.Steps[2].Seen);

        var done = Describe(domain, Look(proof: new[] { Token }, wwwCname: Target, root: new[] { Platform }, asuid: new[] { Asuid }), Expected, DateTime.UtcNow);
        Assert.Equal(StepState.Done, done.Steps[2].State);
        Assert.Equal(4, done.CurrentStep);

        // Forwarding that works counts as done (553: either way), with the one thing it does not do.
        var forwarded = Proven(Fresh("fwd595.example.test"));
        forwarded.RootDnsStatus = AgentDomainStatus.DnsReady;
        forwarded.RootRedirectsToWww = true;
        var viaForwarding = Describe(forwarded, Look(proof: new[] { Token }, wwwCname: Target, root: new[] { "15.197.225.128" }), Expected, DateTime.UtcNow);
        Assert.Equal(StepState.Done, viaForwarding.Steps[2].State);
        Assert.Contains("forward", viaForwarding.Steps[2].Status, StringComparison.OrdinalIgnoreCase);

        // A sub-domain added as the address itself has no short address to set.
        var sub = Describe(new AgentDomain { Id = 9, DomainName = "clients595.example.test", WwwDomain = "clients595.example.test", RootDomain = "clients595.example.test", DnsTarget = Target, VerificationToken = Token },
            null, Expected, DateTime.UtcNow);
        Assert.Equal(StepState.NotNeeded, sub.Steps[2].State);
        Assert.Equal("Point clients595.example.test at iPro", sub.Steps[1].Title);

        // Not a word about mail records: they are never part of this.
        foreach (var guide in new[] { mixed, godaddyForwarding, done, viaForwarding })
            Assert.DoesNotContain(guide.Steps.SelectMany(s => s.Records), r => r.Type == "MX");
    }

    [Fact]
    public void Step_4_shows_iPro_finishing_connected_then_padlock_then_live()
    {
        var domain = Proven(Fresh("maison595.example.test"));
        var look = Look(proof: new[] { Token }, wwwCname: Target, root: new[] { Platform }, asuid: new[] { Asuid });

        var connecting = Describe(domain, look, Expected, DateTime.UtcNow);
        Assert.Equal(4, connecting.CurrentStep);
        Assert.Equal(StepState.Current, connecting.Finish.Single(p => p.Key == "connected").State);

        domain.DnsStatus = AgentDomainStatus.Bound;
        domain.AzureBindingStatus = AgentDomainStatus.Bound;
        var padlock = Describe(domain, look, Expected, DateTime.UtcNow);
        Assert.Equal(StepState.Done, padlock.Finish.Single(p => p.Key == "connected").State);
        Assert.Equal(StepState.Current, padlock.Finish.Single(p => p.Key == "padlock").State);
        Assert.Equal("Step 4 of 4: iPro finishes", padlock.Heading);

        domain.CaaBlockingName = "maison595.example.test";
        Assert.Equal("maison595.example.test", Describe(domain, look, Expected, DateTime.UtcNow).CaaName);
        domain.CaaBlockingName = string.Empty;

        domain.SslStatus = AgentDomainStatus.Bound;
        var live = Describe(domain, look, Expected, DateTime.UtcNow);
        Assert.True(live.Live);
        Assert.Equal(0, live.CurrentStep);
        Assert.Equal("www.maison595.example.test is live", live.Heading);
        Assert.All(live.Steps.Take(2), s => Assert.Equal(StepState.Done, s.State));
        Assert.Equal(StepState.Done, live.Finish.Single(p => p.Key == "live").State);
    }

    [Fact]
    public void A_name_iPro_refuses_gets_the_refusal_and_no_steps_to_follow()
    {
        var refused = Describe(new AgentDomain { Id = 3, DomainName = "www.iproadvisers.com", WwwDomain = "www.iproadvisers.com", RootDomain = "iproadvisers.com", DnsTarget = Target, VerificationToken = Token },
            null, Expected, DateTime.UtcNow);
        Assert.NotNull(refused.Refusal);
        Assert.Contains("IPRO's own addresses", refused.Refusal);
        Assert.Equal(0, refused.CurrentStep);
        Assert.Empty(refused.Steps);
    }

    [Fact]
    public void A_domain_whose_www_already_points_at_iPro_starts_on_step_1_with_step_2_ticked()
    {
        // The owner's test after the release: www.drhug.ca and www.ouritems.ca (GoDaddy; www already a CNAME to the
        // platform from July; the short address on GoDaddy's forwarding; no _ipro-verify yet).
        var guide = Describe(Fresh("drhug595.test"),
            Look(proof: Array.Empty<string>(), wwwCname: Target, root: new[] { "15.197.225.128", "3.33.251.168" }, asuid: Array.Empty<string>(), ns: "ns57.domaincontrol.com"),
            Expected, DateTime.UtcNow);
        Assert.Equal(1, guide.CurrentStep);
        Assert.Equal(StepState.Done, guide.Steps[1].State);
        Assert.Contains("already", guide.Steps[1].Seen);
        Assert.Equal("godaddy", guide.Host.Key);
        Assert.Equal("_ipro-verify", guide.Steps[0].Records[0].Name);
    }

    [Fact]
    public void The_page_asks_iPro_to_connect_only_when_DNS_is_ahead_of_it_and_not_more_than_once_a_minute()
    {
        var now = DateTime.UtcNow;
        var domain = Fresh("maison595.example.test");
        domain.LastCheckedAt = now.AddMinutes(-5);
        var seen = Look(proof: new[] { Token }, wwwCname: Target);
        Assert.True(ShouldCheckNow(domain, Describe(domain, seen, Expected, now), now));
        Assert.False(ShouldCheckNow(domain, Describe(domain, Look(proof: Array.Empty<string>()), Expected, now), now));

        domain.LastCheckedAt = now.AddSeconds(-20);
        Assert.False(ShouldCheckNow(domain, Describe(domain, seen, Expected, now), now));

        var live = Proven(Fresh("live595.example.test"));
        live.DnsStatus = live.AzureBindingStatus = live.SslStatus = AgentDomainStatus.Bound;
        live.LastCheckedAt = now.AddHours(-1);
        Assert.False(ShouldCheckNow(live, Describe(live, Look(proof: new[] { Token }, wwwCname: Target), Expected, now), now));
    }

    // ------------------------------------------------------------------ the look --

    [Fact]
    public async Task The_look_asks_the_zones_own_servers_for_the_four_records_and_nothing_else()
    {
        var asked = Zone("look595.example.test", "ns1.domaincontrol.com", new Dictionary<(string, ushort), string[]>
        {
            [("_ipro-verify.look595.example.test", ZoneDns.TXT)] = new[] { Token },
            [("www.look595.example.test", ZoneDns.CNAME)] = new[] { Target + "." },
            [("look595.example.test", ZoneDns.A)] = new[] { Platform, "15.197.225.128" },
            [("asuid.look595.example.test", ZoneDns.TXT)] = new[] { Asuid },
        });
        Resolve(("ns1.domaincontrol.com", "97.74.100.1"), (Target, Platform), ("www.look595.example.test", Platform));

        var look = await Service().LookAsync(Fresh("look595.example.test"));

        Assert.NotNull(look);
        Assert.True(look!.FromZoneServers);
        Assert.Equal("look595.example.test", look.Zone);
        Assert.Equal(new[] { "ns1.domaincontrol.com" }, look.NameServers);
        Assert.Equal(new[] { Token }, look.ProofValues);
        Assert.Equal(Target, look.WwwCname);
        Assert.Equal(new[] { Platform, "15.197.225.128" }, look.RootAddresses);
        Assert.Equal(new[] { Asuid }, look.AsuidValues);
        Assert.All(asked.Names, name => Assert.Contains(name, new[] { "_ipro-verify.look595.example.test", "www.look595.example.test", "look595.example.test", "asuid.look595.example.test" }));
        Assert.All(asked.Servers, server => Assert.Equal("97.74.100.1", server));
    }

    [Fact]
    public async Task Name_servers_at_private_addresses_are_never_asked_and_iPros_own_names_are_never_looked_up()
    {
        var asked = Zone("private595.example.test", "ns1.private595.example.test", new Dictionary<(string, ushort), string[]>());
        Resolve(("ns1.private595.example.test", "10.1.2.3"), (Target, Platform));
        var look = await Service().LookAsync(Fresh("private595.example.test"));
        Assert.NotNull(look);
        Assert.False(look!.FromZoneServers);
        Assert.Empty(asked.Servers);

        var doh = new List<string>();
        DomainOwnershipProof.LookupHook = (address, _) => { doh.Add(address); return Task.FromResult<string?>("{\"Status\":3}"); };
        Assert.Null(await Service().LookAsync(new AgentDomain { DomainName = "www.iproadvisers.com", WwwDomain = "www.iproadvisers.com", RootDomain = "iproadvisers.com", DnsTarget = Target, VerificationToken = Token }));
        Assert.Empty(doh);
        Assert.Empty(asked.Names);
    }

    // ------------------------------------------------- the proof, asked of the zone first --

    [Fact]
    public async Task The_proof_is_seen_on_the_zones_own_servers_before_www_points_anywhere()
    {
        // 592 waited for www to resolve before asking a public resolver, so that the resolver would not remember
        // "no such record" from before the record was added. The zone's own servers remember nothing.
        Zone("first595.example.test", "ns1.domaincontrol.com", new Dictionary<(string, ushort), string[]>
        {
            [("_ipro-verify.first595.example.test", ZoneDns.TXT)] = new[] { Token },
        });
        Resolve(("ns1.domaincontrol.com", "97.74.100.1"), (Target, Platform));
        var azure = new ScriptedAzure();
        var domain = Fresh("first595.example.test");

        await Service(azure).CheckAsync(domain);

        Assert.NotNull(domain.OwnershipVerifiedAt);
        Assert.Empty(azure.Calls);                                     // www does not point here yet: nothing to bind
        Assert.Equal(AgentDomainStatus.PendingDns, domain.DnsStatus);
    }

    [Fact]
    public async Task The_zones_own_no_wins_over_a_resolvers_old_yes()
    {
        Zone("gone595.example.test", "ns1.domaincontrol.com", new Dictionary<(string, ushort), string[]>(), txtFromResolvers: Token);
        Resolve(("ns1.domaincontrol.com", "97.74.100.1"), (Target, Platform), ("www.gone595.example.test", Platform));
        var domain = Fresh("gone595.example.test");

        await Service().CheckAsync(domain);

        Assert.Null(domain.OwnershipVerifiedAt);
        Assert.Contains("_ipro-verify", domain.LastError);
    }

    // ------------------------------------------------------------- the page and its endpoint --

    [Fact]
    public void The_polling_endpoint_is_a_post_with_its_own_rate_limit()
    {
        var method = typeof(WebsiteController).GetMethod(nameof(WebsiteController.DomainGuide))!;
        Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.Equal(EndpointRateLimits.DomainGuide, method.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName);
        var (limit, window) = EndpointRateLimits.Policies[EndpointRateLimits.DomainGuide];
        Assert.Equal(60, limit / window.TotalMinutes);                   // per address: an office's advisers share one
        Assert.Equal(20, WebsiteController.GuideLooks.PerWindow);        // per adviser: a 20-second poll, a few guides, Check now
    }

    [Fact]
    public async Task The_endpoint_answers_only_for_the_advisers_own_domain_and_looks_up_nothing_else()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await AdviserAsync(db);
        var (otherId, otherSite) = await AdviserAsync(db);
        var theirs = Row(otherId, otherSite, "theirs595.example.test");
        var mine = Row(agentId, websiteId, "mine595.example.test");
        db.AddRange(theirs, mine);
        await db.SaveChangesAsync();
        var check = new ScriptedCheck { Look = Look(proof: Array.Empty<string>(), wwwCname: Target) };

        Assert.IsType<NotFoundResult>(await Website(db, agentId, check).DomainGuide(theirs.Id));
        Assert.Empty(check.Looked);

        var json = Assert.IsType<JsonResult>(await Website(db, agentId, check).DomainGuide(mine.Id));
        var guide = Assert.IsType<Guide>(json.Value);
        Assert.Equal("www.mine595.example.test", guide.DomainName);
        Assert.Equal(1, guide.CurrentStep);
        Assert.Equal(new[] { "www.mine595.example.test" }, check.Looked);
        // The page reads it as text: the steps' states come over as words.
        var text = JsonSerializer.Serialize(json.Value, (JsonSerializerOptions)json.SerializerSettings!);
        Assert.Contains("\"state\":\"current\"", text);
        Assert.Contains("\"currentStep\":1", text);
    }

    [Fact]
    public async Task Seeing_the_proof_connects_at_once_but_polling_never_hammers_Azure()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await AdviserAsync(db);
        var row = Row(agentId, websiteId, "poll595.example.test");
        row.LastCheckedAt = DateTime.UtcNow.AddMinutes(-5);
        db.Add(row);
        await db.SaveChangesAsync();
        var check = new ScriptedCheck { Look = Look(proof: new[] { Token }, wwwCname: Target) };
        var cache = new MemoryCache(new MemoryCacheOptions());

        await Website(db, agentId, check, cache).DomainGuide(row.Id);
        Assert.Equal(1, check.Checks);                                  // DNS is ahead of the row: connect now
        await Website(db, agentId, check, cache).DomainGuide(row.Id);
        await Website(db, agentId, check, cache).DomainGuide(row.Id);
        Assert.Equal(1, check.Checks);                                  // not again within the minute
        Assert.Single(check.Looked);                                    // and the look itself is kept a few seconds
    }

    [Fact]
    public async Task Adding_a_domain_opens_its_guide()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await AdviserAsync(db);
        db.Add(Row(agentId, websiteId, "first595.example.test", live: true));
        await db.SaveChangesAsync();
        var controller = Website(db, agentId, new ScriptedCheck());

        await controller.AddDomain("maison595.test");

        var added = await db.AgentDomains.AsNoTracking().SingleAsync(d => d.DomainName == "www.maison595.test");
        Assert.Equal(added.Id, controller.TempData["OpenDomainGuide"]);
    }

    [Fact]
    public async Task I_need_help_opens_a_ticket_with_what_iPro_sees_for_each_record()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await AdviserAsync(db);
        var (otherId, otherSite) = await AdviserAsync(db);
        var row = Row(agentId, websiteId, "help595.example.test");
        var theirs = Row(otherId, otherSite, "notmine595.example.test");
        db.AddRange(row, theirs);
        await db.SaveChangesAsync();
        var check = new ScriptedCheck { Look = Look(proof: new[] { Token }, wwwCname: "old-host.example.net", ns: "ns57.domaincontrol.com") };

        Assert.IsType<NotFoundResult>(await Website(db, agentId, check).DomainHelp(theirs.Id));

        var view = Assert.IsType<ViewResult>(await Website(db, agentId, check).DomainHelp(row.Id));
        Assert.Equal("~/Views/Support/Create.cshtml", view.ViewName);
        Assert.Contains("www.help595.example.test", (string)view.ViewData["Subject"]!);
        var message = (string)view.ViewData["Message"]!;
        Assert.Contains("GoDaddy", message);
        Assert.Contains("Step 2 of 4", message);
        Assert.Contains("old-host.example.net", message);                // what iPro sees
        Assert.Contains(Target, message);                                // what it expects
        Assert.Contains("_ipro-verify.help595.example.test", message);
    }

    // ------------------------------------------------------------------ the emails --

    [Fact]
    public async Task The_adviser_hears_once_when_the_domain_is_live_and_once_if_it_is_stuck_after_a_day()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await AdviserAsync(db);
        var live = Row(agentId, websiteId, "live595.example.test", live: true);
        var stuck = Row(agentId, websiteId, "stuck595.example.test");
        stuck.CreatedAt = DateTime.UtcNow.AddHours(-25);
        var young = Row(agentId, websiteId, "young595.example.test");
        young.CreatedAt = DateTime.UtcNow.AddHours(-2);
        db.AddRange(live, stuck, young);
        await db.SaveChangesAsync();
        var email = new RecordingEmail();
        var check = new ScriptedCheck { Look = Look(proof: Array.Empty<string>(), wwwCname: "old-host.example.net") };

        await Job(db, check, email).RunAsync();
        await Job(db, check, email).RunAsync();

        var adviser = await db.AgentUsers.AsNoTracking().SingleAsync(a => a.Id == agentId);
        var liveMail = Assert.Single(email.Sent, m => m.Subject.Contains("www.live595.example.test", StringComparison.Ordinal));
        Assert.Equal(adviser.Email, liveMail.To);
        Assert.Contains("is live", liveMail.Subject);
        var stuckMail = Assert.Single(email.Sent, m => m.Subject.Contains("www.stuck595.example.test", StringComparison.Ordinal));
        Assert.Equal(adviser.Email, stuckMail.To);
        Assert.Contains("_ipro-verify", stuckMail.Html);                 // what is missing
        Assert.Contains("old-host.example.net", stuckMail.Html);
        Assert.DoesNotContain(email.Sent, m => m.Subject.Contains("young595", StringComparison.Ordinal));
        Assert.Equal(2, email.Sent.Count);
    }

    [Fact]
    public async Task Domains_already_there_at_the_release_are_never_mailed()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await AdviserAsync(db);
        var live = Row(agentId, websiteId, "before595.example.test", live: true);
        var stuck = Row(agentId, websiteId, "oldstuck595.example.test");
        stuck.CreatedAt = DateTime.UtcNow.AddDays(-40);
        db.AddRange(live, stuck);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE `AgentDomains` DROP COLUMN `LiveEmailSentAt`, DROP COLUMN `StuckEmailSentAt`");

        await StartupSchemaRepair.EnsureAgentDomainSchemaAsync(db);
        await StartupSchemaRepair.EnsureAgentDomainSchemaAsync(db);      // the other app's start

        db.ChangeTracker.Clear();
        Assert.All(await db.AgentDomains.AsNoTracking().ToListAsync(), d => { Assert.NotNull(d.LiveEmailSentAt); Assert.NotNull(d.StuckEmailSentAt); });
        var email = new RecordingEmail();
        await Job(db, new ScriptedCheck { Look = Look(proof: Array.Empty<string>()) }, email).RunAsync();
        Assert.Empty(email.Sent);

        // A domain added afterwards starts with no mark.
        var added = Row(agentId, websiteId, "after595.example.test", live: true);
        db.Add(added);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Null((await db.AgentDomains.AsNoTracking().SingleAsync(d => d.Id == added.Id)).LiveEmailSentAt);
    }

    // ------------------------------------------------------------------ the screens --

    [Fact]
    public void The_guide_is_announced_moves_focus_and_copies_with_words()
    {
        var partial = Read("src/IPRO.Web/Views/Website/_DomainGuide.cshtml");
        Assert.Contains("aria-current=\"@(isCurrent ? \"step\" : null)\"", partial);
        Assert.Contains("aria-expanded=", partial);
        Assert.Contains("aria-controls=", partial);
        Assert.Contains("aria-live=\"polite\"", partial);
        Assert.Contains("data-copy=", partial);
        Assert.Contains(">Copy<", partial);
        Assert.Contains("data-guide-mail", partial);                        // where the email goes: checked, then said
        // Bootstrap's display utilities override the hidden attribute: never both on one element.
        Assert.DoesNotMatch(@"<[^>]*\bd-(block|flex|inline[-\w]*)\b[^>]*\bhidden=", partial);
        Assert.Contains("Your email is not affected", Read("src/IPRO.Utility/DomainSetupGuide.cs"));
        Assert.Contains("I need help", partial);
        Assert.Contains("<script src=\"~/js/domain-guide.js\" asp-append-version=\"true\"></script>", Read("src/IPRO.Web/Views/Website/Domains.cshtml"));

        var script = Read("src/IPRO.Web/wwwroot/js/domain-guide.js");
        Assert.Contains("20000", script);                                // about every 20 seconds
        Assert.Contains("visibilityState", script);                      // only while the page is in front
        Assert.Contains("429", script);                                  // a refusal slows it down, quietly
        Assert.Contains("RequestVerificationToken", script);
        Assert.Contains("Copied", script);
        Assert.Contains("Ctrl+C", script);                               // the clipboard can be refused
        Assert.Contains(".focus(", script);
        Assert.DoesNotContain("innerHTML", script);                      // what DNS holds is shown as text only
        Assert.DoesNotContain("insertAdjacentHTML", script);
    }

    [Fact]
    public async Task SuperAdmin_shows_the_same_step_for_each_advisers_domain()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await AdviserAsync(db);
        db.Add(Row(agentId, websiteId, "admin595.example.test"));
        await db.SaveChangesAsync();
        var check = new ScriptedCheck { Look = Look(proof: new[] { Token }, wwwCname: "old-host.example.net") };
        var controller = new IPRO.Admin.Controllers.DomainsController(db, Microsoft.Extensions.Options.Options.Create(new AzureDomainAutomationOptions()),
            new ScriptedAzure(), check, NullLogger<IPRO.Admin.Controllers.DomainsController>.Instance, new NullAudit());
        Sign(controller, 1);

        var view = Assert.IsType<ViewResult>(await controller.Index());
        var items = Assert.IsAssignableFrom<IReadOnlyList<IPRO.Admin.Models.AgentDomainViewModel>>(view.Model);
        var item = Assert.Single(items, i => i.Domain.DomainName == "www.admin595.example.test");
        Assert.NotNull(item.Guide);
        Assert.Equal("Step 2 of 4: Point www at iPro", item.Guide!.Heading);

        var html = await BuilderImportScreens571Tests.RenderAsync("~/Views/Domains/Index.cshtml", items);
        Assert.Contains("Step 2 of 4: Point www at iPro", html);
        Assert.Contains("old-host.example.net", html);
    }

    // ------------------------------------------------------- the independent review's findings --

    [Fact]
    public async Task H1_page_checks_never_spend_the_jobs_retries_and_progress_gives_them_back()
    {
        // Progress (the proof seen) starts the budget again, even though the domain is not connected yet.
        Zone("budget595.example.test", "ns1.domaincontrol.com", new Dictionary<(string, ushort), string[]>
        {
            [("_ipro-verify.budget595.example.test", ZoneDns.TXT)] = new[] { Token },
        });
        Resolve(("ns1.domaincontrol.com", "97.74.100.1"), (Target, Platform));
        var tired = Fresh("budget595.example.test");
        tired.RetryCount = 40;
        tired.NextRetryAt = DateTime.UtcNow.AddHours(4);
        await Service().CheckAsync(tired);
        Assert.NotNull(tired.OwnershipVerifiedAt);
        Assert.Equal(0, tired.RetryCount);
        Assert.Null(tired.NextRetryAt);
        Assert.False(tired.AutoRetryExhausted);

        // A check the page asks for that finds nothing new leaves the job's budget as it was.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await AdviserAsync(db);
        var row = Row(agentId, websiteId, "spend595.example.test");
        row.OwnershipVerifiedAt = DateTime.UtcNow.AddHours(-1);
        row.DnsStatus = row.AzureBindingStatus = AgentDomainStatus.Bound;          // the padlock on its way
        row.RetryCount = 5;
        row.LastCheckedAt = DateTime.UtcNow.AddMinutes(-10);
        db.Add(row);
        await db.SaveChangesAsync();
        var check = new ScriptedCheck { Look = Look(proof: new[] { Token }, wwwCname: Target), CountsAsTry = true };
        await Website(db, agentId, check).DomainGuide(row.Id);
        Assert.Equal(1, check.Checks);
        db.ChangeTracker.Clear();
        var saved = await db.AgentDomains.AsNoTracking().SingleAsync(d => d.Id == row.Id);
        Assert.Equal(5, saved.RetryCount);
        Assert.Null(saved.NextRetryAt);

        // A refused binding is asked again no more often than the job would; a padlock only in its first three hours.
        var now = DateTime.UtcNow;
        var refused = Proven(Fresh("refused595.test"));
        refused.AzureBindingStatus = AgentDomainStatus.Failed;
        refused.LastCheckedAt = now.AddMinutes(-2);
        Assert.False(ShouldCheckNow(refused, Describe(refused, Look(proof: new[] { Token }, wwwCname: Target), Expected, now), now));
        refused.LastCheckedAt = now.AddMinutes(-6);
        Assert.True(ShouldCheckNow(refused, Describe(refused, Look(proof: new[] { Token }, wwwCname: Target), Expected, now), now));
        var late = Proven(Fresh("late595.test"));
        late.DnsStatus = late.AzureBindingStatus = AgentDomainStatus.Bound;
        late.CreatedAt = now.AddHours(-5);
        late.OwnershipVerifiedAt = now.AddHours(-5);
        late.BoundAt = now.AddHours(-4);                                  // the padlock's clock runs from the connection
        late.LastCheckedAt = now.AddMinutes(-10);
        Assert.False(ShouldCheckNow(late, Describe(late, Look(proof: new[] { Token }, wwwCname: Target), Expected, now), now));

        // "We have been alerted" only once the alert has gone.
        var padlock = Describe(late, Look(proof: new[] { Token }, wwwCname: Target), Expected, now).Finish.Single(p => p.Key == "padlock").Detail;
        Assert.DoesNotContain("alerted and are looking", padlock);
        late.CertificateAlertSentAt = now.AddMinutes(-30);
        Assert.Contains("We have been alerted", Describe(late, Look(proof: new[] { Token }, wwwCname: Target), Expected, now).Finish.Single(p => p.Key == "padlock").Detail);
    }

    [Fact]
    public async Task H2_where_the_email_goes_is_checked_before_the_short_address_is_moved()
    {
        var domain = Proven(Fresh("mail595.test"));
        var atRisk = Describe(domain, Look(proof: new[] { Token }, wwwCname: Target, root: new[] { "198.51.100.20" }, asuid: Array.Empty<string>(),
            mx: new[] { "mail595.test" }, mailOnRoot: true), Expected, DateTime.UtcNow);
        Assert.True(atRisk.MailAtRisk);
        Assert.StartsWith("Careful: your email uses mail595.test", atRisk.MailNote);
        Assert.Contains("forwarding", atRisk.MailNote);
        Assert.Equal("Use forwarding", atRisk.Steps[2].Status);
        Assert.Contains("Leave that record as it is", atRisk.Steps[2].Seen);
        Assert.DoesNotContain("Change that A record", atRisk.Steps[2].Seen);

        var elsewhere = Describe(domain, Look(proof: new[] { Token }, wwwCname: Target, root: new[] { "198.51.100.20" }, mx: new[] { "mail595-test.mail.protection.outlook.com" }, mailOnRoot: false),
            Expected, DateTime.UtcNow);
        Assert.False(elsewhere.MailAtRisk);
        Assert.Equal("Your email is not affected: it goes to mail595-test.mail.protection.outlook.com, which these records do not touch.", elsewhere.MailNote);
        Assert.Equal("Your email is not affected: this domain has no email set up.",
            Describe(domain, Look(proof: new[] { Token }, wwwCname: Target, mx: Array.Empty<string>(), mailOnRoot: false), Expected, DateTime.UtcNow).MailNote);
        Assert.Equal("Checking where your email goes.", Describe(domain, null, Expected, DateTime.UtcNow).MailNote);

        // The look tells: an MX naming the short address, one naming a CNAME to it, or a mail server at its address.
        foreach (var (mx, extra, expected) in new (string, (string, ushort, string)?, bool)[]
        {
            ("cpanel595.example.test", null, true),
            ("mail.cpanel595.example.test", ("mail.cpanel595.example.test", ZoneDns.CNAME, "cpanel595.example.test"), true),
            ("cpanel595-test.mail.protection.outlook.com", null, false),
            ("box.mailhost595.example.net", null, true),                     // resolves to the web host's address below
        })
        {
            DomainCheckService.ForgetZones();
            var records = new Dictionary<(string, ushort), string[]>
            {
                [("cpanel595.example.test", ZoneDns.A)] = new[] { "198.51.100.20" },
                [("cpanel595.example.test", ZoneDns.MX)] = new[] { mx },
                [("www.cpanel595.example.test", ZoneDns.CNAME)] = new[] { Target },
            };
            if (extra is { } e) records[(e.Item1, e.Item2)] = new[] { e.Item3 };
            Zone("cpanel595.example.test", "ns1.hostpapa.com", records);
            Resolve(("ns1.hostpapa.com", "97.74.100.9"), (Target, Platform), ("box.mailhost595.example.net", "198.51.100.20"),
                ("cpanel595-test.mail.protection.outlook.com", "52.101.1.1"));
            var look = await Service().LookAsync(Fresh("cpanel595.example.test"));
            Assert.Equal(expected, look!.MailOnShortAddress);
            Assert.Equal(new[] { mx }, look.MailServers);
        }
    }

    [Fact]
    public async Task M1_a_signed_zone_keeps_the_validated_proof_and_the_zones_servers_must_agree()
    {
        // Signed (a DS record at the parent): the zone's own "yes" over plain TCP is not enough; the validating resolvers
        // decide, as before 595 -- here they have not seen it yet.
        Zone("signed595.example.test", "ns1.domaincontrol.com", new Dictionary<(string, ushort), string[]>
        {
            [("_ipro-verify.signed595.example.test", ZoneDns.TXT)] = new[] { Token },
        }, signed: true);
        Resolve(("ns1.domaincontrol.com", "97.74.100.1"), (Target, Platform), ("www.signed595.example.test", Platform));
        var signed = Fresh("signed595.example.test");
        await Service().CheckAsync(signed);
        Assert.Null(signed.OwnershipVerifiedAt);

        // Two servers that disagree settle nothing either.
        DomainCheckService.ForgetZones();
        var asked = Zone("split595.example.test", "ns1.domaincontrol.com", new Dictionary<(string, ushort), string[]>
        {
            [("_ipro-verify.split595.example.test", ZoneDns.TXT)] = new[] { Token },
        }, secondServer: "ns2.domaincontrol.com", silentAddress: "97.74.101.1");
        Resolve(("ns1.domaincontrol.com", "97.74.100.1"), ("ns2.domaincontrol.com", "97.74.101.1"), (Target, Platform), ("www.split595.example.test", Platform));
        var split = Fresh("split595.example.test");
        await Service().CheckAsync(split);
        Assert.Null(split.OwnershipVerifiedAt);
        Assert.Contains("97.74.101.1", asked.Servers);
    }

    [Fact]
    public void M2_a_padlock_held_up_comes_before_the_optional_short_address()
    {
        var domain = Proven(Fresh("caa595.test"));
        domain.DnsStatus = domain.AzureBindingStatus = AgentDomainStatus.Bound;
        domain.CaaBlockingName = "caa595.test";
        var guide = Describe(domain, Look(proof: new[] { Token }, wwwCname: Target, root: new[] { "198.51.100.3" }, asuid: Array.Empty<string>()), Expected, DateTime.UtcNow);
        Assert.Equal(4, guide.CurrentStep);
        Assert.Equal("caa595.test", guide.CaaName);
        Assert.Equal(StepState.Upcoming, guide.Steps[2].State);
    }

    [Fact]
    public void M3_a_live_domain_still_finishes_its_short_address_from_the_page()
    {
        var now = DateTime.UtcNow;
        var live = Proven(Fresh("later595.test"));
        live.DnsStatus = live.AzureBindingStatus = live.SslStatus = AgentDomainStatus.Bound;
        live.LastCheckedAt = now.AddMinutes(-5);
        var guide = Describe(live, Look(proof: new[] { Token }, wwwCname: Target, root: new[] { Platform }, asuid: new[] { Asuid }), Expected, now);
        Assert.True(guide.Live);
        Assert.True(guide.ShortRecordsSeen);
        Assert.True(ShouldCheckNow(live, guide, now));
        var script = Read("src/IPRO.Web/wwwroot/js/domain-guide.js");
        Assert.Contains("return !this.refused && !this.stopped && (!this.live || this.shortPending);", script);
    }

    [Fact]
    public async Task M4_SuperAdmin_waits_a_few_seconds_at_most_for_DNS()
    {
        var hanging = new ScriptedCheck { Hang = true };
        var rows = Enumerable.Range(1, 8).Select(i => { var d = Fresh($"hang{i}595.test"); d.Id = i; return d; }).ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var guides = await IPRO.Admin.Controllers.DomainsController.GuidesAsync(hanging, null, null, rows);
        Assert.True(clock.Elapsed < IPRO.Admin.Controllers.DomainsController.LookBudget + TimeSpan.FromSeconds(3), clock.Elapsed.ToString());
        Assert.Equal(8, guides.Count);
        Assert.All(guides.Values, g => Assert.Equal(1, g.CurrentStep));
    }

    [Fact]
    public void L3_the_guides_looks_are_counted_per_adviser_inside_the_action()
    {
        // The rate limiter runs before the sign-in is read (Program.cs), so it can only count per address; each adviser
        // is held to 20 a minute by the action itself (second review, L-a).
        var now = DateTime.UtcNow;
        var agent = 900000 + Random.Shared.Next(1, 99999);
        for (var i = 0; i < WebsiteController.GuideLooks.PerWindow; i++) Assert.True(WebsiteController.GuideLooks.Allow(agent, now));
        Assert.False(WebsiteController.GuideLooks.Allow(agent, now));
        Assert.True(WebsiteController.GuideLooks.Allow(agent + 1, now));                          // another adviser
        Assert.True(WebsiteController.GuideLooks.Allow(agent, now + WebsiteController.GuideLooks.Window + TimeSpan.FromSeconds(1)));
        Assert.Contains("if (!GuideLooks.Allow(AgentId, DateTime.UtcNow)) return TooMany();", Read("src/IPRO.Web/Controllers/WebsiteController.cs"));
    }

    [Fact]
    public void L5_L13_L14_www_by_address_on_a_live_row_a_name_that_is_its_own_zone_and_a_row_not_its_own()
    {
        var live = Proven(Fresh("flat595.test"));
        live.DnsStatus = live.AzureBindingStatus = live.SslStatus = AgentDomainStatus.Bound;
        var flattened = Describe(live, Look(proof: new[] { Token }, wwwAddresses: new[] { Platform }), Expected, DateTime.UtcNow);
        Assert.True(flattened.Live);
        Assert.Equal(StepState.Done, flattened.Steps[1].State);

        var greyCloud = Describe(Proven(Fresh("grey595.test")), Look(proof: new[] { Token }, wwwAddresses: new[] { "198.51.100.8" }, ns: "kate.ns.cloudflare.com"), Expected, DateTime.UtcNow);
        Assert.DoesNotContain("proxy", greyCloud.Steps[1].Seen);
        var orange = Describe(Proven(Fresh("orange595.test")), Look(proof: new[] { Token }, wwwAddresses: new[] { "104.21.32.7" }, ns: "kate.ns.cloudflare.com"), Expected, DateTime.UtcNow);
        Assert.Contains("proxy", orange.Steps[1].Seen);

        var apexRow = new AgentDomain { Id = 4, DomainName = "firm595.qc.test", WwwDomain = "firm595.qc.test", RootDomain = "firm595.qc.test", DnsTarget = Target, VerificationToken = Token };
        var apexLook = Look(proof: Array.Empty<string>(), wwwCname: null, wwwAddresses: Array.Empty<string>());
        var apex = Describe(apexRow, new DomainSetupLook
        {
            CheckedAtUtc = apexLook.CheckedAtUtc, FromZoneServers = true, Zone = "firm595.qc.test", NameServers = apexLook.NameServers,
            ProofValues = apexLook.ProofValues, WwwAddresses = apexLook.WwwAddresses, PlatformAddresses = apexLook.PlatformAddresses,
        }, Expected, DateTime.UtcNow);
        Assert.Contains("add www.firm595.qc.test instead", apex.Steps[1].Seen);

        var stray = Describe(new AgentDomain { Id = 5, DomainName = "www.stray595.test", WwwDomain = "www.stray595.test", RootDomain = "elsewhere595.test", DnsTarget = Target, VerificationToken = Token },
            null, Expected, DateTime.UtcNow);
        Assert.Contains("not consistent", stray.Refusal);
    }

    [Fact]
    public void L15_the_dns_seams_are_not_settable_by_the_apps()
    {
        Assert.Null(typeof(ZoneDns).GetProperty("QueryHook", BindingFlags.Public | BindingFlags.Static));
        Assert.Null(typeof(DomainOwnershipProof).GetProperty("LookupHook", BindingFlags.Public | BindingFlags.Static));
        var script = Read("src/IPRO.Web/wwwroot/js/domain-guide.js");
        Assert.Contains("response.redirected", script);                  // signed out: say so, stop
        Assert.Contains("IntersectionObserver", script);                 // only the guides on screen look again
        Assert.Contains("aria-disabled", script);                        // Check now keeps the focus
        Assert.Contains("Use your phone", script);
    }

    // -------------------------------------------- two cases from production, 2026-10-22 (www.411trades.com) --

    [Fact]
    public async Task GoDaddys_forwarding_records_beside_ours_are_named_with_where_to_delete_them()
    {
        // 411trades.com at GoDaddy: the owner added our A @ 40.89.19.0 beside the two A records GoDaddy's Forwarding
        // feature puts there and locks ("You can't modify records that have been applied by a product or service
        // connected to your domain"). They go only when the forwarding is deleted, on the domain's Forwarding tab.
        const string Sentence = "GoDaddy's domain forwarding is on: open the Forwarding tab for trades595.test and delete the forwarding; its two A records go away by themselves.";
        var domain = Proven(Fresh("trades595.test"));
        var beside = Describe(domain, Look(proof: new[] { Token }, wwwCname: Target, root: new[] { Platform, "15.197.225.128", "3.33.251.168" },
            asuid: new[] { Asuid }, ns: "ns57.domaincontrol.com", mx: Array.Empty<string>(), mailOnRoot: false), Expected, DateTime.UtcNow);
        Assert.StartsWith(Sentence, beside.Steps[2].Seen);
        Assert.DoesNotContain("Delete the other A", beside.Steps[2].Seen);

        // Forwarding alone (our record not added yet): the same, then the record to add, and the other choice in one sentence.
        var alone = Describe(domain, Look(proof: new[] { Token }, wwwCname: Target, root: new[] { "15.197.225.128", "3.33.251.168" },
            asuid: Array.Empty<string>(), ns: "ns57.domaincontrol.com", mx: Array.Empty<string>(), mailOnRoot: false), Expected, DateTime.UtcNow);
        Assert.StartsWith(Sentence, alone.Steps[2].Seen);
        Assert.Contains("add an A record for @ with the value 40.89.19.0", alone.Steps[2].Seen);
        Assert.Contains("only its home page", alone.Steps[2].Seen);
        Assert.Contains("we recommend", alone.Steps[2].Seen);

        // The domain check says the same where its own sentence is shown (the short address's line, step 4).
        DomainCheckService.ResolveHook = (host, ct) => Task.FromResult(host == "trades595.example.test"
            ? new[] { IPAddress.Parse(Platform), IPAddress.Parse("15.197.225.128"), IPAddress.Parse("3.33.251.168") }
            : new[] { IPAddress.Parse(Platform) });
        DomainOwnershipProof.LookupHook = (_, _) => Task.FromResult<string?>("{\"Status\":3}");
        var row = Proven(Fresh("trades595.example.test"));
        var azure = new ScriptedAzure();
        await Service(azure).CheckAsync(row);
        Assert.Equal("GoDaddy's domain forwarding is on: open the Forwarding tab for trades595.example.test and delete the forwarding; its two A records go away by themselves. Then click Check now.",
            row.RootLastError);
        Assert.Equal("Needs attention", ShortAddressState.Describe(row).Label);
        Assert.DoesNotContain(azure.Calls, c => c.StartsWith("ensure-root", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_padlock_clock_starts_when_the_domain_was_proven_and_connected_not_when_it_was_added()
    {
        // www.411trades.com: added on 10-21, proven on 10-22 at 14:14 UTC and connected at once; My Website said "Taking
        // longer than usual ... We've been alerted" within minutes, because the clock ran from the day it was added.
        var now = DateTime.UtcNow;
        var row = Proven(Fresh("clock595.test"));
        row.CreatedAt = now.AddDays(-1);
        row.OwnershipVerifiedAt = now.AddMinutes(-10);
        row.DnsStatus = row.AzureBindingStatus = AgentDomainStatus.Bound;
        row.SslStatus = AgentDomainStatus.BindingPending;
        row.LastCheckedAt = now.AddMinutes(-5);
        var guide = Describe(row, Look(proof: new[] { Token }, wwwCname: Target), Expected, now);
        Assert.False(guide.CertificateSlow);
        Assert.Equal("Ordered. It usually arrives within a few minutes, and it renews itself.", guide.Finish.Single(p => p.Key == "padlock").Detail);
        Assert.True(ShouldCheckNow(row, guide, now));                    // the page still helps it along
        Assert.Equal((true, (string?)null), DomainAutomationJob.CertificateWait(row, now));   // and IPRO is not alerted

        // Connected long after it was proven (the CNAME added later): the clock runs from the connection.
        row.OwnershipVerifiedAt = now.AddDays(-1);
        row.BoundAt = now.AddMinutes(-20);
        Assert.False(Describe(row, Look(proof: new[] { Token }, wwwCname: Target), Expected, now).CertificateSlow);
        Assert.Equal((true, (string?)null), DomainAutomationJob.CertificateWait(row, now));

        // Three hours after the connection it is late, for the page and for the alert alike.
        row.BoundAt = now.AddHours(-4);
        Assert.True(Describe(row, Look(proof: new[] { Token }, wwwCname: Target), Expected, now).CertificateSlow);
        Assert.Equal((true, (string?)"www.clock595.test"), DomainAutomationJob.CertificateWait(row, now));

        // The domain check writes down when the www name was connected, once.
        Resolve(("www.bound595.example.test", Platform), (Target, Platform));
        DomainOwnershipProof.LookupHook = (_, _) => Task.FromResult<string?>("{\"Status\":3}");
        var fresh = Proven(Fresh("bound595.example.test"));
        await Service().CheckAsync(fresh);
        Assert.Equal(AgentDomainStatus.Bound, fresh.AzureBindingStatus);
        Assert.NotNull(fresh.BoundAt);
        var first = fresh.BoundAt;
        fresh.LastCheckedAt = null;
        await Service().CheckAsync(fresh);
        Assert.Equal(first, fresh.BoundAt);
    }

    // ------------------------------------------------------------- the second review's findings --

    [Fact]
    public void MA_Check_now_runs_the_check_and_CAA_and_a_live_short_address_are_looked_at_again()
    {
        var now = DateTime.UtcNow;
        var live = Proven(Fresh("asked595.test"));
        live.DnsStatus = live.AzureBindingStatus = live.SslStatus = AgentDomainStatus.Bound;
        live.LastCheckedAt = now.AddMinutes(-2);
        var settled = Describe(live, Look(proof: new[] { Token }, wwwCname: Target), Expected, now);
        Assert.False(ShouldCheckNow(live, settled, now));                        // nothing new: the timer only looks
        Assert.True(ShouldCheckNow(live, settled, now, asked: true));            // Check now: the full check
        live.LastCheckedAt = now.AddSeconds(-10);
        Assert.False(ShouldCheckNow(live, settled, now, asked: true));           // 15 seconds apart at most (third review, L-5)

        // A CAA record to look at again: every five minutes, however old the row.
        var caa = Proven(Fresh("caa2595.test"));
        caa.DnsStatus = caa.AzureBindingStatus = AgentDomainStatus.Bound;
        caa.CaaBlockingName = "caa2595.test";
        caa.CreatedAt = now.AddDays(-5);
        caa.BoundAt = now.AddDays(-4);
        caa.LastCheckedAt = now.AddMinutes(-6);
        Assert.True(ShouldCheckNow(caa, Describe(caa, Look(proof: new[] { Token }, wwwCname: Target), Expected, now), now));
        caa.LastCheckedAt = now.AddMinutes(-3);
        Assert.False(ShouldCheckNow(caa, Describe(caa, Look(proof: new[] { Token }, wwwCname: Target), Expected, now), now));

        // A live domain whose short address is not working yet, with its addresses seen (the registrar's forwarding may
        // just have been switched on): every five minutes.
        var forwarding = Proven(Fresh("fwd2595.test"));
        forwarding.DnsStatus = forwarding.AzureBindingStatus = forwarding.SslStatus = AgentDomainStatus.Bound;
        forwarding.LastCheckedAt = now.AddMinutes(-6);
        var seen = Look(proof: new[] { Token }, wwwCname: Target, root: new[] { "15.197.225.128", "3.33.251.168" });
        Assert.True(ShouldCheckNow(forwarding, Describe(forwarding, seen, Expected, now), now));
        forwarding.LastCheckedAt = now.AddMinutes(-2);
        Assert.False(ShouldCheckNow(forwarding, Describe(forwarding, seen, Expected, now), now));
    }

    [Fact]
    public async Task MA_the_endpoint_passes_Check_now_through()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await AdviserAsync(db);
        var row = Row(agentId, websiteId, "asked2595.example.test", live: true);
        row.LastCheckedAt = DateTime.UtcNow.AddMinutes(-2);
        db.Add(row);
        await db.SaveChangesAsync();
        var check = new ScriptedCheck { Look = Look(proof: new[] { Token }, wwwCname: Target) };
        var cache = new MemoryCache(new MemoryCacheOptions());

        await Website(db, agentId, check, cache).DomainGuide(row.Id);
        Assert.Equal(0, check.Checks);
        await Website(db, agentId, check, cache).DomainGuide(row.Id, asked: true);
        Assert.Equal(1, check.Checks);
        Assert.Contains("fetch(this.url + (asked ? '?asked=true' : ''), {", Read("src/IPRO.Web/wwwroot/js/domain-guide.js"));
    }

    [Fact]
    public async Task MB_through_the_resolvers_a_missing_CNAME_is_never_called_wrong_and_is_not_asked_too_early()
    {
        var now = DateTime.UtcNow;
        var domain = Proven(Fresh("fallback595.test"));
        var reaches = Describe(domain, new DomainSetupLook
        {
            CheckedAtUtc = now, FromZoneServers = false, ProofValues = new[] { Token }, WwwCname = null, WwwAddresses = new[] { Platform }, PlatformAddresses = new[] { Platform },
        }, Expected, now);
        Assert.NotEqual(false, reaches.Steps[1].SeenRight);           // never called wrong (what it says: R3_L3_L4_L6)
        Assert.DoesNotContain("Delete", reaches.Steps[1].Seen);
        var elsewhere = Describe(domain, new DomainSetupLook
        {
            CheckedAtUtc = now, FromZoneServers = false, ProofValues = new[] { Token }, WwwCname = null, WwwAddresses = new[] { "198.51.100.4" }, PlatformAddresses = new[] { Platform },
        }, Expected, now);
        Assert.Contains("goes to 198.51.100.4, not to iPro", elsewhere.Steps[1].Seen);
        Assert.DoesNotContain("A record", elsewhere.Steps[1].Seen);

        // Before www resolves, the resolvers are asked neither for its CNAME nor for the proof.
        var asked = new List<string>();
        DomainOwnershipProof.LookupHook = (address, _) => { lock (asked) asked.Add(address); return Task.FromResult<string?>("{\"Status\":3}"); };
        Resolve((Target, Platform));
        var look = await Service().LookAsync(Fresh("early595.example.test"));
        Assert.NotNull(look);
        Assert.False(look!.FromZoneServers);
        Assert.DoesNotContain(asked, a => a.Contains("type=CNAME", StringComparison.Ordinal) || a.Contains("type=TXT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MC_a_server_that_refuses_one_zone_is_still_asked_for_others_and_only_an_unreachable_one_is_left_out()
    {
        var log = new List<string>();
        var serverDown = false;
        ZoneDns.QueryHook = (server, name, type, _) =>
        {
            lock (log) log.Add(name);
            if (serverDown) return Task.FromResult<ZoneDns.Answer?>(null);
            if (name.EndsWith("gone595.example.test", StringComparison.Ordinal))
                return Task.FromResult<ZoneDns.Answer?>(new ZoneDns.Answer(5, false, new List<ZoneDns.Record>()));   // REFUSED
            var found = name == "www.kept595.example.test" && type == ZoneDns.CNAME ? new[] { new ZoneDns.Record(ZoneDns.CNAME, name, Target) } : Array.Empty<ZoneDns.Record>();
            return Task.FromResult<ZoneDns.Answer?>(new ZoneDns.Answer(ZoneDns.NoError, true, found));
        };
        DomainOwnershipProof.LookupHook = (address, _) =>
        {
            foreach (var zone in new[] { "gone595.example.test", "kept595.example.test", "third595.example.test", "fourth595.example.test" })
                if (address.Contains("name=" + zone + "&type=NS", StringComparison.Ordinal))
                    return Task.FromResult<string?>("{\"Status\":0,\"Answer\":[{\"name\":\"" + zone + ".\",\"type\":2,\"TTL\":3600,\"data\":\"ns1.shared595.example.\"}]}");
            return Task.FromResult<string?>("{\"Status\":3}");
        };
        Resolve(("ns1.shared595.example", "97.74.200.1"), (Target, Platform));

        var gone = await Service().LookAsync(Fresh("gone595.example.test"));
        Assert.False(gone!.FromZoneServers);                                      // REFUSED: no answer of its own
        var kept = await Service().LookAsync(Fresh("kept595.example.test"));
        Assert.True(kept!.FromZoneServers);                                       // the same server, still asked
        Assert.Equal(Target, kept.WwwCname);

        serverDown = true;                                                        // now it cannot be reached at all
        await Service().LookAsync(Fresh("third595.example.test"));
        serverDown = false;
        log.Clear();
        var after = await Service().LookAsync(Fresh("fourth595.example.test"));
        Assert.Empty(log);                                                        // left out for ten minutes
        Assert.False(after!.FromZoneServers);
    }

    [Fact]
    public void Ld_Le_Lf_Lg_the_email_said_only_as_far_as_known_and_the_padlock_held_up_comes_first()
    {
        var now = DateTime.UtcNow;
        var domain = Proven(Fresh("mail2595.test"));
        Assert.Equal("Your email is not affected: this domain receives no email.",
            Describe(domain, Look(proof: new[] { Token }, wwwCname: Target, mx: new[] { "" }, mailOnRoot: false), Expected, now).MailNote);
        Assert.StartsWith("We could not check where your email goes.",
            Describe(domain, Look(proof: new[] { Token }, wwwCname: Target, mx: new[] { "mx.example.net" }, mailOnRoot: null), Expected, now).MailNote);

        // The short address already points at iPro and the email depends on it: the email is not arriving.
        var moved = Proven(Fresh("moved595.test"));
        moved.RootDnsStatus = moved.RootAzureBindingStatus = moved.RootSslStatus = AgentDomainStatus.Bound;
        moved.RootRedirectsToWww = true;
        var broken = Describe(moved, Look(proof: new[] { Token }, wwwCname: Target, root: new[] { Platform }, asuid: new[] { Asuid }, mx: new[] { "moved595.test" }, mailOnRoot: true), Expected, now);
        Assert.True(broken.MailBroken);
        Assert.False(broken.MailAtRisk);
        Assert.StartsWith("Your email may not be arriving", broken.MailNote);

        // A binding Azure refused while CAA keeps the padlock out: step 4 first too.
        var refused = Proven(Fresh("caa3595.test"));
        refused.AzureBindingStatus = AgentDomainStatus.Failed;
        refused.CaaBlockingName = "caa3595.test";
        var guide = Describe(refused, Look(proof: new[] { Token }, wwwCname: Target, root: new[] { "198.51.100.3" }, ns: "ns57.domaincontrol.com"), Expected, now);
        Assert.Equal(4, guide.CurrentStep);
        Assert.Equal("@", guide.CaaNameInBox);
        var cpanel = Describe(refused, Look(proof: new[] { Token }, wwwCname: Target, ns: "ns1.hostpapa.com"), Expected, now);
        Assert.Equal("caa3595.test.", cpanel.CaaNameInBox);
        Assert.Contains("data-guide-caa-box>@(guide.CaaNameInBox ?? \"@\")</code>", Read("src/IPRO.Web/Views/Website/_DomainGuide.cshtml"));
    }

    [Fact]
    public void Lh_Li_Lj_Lc_words_spoken_budgets_and_spacing()
    {
        // The short address's own retries every five minutes, and a refused one does not hold up the www check.
        var now = DateTime.UtcNow;
        var row = Proven(Fresh("root595.test"));
        row.LastCheckedAt = now.AddMinutes(-2);
        row.RootAzureBindingStatus = AgentDomainStatus.Failed;
        var look = Look(proof: new[] { Token }, wwwCname: Target, root: new[] { Platform }, asuid: new[] { Asuid });
        Assert.True(ShouldCheckNow(row, Describe(row, look, Expected, now), now));     // www not connected yet: after a minute
        row.DnsStatus = row.AzureBindingStatus = row.SslStatus = AgentDomainStatus.Bound;
        row.RootAzureBindingStatus = AgentDomainStatus.NotConfigured;
        Assert.False(ShouldCheckNow(row, Describe(row, look, Expected, now), now));    // the short address alone: five minutes
        row.LastCheckedAt = now.AddMinutes(-6);
        Assert.True(ShouldCheckNow(row, Describe(row, look, Expected, now), now));

        var script = Read("src/IPRO.Web/wwwroot/js/domain-guide.js");
        Assert.Contains("if (previous > 0 && step.number >= previous && step.number < shown && stateClass(step.state) === 'done') doneNow.push(step.number);", script);
        Assert.Contains("try { look = await _domainCheck.LookAsync(domain, budget.Token).WaitAsync(budget.Token); }", Read("src/IPRO.Scheduler/DomainAutomationJob.cs"));
        var check = Read("src/IPRO.Utility/DomainCheckService.cs");
        Assert.Contains("// A question cut short by the caller's budget is no answer: nothing is remembered from it (review L-c).\n            cancellationToken.ThrowIfCancellationRequested();", check);
        Assert.Contains("var batches = await Task.WhenAll(servers.Select(server => ZoneDns.QueryManyAsync(server, asked, cancellationToken)));", check);
    }

    // --------------------------------------------------------------- the Domain page (owner, 2026-10-22) --

    [Fact]
    public async Task The_domains_have_a_page_of_their_own_and_My_Website_keeps_a_summary()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await AdviserAsync(db);
        var live = Row(agentId, websiteId, "page595.test", live: true);
        live.IsPrimary = true;
        db.Add(live);
        await db.SaveChangesAsync();
        var website = await db.AgentWebsites.SingleAsync(w => w.Id == websiteId);
        website.CustomDomain = "www.page595.test";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var check = new ScriptedCheck();

        var page = Assert.IsType<ViewResult>(await Website(db, agentId, check).Domains());
        Assert.Null(page.ViewName);                                               // Views/Website/Domains.cshtml
        Assert.Equal(websiteId, Assert.IsType<AgentWebsite>(page.Model).Id);

        // Every domain action comes back to it.
        Assert.Equal("Domains", Assert.IsType<RedirectToActionResult>(await Website(db, agentId, check).AddDomain("second595.test")).ActionName);
        var second = await db.AgentDomains.AsNoTracking().SingleAsync(d => d.DomainName == "www.second595.test");
        Assert.Equal("Domains", Assert.IsType<RedirectToActionResult>(await Website(db, agentId, check).SetPrimaryDomain(second.Id)).ActionName);
        Assert.Equal("Domains", Assert.IsType<RedirectToActionResult>(await Website(db, agentId, check).SetPrimaryDomain(live.Id)).ActionName);
        Assert.Equal("Domains", Assert.IsType<RedirectToActionResult>(await Website(db, agentId, check).RemoveDomain(second.Id, "nope")).ActionName);

        // My Website's settings form leaves the domain alone, whatever value an old form still carries.
        var template = await db.WebsiteTemplates.AsNoTracking().FirstAsync();
        await Website(db, agentId, check).Save(new AgentWebsite { TemplateId = template.Id, SiteTitle = "Bakery", CustomDomain = "www.stale595.test", CustomDomainDisplay = "x" }, null, keepDomain: true);
        db.ChangeTracker.Clear();
        var saved = await db.AgentWebsites.AsNoTracking().SingleAsync(w => w.Id == websiteId);
        Assert.Equal("www.page595.test", saved.CustomDomain);
        Assert.Equal(string.Empty, saved.CustomDomainDisplay);
        Assert.False(await db.AgentDomains.AnyAsync(d => d.DomainName == "www.stale595.test"));

        // The address on the emails (552), set on the Domain page with 552's rule.
        Assert.Equal("Domains", Assert.IsType<RedirectToActionResult>(await Website(db, agentId, check).DomainDisplay("  WWW.Page595.Test ")).ActionName);
        db.ChangeTracker.Clear();
        Assert.Equal("WWW.Page595.Test", (await db.AgentWebsites.AsNoTracking().SingleAsync(w => w.Id == websiteId)).CustomDomainDisplay);
        var refusedDisplay = Website(db, agentId, check);
        await refusedDisplay.DomainDisplay("www.elsewhere595.test");
        Assert.Contains("must be www.page595.test with your own capitals", refusedDisplay.TempData["Error"] as string);
        db.ChangeTracker.Clear();
        Assert.Equal("WWW.Page595.Test", (await db.AgentWebsites.AsNoTracking().SingleAsync(w => w.Id == websiteId)).CustomDomainDisplay);

        // The ways there: a Domain button on My Website, the menu, and My Website's summary card.
        var myWebsite = Read("src/IPRO.Web/Views/Website/Index.cshtml");
        Assert.Contains("<a href=\"/portal/Website/Domains\" class=\"btn btn-outline-primary\"><i class=\"fas fa-route me-1\"></i>Domain</a>", myWebsite);
        Assert.Contains("data-domain-summary", myWebsite);
        Assert.Contains("<input type=\"hidden\" name=\"keepDomain\" value=\"true\" />", myWebsite);
        Assert.DoesNotContain("Custom Domain Setup", myWebsite);
        Assert.DoesNotContain("name=\"CustomDomain\"", myWebsite);
        Assert.DoesNotContain("_DomainGuide", myWebsite);
        Assert.Contains("href=\"/portal/Website/Domains\"", Read("src/IPRO.Web/Views/Shared/_Layout.cshtml"));
        Assert.Contains("PortalPaths.To(\"/Website/Domains\")", Read("src/IPRO.Scheduler/DomainAutomationJob.cs"));
    }

    // ------------------------------------------------------------ third review (2026-10-22) --

    [Fact]
    public async Task R3_M1_the_address_on_the_emails_follows_the_primary_domain_and_never_blocks_My_Website()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await AdviserAsync(db);
        var a = Row(agentId, websiteId, "a3595.test", live: true);
        a.IsPrimary = true;
        var b = Row(agentId, websiteId, "b3595.test", live: true);
        db.AddRange(a, b);
        await db.SaveChangesAsync();
        var website = await db.AgentWebsites.SingleAsync(w => w.Id == websiteId);
        website.CustomDomain = "www.a3595.test";
        website.CustomDomainDisplay = "WWW.A3595.Test";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var check = new ScriptedCheck();
        async Task<AgentWebsite> Stored()
        {
            db.ChangeTracker.Clear();
            return await db.AgentWebsites.AsNoTracking().SingleAsync(w => w.Id == websiteId);
        }

        // The same primary again: the address on the emails stays.
        await Website(db, agentId, check).SetPrimaryDomain(a.Id);
        Assert.Equal("WWW.A3595.Test", (await Stored()).CustomDomainDisplay);
        // Another primary: the old one's capitals are not this one's, so the emails use small letters until it is typed.
        await Website(db, agentId, check).SetPrimaryDomain(b.Id);
        Assert.Equal("www.b3595.test", (await Stored()).CustomDomain);
        Assert.Equal(string.Empty, (await Stored()).CustomDomainDisplay);

        // One left over from before the Domain page never stops My Website's settings from saving: that form does not
        // carry it, so it is not checked there (the emails ignore one that does not match).
        var tracked = await db.AgentWebsites.SingleAsync(w => w.Id == websiteId);
        tracked.CustomDomainDisplay = "www.Old3595.test";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var template = await db.WebsiteTemplates.AsNoTracking().FirstAsync();
        var save = Website(db, agentId, check);
        await save.Save(new AgentWebsite { TemplateId = template.Id, SiteTitle = "Bakery three" }, null, keepDomain: true);
        Assert.Null(save.TempData["Error"]);
        Assert.Equal("Bakery three", (await Stored()).SiteTitle);
        Assert.Equal("www.Old3595.test", (await Stored()).CustomDomainDisplay);

        // Removing the primary domain moves the website to the other one and drops an address that is not that one.
        await Website(db, agentId, check).RemoveDomain(b.Id, "www.b3595.test");
        Assert.Equal("www.a3595.test", (await Stored()).CustomDomain);
        Assert.Equal(string.Empty, (await Stored()).CustomDomainDisplay);

        // The Domain page says so when the address typed there is not the primary domain's.
        Assert.Contains("data-display-unused", Read("src/IPRO.Web/Views/Website/Domains.cshtml"));
    }

    [Fact]
    public async Task R3_L2_an_email_already_stopped_is_said_on_the_row_and_in_SuperAdmin()
    {
        var now = DateTime.UtcNow;
        // The short address points at iPro and its padlock is still on its way: the email has already stopped.
        var securing = Proven(Fresh("securing3595.test"));
        securing.DnsStatus = securing.AzureBindingStatus = securing.SslStatus = AgentDomainStatus.Bound;
        securing.RootDnsStatus = securing.RootAzureBindingStatus = AgentDomainStatus.Bound;
        securing.RootSslStatus = AgentDomainStatus.BindingPending;
        var guide = Describe(securing, Look(proof: new[] { Token }, wwwCname: Target, root: new[] { Platform }, asuid: new[] { Asuid }, mx: new[] { "securing3595.test" }, mailOnRoot: true), Expected, now);
        Assert.True(guide.MailBroken);
        Assert.False(guide.MailAtRisk);
        Assert.StartsWith("Your email may not be arriving", guide.MailNote);

        // The Domain page asks once for each live domain whose short address points here, and the row says it.
        Assert.Contains("data-guide-mail-row=\"@domain.Id\"", Read("src/IPRO.Web/Views/Website/Domains.cshtml"));
        Assert.Contains("data-guide-mail-once=\"@(", Read("src/IPRO.Web/Views/Website/_DomainGuide.cshtml"));
        var script = Read("src/IPRO.Web/wwwroot/js/domain-guide.js");
        Assert.Contains("'[data-guide-mail-row=\"' + this.id + '\"]'", script);
        Assert.Contains("section.getAttribute('data-guide-mail-once') === 'true'", script);

        // SuperAdmin looks at those too, and only those of the live ones.
        var direct = Proven(Fresh("superlive3595.test"));
        direct.Id = 91;
        direct.DnsStatus = direct.AzureBindingStatus = direct.SslStatus = AgentDomainStatus.Bound;
        direct.RootDnsStatus = direct.RootAzureBindingStatus = direct.RootSslStatus = AgentDomainStatus.Bound;
        direct.RootRedirectsToWww = true;
        var quiet = Proven(Fresh("quietlive3595.test"));
        quiet.Id = 92;
        quiet.DnsStatus = quiet.AzureBindingStatus = quiet.SslStatus = AgentDomainStatus.Bound;
        var check = new ScriptedCheck { Look = Look(proof: new[] { Token }, wwwCname: Target, root: new[] { Platform }, asuid: new[] { Asuid }, mx: new[] { "superlive3595.test" }, mailOnRoot: true) };
        var guides = await IPRO.Admin.Controllers.DomainsController.GuidesAsync(check, null, null, new[] { direct, quiet });
        Assert.Equal(new[] { "www.superlive3595.test" }, check.Looked);
        Assert.True(guides[91].MailBroken);
    }

    [Fact]
    public void R3_L3_L4_L6_the_CAA_name_box_the_platforms_address_through_the_resolvers_and_the_docs()
    {
        // L-3: a host that leaves the name empty for the domain itself keeps it empty after a look, and says so.
        Assert.Contains("text(caa.querySelector('[data-guide-caa-box]'), data.caaNameInBox == null ? '@' : data.caaNameInBox);", Read("src/IPRO.Web/wwwroot/js/domain-guide.js"));
        Assert.Contains("data-guide-caa-empty hidden=\"@(guide.CaaNameInBox != \"\")\">(leave it empty)</span>", Read("src/IPRO.Web/Views/Website/_DomainGuide.cshtml"));

        // L-4: through the resolvers, the platform's address could be the CNAME or an A record Azure will not connect:
        // said as it is, and the step left to what iPro recorded.
        var now = DateTime.UtcNow;
        var domain = Proven(Fresh("reaches3595.test"));
        var look = new DomainSetupLook
        {
            CheckedAtUtc = now, FromZoneServers = false, ProofValues = new[] { Token }, WwwCname = null, WwwAddresses = new[] { Platform }, PlatformAddresses = new[] { Platform },
        };
        var reaches = Describe(domain, look, Expected, now);
        Assert.Null(reaches.Steps[1].SeenRight);
        Assert.Equal(StepState.Current, reaches.Steps[1].State);
        Assert.Contains("reaches iPro's address", reaches.Steps[1].Seen);
        Assert.DoesNotContain("Delete", reaches.Steps[1].Seen);
        domain.DnsStatus = AgentDomainStatus.DnsReady;
        Assert.Equal(StepState.Done, Describe(domain, look, Expected, now).Steps[1].State);

        // L-6: the list is "Your domains" on the Domain page now.
        Assert.DoesNotContain("Domain Manager", Read("DOCS/05_DOMAINS_AND_LEADS.md"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task R3_L5_Check_now_soon_after_another_check_still_checks_and_says_when_it_did_not()
    {
        var now = DateTime.UtcNow;
        var row = Proven(Fresh("asked3595.test"));
        var guide = Describe(row, Look(proof: new[] { Token }), Expected, now);
        row.LastCheckedAt = now.AddSeconds(-20);                 // the five-minute job, a moment ago
        Assert.True(ShouldCheckNow(row, guide, now, asked: true));
        Assert.False(ShouldCheckNow(row, guide, now));
        row.LastCheckedAt = now.AddSeconds(-5);
        Assert.False(ShouldCheckNow(row, guide, now, asked: true));

        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await AdviserAsync(db);
        var stored = Row(agentId, websiteId, "asked4595.example.test", live: true);
        stored.LastCheckedAt = DateTime.UtcNow.AddSeconds(-30);
        db.Add(stored);
        await db.SaveChangesAsync();
        var check = new ScriptedCheck { Look = Look(proof: new[] { Token }, wwwCname: Target) };
        var cache = new MemoryCache(new MemoryCacheOptions());
        var first = Assert.IsType<JsonResult>(await Website(db, agentId, check, cache).DomainGuide(stored.Id, asked: true));
        Assert.True(Assert.IsType<Guide>(first.Value).CheckedNow);
        var again = Assert.IsType<JsonResult>(await Website(db, agentId, check, cache).DomainGuide(stored.Id, asked: true));
        Assert.False(Assert.IsType<Guide>(again.Value).CheckedNow);
        Assert.Equal(1, check.Checks);
        Assert.Contains("data.checkedNow", Read("src/IPRO.Web/wwwroot/js/domain-guide.js"));
    }

    // ------------------------------------------------------------------- plumbing --

    private static AgentDomain Fresh(string root) => new()
    {
        Id = 58, AgentUserId = 7, DomainName = "www." + root, WwwDomain = "www." + root, RootDomain = root, DnsTarget = Target,
        VerificationToken = Token, CreatedAt = DateTime.UtcNow.AddMinutes(-2),
        RootAzureBindingStatus = AgentDomainStatus.NotConfigured, RootSslStatus = AgentDomainStatus.NotConfigured,
    };

    private static AgentDomain Proven(AgentDomain domain)
    {
        domain.OwnershipVerifiedAt = DateTime.UtcNow.AddMinutes(-1);
        return domain;
    }

    private static DomainSetupLook Look(string[]? proof = null, string? wwwCname = null, string[]? wwwAddresses = null, string[]? root = null,
        string[]? asuid = null, string ns = "ns1.someprovider.example", string[]? mx = null, bool? mailOnRoot = null) => new()
    {
        CheckedAtUtc = DateTime.UtcNow, FromZoneServers = true, Zone = "", NameServers = new[] { ns },
        ProofValues = proof, WwwCname = wwwCname, WwwAddresses = wwwAddresses ?? (wwwCname == Target ? new[] { Platform } : Array.Empty<string>()),
        RootAddresses = root, AsuidValues = asuid, MailServers = mx, MailOnShortAddress = mailOnRoot, PlatformAddresses = new[] { Platform },
    };

    private sealed class ZoneLog
    {
        public List<string> Names { get; } = new();
        public List<string> Servers { get; } = new();
    }

    // The zone's own servers (ZoneDns.QueryHook) and the public resolvers that name them (NS through DoH).
    private static ZoneLog Zone(string zone, string nameServer, Dictionary<(string Name, ushort Type), string[]> records, string? txtFromResolvers = null,
        bool signed = false, string? secondServer = null, string? silentAddress = null)
    {
        var log = new ZoneLog();
        ZoneDns.QueryHook = (server, name, type, _) =>
        {
            lock (log) { log.Names.Add(name); log.Servers.Add(server.ToString()); }
            if (server.ToString() == silentAddress)                          // a second server that has none of it
                return Task.FromResult<ZoneDns.Answer?>(new ZoneDns.Answer(ZoneDns.NameError, true, new List<ZoneDns.Record>()));
            var found = records.TryGetValue((name, type), out var values) ? values : Array.Empty<string>();
            var rcode = records.Keys.Any(k => k.Name == name) ? ZoneDns.NoError : ZoneDns.NameError;
            return Task.FromResult<ZoneDns.Answer?>(new ZoneDns.Answer(rcode, true, found.Select(v => new ZoneDns.Record(type, name, v.TrimEnd('.'))).ToList()));
        };
        DomainOwnershipProof.LookupHook = (address, _) =>
        {
            if (address.Contains("name=" + Uri.EscapeDataString(zone) + "&type=NS", StringComparison.Ordinal))
                return Task.FromResult<string?>("{\"Status\":0,\"Answer\":[{\"name\":\"" + zone + ".\",\"type\":2,\"TTL\":3600,\"data\":\"" + nameServer + ".\"}" +
                    (secondServer == null ? "" : ",{\"name\":\"" + zone + ".\",\"type\":2,\"TTL\":3600,\"data\":\"" + secondServer + ".\"}") + "]}");
            if (signed && address.Contains("name=" + Uri.EscapeDataString(zone) + "&type=DS", StringComparison.Ordinal))
                return Task.FromResult<string?>("{\"Status\":0,\"Answer\":[{\"name\":\"" + zone + ".\",\"type\":43,\"TTL\":3600,\"data\":\"12345 13 2 ABCDEF\"}]}");
            if (txtFromResolvers != null && address.Contains("type=TXT", StringComparison.Ordinal))
                return Task.FromResult<string?>("{\"Status\":0,\"Answer\":[{\"name\":\"x.\",\"type\":16,\"TTL\":300,\"data\":\"\\\"" + txtFromResolvers + "\\\"\"}]}");
            return Task.FromResult<string?>("{\"Status\":3}");
        };
        return log;
    }

    private static void Resolve(params (string Host, string Address)[] records)
    {
        var table = records.ToDictionary(r => r.Host, r => new[] { IPAddress.Parse(r.Address) }, StringComparer.OrdinalIgnoreCase);
        DomainCheckService.ResolveHook = (host, ct) => Task.FromResult(table.TryGetValue(host, out var found) ? found : Array.Empty<IPAddress>());
    }

    private static DomainCheckService Service(IAzureDomainAutomationService? azure = null) =>
        new(new NoHttp(), azure ?? new ScriptedAzure(), NullLogger<DomainCheckService>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:WebsiteAddress"] = Platform }).Build());

    private static AgentDomain Row(int agentId, int websiteId, string root, bool live = false) => new()
    {
        AgentUserId = agentId, AgentWebsiteId = websiteId, DomainName = "www." + root, WwwDomain = "www." + root, RootDomain = root,
        DnsTarget = Target, IsPrimary = false, VerificationToken = Token, CreatedAt = DateTime.UtcNow.AddMinutes(-3),
        OwnershipVerifiedAt = live ? DateTime.UtcNow.AddDays(-1) : null,
        DnsStatus = live ? AgentDomainStatus.Bound : AgentDomainStatus.PendingDns,
        AzureBindingStatus = live ? AgentDomainStatus.Bound : AgentDomainStatus.BindingPending,
        SslStatus = live ? AgentDomainStatus.Bound : AgentDomainStatus.BindingPending,
    };

    private static async Task<(int AgentId, int WebsiteId)> AdviserAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T595-{Guid.NewGuid():N}")[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        var template = new WebsiteTemplate { Name = "Modern", TemplateKey = ($"t595-{Guid.NewGuid():N}")[..16], BusinessType = "All", IsActive = true };
        db.AddRange(rule, template);
        await db.SaveChangesAsync();
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.InstantWebsite, FeatureName = "Website", IsIncluded = true });
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.MultiDomainSupport, FeatureName = "Domains", IsIncluded = true, LimitValue = -1 });
        var agent = new AgentUser
        {
            UserName = ($"t595-{Guid.NewGuid():N}")[..20], Email = ($"t595-{Guid.NewGuid():N}")[..14] + "@example.test",
            FirstName = "Sam", LastName = "Baker", CompanyName = "Baker Co",
            DomainName = ($"t595-{Guid.NewGuid():N}")[..24] + ".247advisers.com", Country = "Canada", Province = "Ontario",
            PackageId = rule.Id, TrialEndsAt = null,
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        db.Add(new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 60m, Status = BillingStatus.Active,
            Period = BillingPeriod.Monthly, StartDate = DateTime.UtcNow.AddDays(-40), NextBillingDate = DateTime.UtcNow.AddDays(20),
        });
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, SiteTitle = "Bakery", IsPublished = true };
        db.Add(website);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (agent.Id, website.Id);
    }

    private static WebsiteController Website(IPRODbContext db, int agentId, IDomainCheckService check, IMemoryCache? cache = null)
    {
        var controller = new WebsiteController(
            new WebsiteService(new UnitOfWork(db)), new NullBlob(), new PackageEntitlementService(new UnitOfWork(db), db),
            new AgentService(new UnitOfWork(db), new PasswordHasher<AgentUser>()),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:WebsiteDnsTarget"] = Target, ["App:WebsiteAddress"] = Platform, ["App:DomainVerificationId"] = Asuid,
            }).Build(), db, check, new ScriptedAzure(), NullLogger<WebsiteController>.Instance, cache ?? new MemoryCache(new MemoryCacheOptions()));
        Sign(controller, agentId);
        return controller;
    }

    private static DomainAutomationJob Job(IPRODbContext db, IDomainCheckService check, RecordingEmail email) =>
        new(db, check, email, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:BaseUrl"] = "https://app.example.test", ["App:WebsiteDnsTarget"] = Target, ["App:WebsiteAddress"] = Platform, ["App:DomainVerificationId"] = Asuid,
        }).Build(), NullLogger<DomainAutomationJob>.Instance, new ScriptedAzure());

    private static void Sign(Controller controller, int agentId)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString(CultureInfo.InvariantCulture)) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NullTempData());
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(DomainGuideScreens595Tests.RepoRoot(), relative)).Replace("\r\n", "\n");

    // The domain check as the page sees it: a scripted look, and a count of the checks that would ask Azure.
    private sealed class ScriptedCheck : IDomainCheckService
    {
        public DomainSetupLook? Look { get; set; }
        public List<string> Looked { get; } = new();
        public int Checks { get; private set; }
        // As the real check does for a domain it does not find connected: one of the job's tries spent.
        public bool CountsAsTry { get; set; }
        // Name servers that never answer.
        public bool Hang { get; set; }

        public Task<bool> CheckAsync(AgentDomain domain, CancellationToken cancellationToken = default)
        {
            Checks++;
            domain.LastCheckedAt = DateTime.UtcNow;
            if (CountsAsTry)
            {
                domain.RetryCount++;
                domain.NextRetryAt = DateTime.UtcNow.AddMinutes(30);
            }
            return Task.FromResult(false);
        }

        public async Task<DomainSetupLook?> LookAsync(AgentDomain domain, CancellationToken cancellationToken = default)
        {
            lock (Looked) Looked.Add(domain.DomainName);
            if (Hang) await Task.Delay(Timeout.Infinite, cancellationToken);
            return Look;
        }
    }

    private sealed class ScriptedAzure : IAzureDomainAutomationService
    {
        public bool IsConfigured => true;
        public List<string> Calls { get; } = new();

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
            return Task.FromResult(new AzureDomainAutomationResult { Success = true, Message = "removed" });
        }
    }

    internal sealed class RecordingEmail : IPRO.Email.IEmailService
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = new();

        public Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        {
            Sent.Add((toEmail, subject, htmlBody));
            return Task.FromResult(true);
        }

        public async Task<IPRO.Email.EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        {
            await SendAsync(toEmail, toName, subject, htmlBody);
            return IPRO.Email.EmailSendResult.Sent();
        }

        public Task<bool> SendBulkAsync(IEnumerable<IPRO.Email.EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
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

// The DNS reader: the zone's own servers' answers are read as untrusted bytes.
public class ZoneDns595Tests
{
    [Fact]
    public void A_query_is_one_question_of_the_asked_type_without_recursion()
    {
        var query = ZoneDns.BuildQuery(0x1234, "_ipro-verify.DrHug.ca.", ZoneDns.TXT)!;
        Assert.Equal(new byte[] { 0x12, 0x34, 0x00, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, query[..12]);
        Assert.Equal("\u000c_ipro-verify\u0005drhug\u0002ca\u0000", Encoding.ASCII.GetString(query, 12, query.Length - 16));
        Assert.Equal(new byte[] { 0, 16, 0, 1 }, query[^4..]);
        Assert.Null(ZoneDns.BuildQuery(1, new string('a', 64) + ".ca", ZoneDns.A));
        Assert.Null(ZoneDns.BuildQuery(1, "", ZoneDns.A));
    }

    [Fact]
    public void Answers_are_read_with_compressed_names_and_split_TXT_strings()
    {
        var message = Response(0xBEEF, rcode: 0, "www.drhug.ca", ZoneDns.CNAME,
            Answer(Pointer(12), ZoneDns.CNAME, Name("ipro-prod-web.azurewebsites.net")),
            Answer(Pointer(12), ZoneDns.TXT, Strings("ipro-0123456789abcdef", "0123456789abcdef")),
            Answer(Pointer(12), ZoneDns.A, new byte[] { 40, 89, 19, 0 }),
            Answer(Pointer(12), 99, new byte[] { 1, 2, 3 }));

        var answer = ZoneDns.Parse(message, 0xBEEF)!;

        Assert.Equal(ZoneDns.NoError, answer.Rcode);
        Assert.True(answer.Authoritative);
        Assert.Equal(new[] { "ipro-prod-web.azurewebsites.net" }, answer.Values(ZoneDns.CNAME, "WWW.drhug.ca."));
        Assert.Equal(new[] { "ipro-0123456789abcdef0123456789abcdef" }, answer.Values(ZoneDns.TXT, "www.drhug.ca"));
        Assert.Equal(new[] { "40.89.19.0" }, answer.Values(ZoneDns.A, "www.drhug.ca"));
        Assert.Equal(3, answer.Records.Count);                           // the unknown type is skipped
    }

    [Fact]
    public void A_mail_record_gives_its_mail_server()
    {
        var mx = new List<byte> { 0, 10 };
        mx.AddRange(Name("mail.cpanel.example"));
        var answer = ZoneDns.Parse(Response(9, rcode: 0, "cpanel.example", ZoneDns.MX, Answer(Pointer(12), ZoneDns.MX, mx.ToArray())), 9)!;
        Assert.Equal(new[] { "mail.cpanel.example" }, answer.Values(ZoneDns.MX, "cpanel.example"));
        Assert.Null(ZoneDns.Parse(Response(9, rcode: 0, "cpanel.example", ZoneDns.MX, Answer(Pointer(12), ZoneDns.MX, new byte[] { 0, 10 })), 9));
    }

    [Fact]
    public async Task R3_L1_a_server_that_says_anything_is_reachable_and_an_oversized_answer_is_set_aside()
    {
        var good = Response(2, rcode: 0, "www.drhug.ca", ZoneDns.A, Answer(Pointer(12), ZoneDns.A, new byte[] { 40, 89, 19, 0 }));
        var huge = new byte[20000];
        huge[1] = 1;                                                     // the answer to question 1, over 16 KB
        var pending = new Dictionary<ushort, int> { [1] = 0, [2] = 1 };
        var answers = new ZoneDns.Answer?[2];
        Assert.True(await ZoneDns.ReadAnswersAsync(new MemoryStream(Framed(huge).Concat(Framed(good)).ToArray()), pending, answers, CancellationToken.None));
        Assert.False(answers[0]!.Usable);                                // set aside: no answer of its own, never a "no"
        Assert.Equal(new[] { "40.89.19.0" }, answers[1]!.Values(ZoneDns.A, "www.drhug.ca"));

        // A length too short for a message: it said something, so it is not unreachable.
        Assert.True(await ZoneDns.ReadAnswersAsync(new MemoryStream(new byte[] { 0, 5, 1, 2, 3, 4, 5 }), new Dictionary<ushort, int> { [1] = 0 }, new ZoneDns.Answer?[1], CancellationToken.None));
        // Closed without a word: unreachable.
        Assert.False(await ZoneDns.ReadAnswersAsync(new MemoryStream(), new Dictionary<ushort, int> { [1] = 0 }, new ZoneDns.Answer?[1], CancellationToken.None));
    }

    private static byte[] Framed(byte[] message) => new[] { (byte)(message.Length >> 8), (byte)message.Length }.Concat(message).ToArray();

    [Fact]
    public void No_such_name_is_an_answer_and_anything_malformed_is_none()
    {
        Assert.Equal(ZoneDns.NameError, ZoneDns.Parse(Response(7, rcode: 3, "nope.drhug.ca", ZoneDns.TXT), 7)!.Rcode);

        var good = Response(7, rcode: 0, "www.drhug.ca", ZoneDns.A, Answer(Pointer(12), ZoneDns.A, new byte[] { 40, 89, 19, 0 }));
        Assert.Null(ZoneDns.Parse(good, 8));                             // another query's answer
        var servfail = ZoneDns.Parse(Response(7, rcode: 2, "www.drhug.ca", ZoneDns.A), 7)!;   // SERVFAIL: an answer, not a usable one
        Assert.False(servfail.Usable);
        Assert.Empty(servfail.Records);
        for (var cut = 1; cut < good.Length; cut++)
            Assert.Null(ZoneDns.Parse(good.AsSpan(0, cut), 7));          // every truncation
        var query = good.ToArray();
        query[2] &= 0x7F;
        Assert.Null(ZoneDns.Parse(query, 7));                            // a question, not an answer

        // A name that points at itself, and one that points past the end.
        Assert.Null(ZoneDns.Parse(Response(7, rcode: 0, "www.drhug.ca", ZoneDns.CNAME, Answer(Pointer(12), ZoneDns.CNAME, new byte[] { 0xC0, 0x0C + 0 })).Concat(Array.Empty<byte>()).ToArray().Also(m => Loop(m)), 7));
        Assert.Null(ZoneDns.Parse(Response(7, rcode: 0, "www.drhug.ca", ZoneDns.CNAME, Answer(Pointer(12), ZoneDns.CNAME, new byte[] { 0xC3, 0xFF })), 7));
        // An address of the wrong length.
        Assert.Null(ZoneDns.Parse(Response(7, rcode: 0, "www.drhug.ca", ZoneDns.A, Answer(Pointer(12), ZoneDns.A, new byte[] { 1, 2, 3 })), 7));
    }

    // Makes the question's name a pointer to itself.
    private static void Loop(byte[] message)
    {
        message[12] = 0xC0;
        message[13] = 12;
    }

    internal static byte[] Response(ushort id, int rcode, string question, ushort type, params byte[][] answers)
    {
        var bytes = new List<byte> { (byte)(id >> 8), (byte)id, 0x84, (byte)rcode, 0, 1, 0, (byte)answers.Length, 0, 0, 0, 0 };
        bytes.AddRange(Name(question));
        bytes.AddRange(new byte[] { 0, (byte)type, 0, 1 });
        foreach (var answer in answers) bytes.AddRange(answer);
        return bytes.ToArray();
    }

    internal static byte[] Answer(byte[] owner, ushort type, byte[] data)
    {
        var bytes = new List<byte>(owner) { (byte)(type >> 8), (byte)type, 0, 1, 0, 0, 1, 0x2C, (byte)(data.Length >> 8), (byte)data.Length };
        bytes.AddRange(data);
        return bytes.ToArray();
    }

    internal static byte[] Pointer(int at) => new[] { (byte)(0xC0 | (at >> 8)), (byte)at };

    internal static byte[] Name(string name)
    {
        var bytes = new List<byte>();
        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        return bytes.ToArray();
    }

    internal static byte[] Strings(params string[] parts)
    {
        var bytes = new List<byte>();
        foreach (var part in parts)
        {
            bytes.Add((byte)part.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(part));
        }
        return bytes.ToArray();
    }
}

internal static class ByteArrayAlso
{
    public static byte[] Also(this byte[] bytes, Action<byte[]> change)
    {
        change(bytes);
        return bytes;
    }
}
