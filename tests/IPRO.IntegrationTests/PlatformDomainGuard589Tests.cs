using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// 589 (2026-10-18), found by 586's independent review. My Website refused a custom domain only when it
// was in the free-site zone (247advisers.com) or another adviser's, so an adviser whose package has extra
// domains could add iPro's OWN names -- app.iproadvisers.com, the brand names, the admin host, the App
// Service's own name. Removing it again sent Azure a DELETE for that name's binding and certificate on the
// production web app: one adviser could take iPro's main address offline.
//
//   - My Website (Add, and the Save form's custom-domain box) refuses every iPro name, in each of the three
//     forms it compares (the name, the short address, the www name), with a plain sentence.
//   - The domain automation never binds, re-binds or deletes an iPro name, whatever a row says: a stored
//     row from before this fix, the admin app's erasure of an adviser, the five-minute domain check. Nothing
//     is sent to Azure for one, and a Warning names it.
//   - The domain check refuses such a row before any lookup or probe (a probe of iPro's own address finds
//     iPro's page and used to read that as "bound").
//
//   - Only a host name is stored or sent (the review): a name with a backslash, a percent sign, a control or
//     zero-width character is refused, and a name that differs from iPro's only in a dot or a dash too.
//
// In ConfigHazardTests' collection: AzureDomainAutomationService.SiteNameProvider and
// DomainCheckService.ResolveHook are seams for the whole process. Every lookup here throws, so a guard
// that slipped would fail a test instead of asking the real DNS (or iPro's own site) anything.
[Collection(ConfigHazardTests.AzureStatics)]
public class PlatformDomainGuard589Tests : IDisposable
{
    public PlatformDomainGuard589Tests()
    {
        AzureDomainAutomationService.SiteNameProvider = () => "ipro-prod-web";
        DomainCheckService.ResolveHook = (host, _) => throw new InvalidOperationException("589: nothing may be looked up here (" + host + ")");
    }

    public void Dispose()
    {
        AzureDomainAutomationService.SiteNameProvider = () => Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");
        DomainCheckService.ResolveHook = (host, ct) => Dns.GetHostAddressesAsync(host, ct);
    }

    // iPro's names as an adviser might type them. Every one is refused with production's settings.
    private static readonly string[] IproNames =
    {
        "app.iproadvisers.com", "https://APP.iProAdvisers.com/portal/Website", "app.iproadvisers.com.",
        "iproadvisers.com", "www.iproadvisers.com", "admin.iproadvisers.com", "mail.iproadvisers.com",
        "www.iproaccountants.com", "iproaccountants.com", "www.ipromortgages.com", "ipromortgages.com",
        "ipro-prod-web.azurewebsites.net", "ipro-prod-web.scm.azurewebsites.net", "someone-else.azurewebsites.net",
        "localhost", "portal.localhost", "crm.to", "www.crm.to",
        // The short address's letters spelled full-width: the same name to a resolver that maps them.
        "app.iproadvisers." + new string(new[] { (char)0xFF43, (char)0xFF4F, (char)0xFF4D }),
    };

    // ------------------------------------------------------------------ My Website --

    [Fact]
    public async Task An_adviser_cannot_add_any_of_iPros_own_names_as_a_custom_domain()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, _, _) = await AdviserAsync(db);
        var controller = Controller(db, agentId, ProductionConfig(), new ScriptedAzure());

        // The harness can add a domain at all: an adviser's own name goes in.
        await controller.AddDomain("www.smith589.example.test");
        Assert.Null(controller.TempData["Error"]);
        Assert.True(await db.AgentDomains.AnyAsync(d => d.AgentUserId == agentId && d.DomainName == "www.smith589.example.test"));

        foreach (var name in IproNames)
        {
            controller.TempData.Clear();
            var result = await controller.AddDomain(name);
            Assert.Equal("Index", Assert.IsType<RedirectToActionResult>(result).ActionName);
            Assert.Contains("one of IPRO's own addresses", controller.TempData["Error"] as string ?? "(no error for " + name + ")");
        }

        db.ChangeTracker.Clear();
        Assert.Equal(new[] { "www.smith589.example.test" }, await db.AgentDomains.Where(d => d.AgentUserId == agentId).Select(d => d.DomainName).ToListAsync());
        Assert.Equal("www.smith589.example.test", (await db.AgentWebsites.SingleAsync(w => w.AgentUserId == agentId)).CustomDomain);
    }

    [Fact]
    public async Task The_names_stay_refused_with_no_settings_at_all_and_names_that_only_look_alike_are_fine()
    {
        // The admin app's settings name none of iPro's addresses; a missing setting must not open the hole.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, _, _) = await AdviserAsync(db);
        var controller = Controller(db, agentId, new ConfigurationBuilder().Build(), new ScriptedAzure());

        foreach (var name in IproNames)
        {
            controller.TempData.Clear();
            await controller.AddDomain(name);
            Assert.Contains("one of IPRO's own addresses", controller.TempData["Error"] as string ?? "(no error for " + name + ")");
        }

        // A name that only contains one of iPro's is somebody else's.
        foreach (var name in new[] { "myiproadvisers.com", "iproadvisers.com.example.test", "notazurewebsites.net" })
        {
            controller.TempData.Clear();
            await controller.AddDomain(name);
            Assert.Null(controller.TempData["Error"]);
        }
        Assert.Equal(3, await db.AgentDomains.CountAsync(d => d.AgentUserId == agentId));
    }

    [Fact]
    public async Task A_name_the_settings_give_as_iPros_is_refused_with_its_www_twin_and_the_names_under_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, _, _) = await AdviserAsync(db);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:BaseUrl"] = "https://portal589.example.test",
            ["App:AdminDomain"] = "admin589.example.test",
            ["App:PlatformDomains"] = "web589.example.test,",
            ["App:AliasHosts"] = "www.brand589.example.test=/accountants, brand589.example.test=/accountants",
        }).Build();
        var controller = Controller(db, agentId, config, new ScriptedAzure());

        foreach (var name in new[]
                 {
                     "portal589.example.test", "www.portal589.example.test", "admin589.example.test", "web589.example.test",
                     "brand589.example.test", "www.brand589.example.test", "shop.brand589.example.test",
                 })
        {
            controller.TempData.Clear();
            await controller.AddDomain(name);
            Assert.Contains("one of IPRO's own addresses", controller.TempData["Error"] as string ?? "(no error for " + name + ")");
        }

        controller.TempData.Clear();
        await controller.AddDomain("www.next589.example.test");
        Assert.Null(controller.TempData["Error"]);
    }

    [Fact]
    public async Task The_Save_forms_custom_domain_box_refuses_an_iPro_name_too()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, templateId, _) = await AdviserAsync(db);
        var controller = Controller(db, agentId, ProductionConfig(), new ScriptedAzure());

        foreach (var name in new[] { "app.iproadvisers.com", "www.iproaccountants.com", "ipro-prod-web.azurewebsites.net" })
        {
            controller.TempData.Clear();
            await controller.Save(new AgentWebsite
            {
                TemplateId = templateId, SiteTitle = "Mine", TagLine = "A line", ThemeColor = "#224466",
                CustomDomain = name, CustomDomainDisplay = string.Empty,
            }, logo: null);
            Assert.Contains("one of IPRO's own addresses", controller.TempData["Error"] as string ?? "(no error for " + name + ")");
        }

        db.ChangeTracker.Clear();
        Assert.Equal(string.Empty, (await db.AgentWebsites.SingleAsync(w => w.AgentUserId == agentId)).CustomDomain);
        Assert.False(await db.AgentDomains.AnyAsync(d => d.AgentUserId == agentId));
    }

    [Fact]
    public async Task Removing_a_stored_row_that_names_iPros_address_deletes_the_row_and_sends_Azure_nothing()
    {
        // A row made before this fix: the adviser removes it, and the binding and certificate stay.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, _, websiteId) = await AdviserAsync(db);
        var main = StoredRow(agentId, websiteId, "app.iproadvisers.com", "app.iproadvisers.com", "app.iproadvisers.com");
        var brand = StoredRow(agentId, websiteId, "www.iproaccountants.com", "iproaccountants.com", "www.iproaccountants.com");
        brand.RootDnsStatus = AgentDomainStatus.Bound;
        brand.RootAzureBindingStatus = AgentDomainStatus.Bound;
        brand.RootSslStatus = AgentDomainStatus.Bound;
        db.AddRange(main, brand);
        await db.SaveChangesAsync();

        var azure = new RecordingAzure();
        var log = new RecordingLogger();
        var service = Service(azure, log, new ConfigurationBuilder().Build());
        var controller = Controller(db, agentId, ProductionConfig(), service);

        await controller.RemoveDomain(main.Id, "app.iproadvisers.com");
        await controller.RemoveDomain(brand.Id, "www.iproaccountants.com");

        Assert.Empty(azure.Requests);
        db.ChangeTracker.Clear();
        Assert.False(await db.AgentDomains.AnyAsync(d => d.AgentUserId == agentId));
        // The whole name and what follows it: "www.iproaccountants.com" must not stand in for the short address.
        foreach (var host in new[] { "app.iproadvisers.com", "www.iproaccountants.com", "iproaccountants.com" })
            Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("refused to remove " + host + ":", StringComparison.Ordinal));
    }

    // -------------------------------------------------------------- the automation --

    [Fact]
    public async Task The_automation_never_binds_or_deletes_an_iPro_name_and_still_does_an_advisers()
    {
        foreach (var config in new[] { ProductionConfig(), new ConfigurationBuilder().Build() })
        {
            var azure = new RecordingAzure();
            var log = new RecordingLogger();
            var service = Service(azure, log, config);

            foreach (var name in IproNames.Where(n => !n.Contains('/')).Concat(new[] { "www.iproadvisers-com", "app-iproadvisers.com" }))
            {
                Assert.False((await service.RemoveDomainAsync(name)).Success);
                Assert.False((await service.EnsureDomainAsync(name)).Success);
                Assert.False((await service.EnsureRootDomainAsync(name)).Success);
            }
            Assert.Empty(azure.Requests);
            Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("app.iproadvisers.com", StringComparison.Ordinal));

            // An adviser's own name: the binding and its certificate go, as before.
            Assert.True((await service.RemoveDomainAsync("www.smith589.example.test")).Success);
            Assert.Contains(azure.Requests, r => r.Method == HttpMethod.Delete && r.Url.Contains("/hostNameBindings/www.smith589.example.test?"));
            Assert.Contains(azure.Requests, r => r.Method == HttpMethod.Delete && r.Url.Contains("/certificates/managed-www-smith589-example-test?"));
        }
    }

    [Fact]
    public void The_apps_build_the_automation_with_their_settings()
    {
        // Both Program.cs files register the service by type; the container must hand it the settings, or a
        // name only the settings know (a brand name iPro adds later) would be unguarded.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:AliasHosts"] = "www.brand589.example.test=/accountants",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddSingleton<IHttpClientFactory>(new RecordingAzure());
        services.Configure<AzureDomainAutomationOptions>(o =>
        {
            o.Enabled = true; o.TenantId = "t"; o.ClientId = "c"; o.ClientSecret = "s"; o.SubscriptionId = "sub"; o.ResourceGroup = "rg"; o.WebAppName = "ipro-prod-web";
        });
        services.AddScoped<IAzureDomainAutomationService, AzureDomainAutomationService>();
        services.AddScoped<IDomainCheckService, DomainCheckService>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var automation = scope.ServiceProvider.GetRequiredService<IAzureDomainAutomationService>();
        Assert.Contains("one of IPRO's own addresses", automation.RemoveDomainAsync("brand589.example.test").GetAwaiter().GetResult().Message);

        var check = scope.ServiceProvider.GetRequiredService<IDomainCheckService>();
        var row = new AgentDomain { DomainName = "www.brand589.example.test", RootDomain = "brand589.example.test", WwwDomain = "www.brand589.example.test" };
        Assert.False(check.CheckAsync(row).GetAwaiter().GetResult());
        Assert.Contains("one of IPRO's own addresses", row.LastError);
    }

    [Fact]
    public async Task The_domain_check_refuses_an_iPro_name_before_any_lookup_probe_or_binding()
    {
        foreach (var configured in new[] { true, false })
        {
            var azure = new ScriptedAzure { IsConfigured = configured };
            var log = new RecordingLogger();
            var check = new DomainCheckService(new NoHttp(), azure, log.For<DomainCheckService>());

            foreach (var (name, root) in new[]
                     {
                         ("app.iproadvisers.com", "app.iproadvisers.com"), ("www.iproadvisers.com", "iproadvisers.com"),
                         ("www.ipromortgages.com", "ipromortgages.com"), ("www.crm.to", "crm.to"),
                         ("www.app-iproadvisers.com", "app-iproadvisers.com"),   // its short address names app.iproadvisers.com's certificate
                     })
            {
                var row = new AgentDomain
                {
                    DomainName = name, RootDomain = root, WwwDomain = name, DnsTarget = "ipro-prod-web.azurewebsites.net",
                    // As a row the old probe had already called bound, short address and all.
                    DnsStatus = AgentDomainStatus.Bound, AzureBindingStatus = AgentDomainStatus.Bound, SslStatus = AgentDomainStatus.Bound,
                    RootDnsStatus = AgentDomainStatus.Bound, RootAzureBindingStatus = AgentDomainStatus.Bound, RootSslStatus = AgentDomainStatus.BindingPending,
                    RootBoundAt = DateTime.UtcNow.AddDays(-1),
                };
                Assert.False(await check.CheckAsync(row));
                Assert.NotEqual(AgentDomainStatus.Bound, row.AzureBindingStatus);
                Assert.Contains("one of IPRO's own addresses", row.LastError);
                Assert.Contains("one of IPRO's own addresses", DomainErrorTranslator.ToAgentMessage(row.LastError));
                Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains(name, StringComparison.Ordinal));
                // Nothing of the short address is waiting for a certificate: no alert about iPro's own name.
                Assert.Equal((false, (string?)null), IPRO.Scheduler.DomainAutomationJob.CertificateWait(row, DateTime.UtcNow));
            }
            Assert.Empty(azure.Calls);
        }
    }

    [Fact]
    public void Only_the_automation_service_speaks_to_Azure_about_bindings_and_certificates()
    {
        // The guard lives in AzureDomainAutomationService; a second path to the same API would not have it.
        var root = SolutionRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => !f.EndsWith("AzureDomainAutomationService.cs", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("management.azure.com", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        Assert.Empty(offenders);
    }

    // ------------------------------------------------- the review: shape and leftovers --

    // An adviser's own name with something that is no part of a host name, or written in a form a resolver
    // would change: each is refused before it is stored, and the automation sends nothing for it. Built on an
    // ordinary name, so that "refused as iPro's" cannot stand in for "refused as no host name".
    private const string Own = "www.smith589.example.test";

    private static readonly string[] MalformedNames =
    {
        Own + (char)0x5C, Own + "%2f", Own + (char)0x200B, (char)0xFEFF + Own, Own + (char)0x00AD, Own + (char)0x07,
        "www.smith" + (char)0x0A + "589.example.test", "www.smith589" + (char)0x09 + ".example.test",
        "-" + Own, "www..smith589.example.test", "www.smith_589.example.test",
        // Forms IDNA reads as another name: full-width letters, an ideographic full stop, another script.
        new string((char)0xFF57, 3) + ".smith589.example.test", "www" + (char)0x3002 + "smith589.example.test",
        "www.caf" + (char)0xE9 + "589.example.test",
    };

    [Fact]
    public async Task A_name_that_is_no_host_name_is_never_stored_or_sent_and_lookalikes_are_refused_at_once()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, _, _) = await AdviserAsync(db);
        var controller = Controller(db, agentId, ProductionConfig(), new ScriptedAzure());

        foreach (var name in MalformedNames.Append("intranet"))
        {
            controller.TempData.Clear();
            await controller.AddDomain(name);
            Assert.Equal("Enter a valid domain name.", controller.TempData["Error"] as string ?? "(no error for " + PlatformHostNames.Printable(name) + ")");
        }
        foreach (var name in new[] { "www.app-iproadvisers.com", "best-iproaccountants.com", "www.my-crm.to" })
        {
            controller.TempData.Clear();
            await controller.AddDomain(name);
            Assert.Contains("too close to one of IPRO's own addresses", controller.TempData["Error"] as string ?? "(no error for " + name + ")");
        }
        Assert.False(await db.AgentDomains.AnyAsync(d => d.AgentUserId == agentId));

        var azure = new RecordingAzure();
        var log = new RecordingLogger();
        var service = Service(azure, log, new ConfigurationBuilder().Build());
        foreach (var name in MalformedNames)
        {
            Assert.Contains("not a valid domain name", (await service.RemoveDomainAsync(name)).Message);
            Assert.Contains("not a valid domain name", (await service.EnsureDomainAsync(name)).Message);
            Assert.Contains("not a valid domain name", (await service.EnsureRootDomainAsync(name)).Message);
        }
        Assert.Empty(azure.Requests);
        // A typed line break or bell never reaches the log as itself.
        Assert.DoesNotContain(log.Lines, l => l.Text.Contains((char)0x0A) || l.Text.Contains((char)0x07));

        // A domain in another script is entered as its xn-- form, stored as that and sent as that.
        var ascii = new System.Globalization.IdnMapping().GetAscii("www.caf" + (char)0xE9 + "589.example.test");
        controller.TempData.Clear();
        await controller.AddDomain(ascii);
        Assert.Null(controller.TempData["Error"]);
        Assert.True(await db.AgentDomains.AnyAsync(d => d.AgentUserId == agentId && d.DomainName == ascii));
        Assert.True((await service.RemoveDomainAsync(ascii)).Success);
        Assert.Contains(azure.Requests, r => r.Method == HttpMethod.Delete && r.Url.Contains("/hostNameBindings/" + ascii + "?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_twin_of_another_advisers_domain_spelled_with_lookalike_characters_cannot_touch_it()
    {
        // The rechecks' case: a name stored in one form but read by Azure as another adviser's domain would pass
        // "already in use", and removing it would delete that adviser's binding and certificate.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (victimId, _, victimSite) = await AdviserAsync(db);
        db.Add(StoredRow(victimId, victimSite, "www.victim589.example.test", "victim589.example.test", "www.victim589.example.test"));
        await db.SaveChangesAsync();
        var (attackerId, _, attackerSite) = await AdviserAsync(db);

        var controller = Controller(db, attackerId, ProductionConfig(), new ScriptedAzure());
        foreach (var twin in new[]
                 {
                     new string((char)0xFF57, 3) + ".victim589.example.test", "www" + (char)0x3002 + "victim589.example.test",
                     "www.victim589.example.test" + (char)0x200B,
                     // A port cut off after the trimming used to leave the dot or the TAB in front of it.
                     "www.victim589.example.test.:443", "www.victim589.example.test" + (char)0x09 + ":8080", "victim589.example.test.:443",
                 })
        {
            controller.TempData.Clear();
            await controller.AddDomain(twin);
            Assert.NotNull(controller.TempData["Error"]);
        }
        Assert.False(await db.AgentDomains.AnyAsync(d => d.AgentUserId == attackerId));
        Assert.Equal("www.example.com", WebsiteController.NormalizeDomain("example.com.:443"));
        Assert.Equal("www.example.com", WebsiteController.NormalizeDomain("www.example.com" + (char)0x09 + ":8080"));

        // Twins stored before this fix (a full-width one the database's own collation already counts as the
        // same name, so it cannot sit beside the real row): removing them, or checking them, sends nothing.
        var stored = new[] { "www.victim589.example.test.", "victim589.example.test." }
            .Select(name => StoredRow(attackerId, attackerSite, name, name, name)).ToList();
        db.AddRange(stored);
        await db.SaveChangesAsync();
        var azure = new RecordingAzure();
        var service = Service(azure, new RecordingLogger(), ProductionConfig());
        var scripted = new ScriptedAzure();
        foreach (var row in stored)
        {
            Assert.False(await new DomainCheckService(new NoHttp(), scripted, NullLogger<DomainCheckService>.Instance).CheckAsync(row));
            Assert.Contains("not a valid domain name", row.LastError);
            await Controller(db, attackerId, ProductionConfig(), service).RemoveDomain(row.Id, row.DomainName);
        }

        Assert.Empty(azure.Requests);
        Assert.Empty(scripted.Calls);
        db.ChangeTracker.Clear();
        Assert.False(await db.AgentDomains.AnyAsync(d => d.AgentUserId == attackerId));
        Assert.True(await db.AgentDomains.AnyAsync(d => d.AgentUserId == victimId && d.DomainName == "www.victim589.example.test"));
    }

    [Fact]
    public async Task With_automation_switched_off_the_refusal_is_still_written_down()
    {
        var azure = new RecordingAzure();
        var log = new RecordingLogger();
        var service = Service(azure, log, new ConfigurationBuilder().Build(), enabled: false);

        Assert.False((await service.RemoveDomainAsync("app.iproadvisers.com")).Success);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("refused to remove app.iproadvisers.com:", StringComparison.Ordinal));
        Assert.Contains("disabled", (await service.RemoveDomainAsync("www.smith589.example.test")).Message);
        Assert.Empty(azure.Requests);
    }

    [Fact]
    public async Task The_adviser_reads_the_refusal_as_written_whatever_words_the_name_holds()
    {
        // DomainErrorTranslator looks for words such as Conflict and NotFound; a refused name may hold them.
        var service = Service(new RecordingAzure(), new RecordingLogger(), new ConfigurationBuilder().Build());
        var refusal = (await service.RemoveDomainAsync("conflict-notfound.iproadvisers.com")).Message;
        Assert.Contains("one of IPRO's own addresses", refusal);
        Assert.Equal(refusal, DomainErrorTranslator.ToAgentMessage(refusal));

        var invalid = (await service.RemoveDomainAsync("badrequest" + (char)0x5C + ".example.test")).Message;
        Assert.Equal(invalid, DomainErrorTranslator.ToAgentMessage(invalid));
    }

    [Fact]
    public void The_built_in_list_covers_every_name_production_s_settings_give_as_iPros()
    {
        // The admin app has none of these settings: every name the web app's settings call iPro's must be on
        // the built-in list too, or the admin app would bind or delete it.
        var config = ProductionConfig();
        var names = config["App:PlatformDomains"]!.Split(',')
            .Concat(config["App:AliasHosts"]!.Split(',').Select(entry => entry.Split('=')[0]))
            .Append(new Uri(config["App:BaseUrl"]!).Host)
            .Append(config["App:AdminDomain"]!)
            .Append(config["App:WebsiteDnsTarget"]!)
            .Append("anyone." + config["App:TemporarySiteRootDomain"]);
        foreach (var name in names)
            Assert.True(PlatformHostNames.IsPlatformHost(name), name + " is iPro's in production's settings but not on the built-in list");
    }

    [Fact]
    public void Both_apps_register_the_automation_by_type_so_the_container_gives_it_the_settings()
    {
        // The_apps_build_the_automation_with_their_settings shows what the container does with these lines; this
        // pins that both apps still have them (a factory registration would have to pass the settings itself).
        var root = SolutionRoot();
        foreach (var program in new[] { @"src\IPRO.Web\Program.cs", @"src\IPRO.Admin\Program.cs" })
        {
            var source = File.ReadAllText(Path.Combine(root, program));
            Assert.Contains("builder.Services.AddScoped<IAzureDomainAutomationService, AzureDomainAutomationService>();", source);
            Assert.Contains("builder.Services.AddScoped<IDomainCheckService, DomainCheckService>();", source);
        }
    }

    [Fact]
    public void The_guides_say_which_names_are_refused_and_where_a_new_domain_of_iPros_goes()
    {
        var root = SolutionRoot();
        string Read(string relative) => File.ReadAllText(Path.Combine(root, relative)).Replace("\r\n", "\n");

        Assert.Contains("IPRO's own addresses cannot be added", Read(@"DOCS\05_DOMAINS_AND_LEADS.md"));
        var trouble = Read(@"DOCS\09_TROUBLESHOOTING.md");
        Assert.Contains("## A Domain Is \"One of IPRO's Own Addresses\"", trouble);
        Assert.Contains("`Azure domain automation refused to remove <name>`", trouble);
        Assert.Contains("goes on `OwnDomains` in\n`src/IPRO.Utility/PlatformHostNames.cs`", Read(@"DOCS\DOMAIN_SWITCH_RUNBOOK.md"));
    }

    // ------------------------------------------------------------------- plumbing --

    private static IConfiguration ProductionConfig() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        // As production has them (appsettings.json, App Service settings; DOCS/DOMAIN_SWITCH_RUNBOOK.md).
        ["App:BaseUrl"] = "https://app.iproadvisers.com",
        ["App:AdminDomain"] = "admin.iproadvisers.com",
        ["App:TemporarySiteRootDomain"] = "247advisers.com",
        ["App:WebsiteDnsTarget"] = "ipro-prod-web.azurewebsites.net",
        ["App:PlatformDomains"] = "ipro-prod-web.azurewebsites.net,app.iproadvisers.com,www.iproadvisers.com,iproadvisers.com",
        ["App:AliasHosts"] = "www.iproadvisers.com=/,iproadvisers.com=/,www.ipromortgages.com=/mortgage,ipromortgages.com=/mortgage,www.iproaccountants.com=/accountants,iproaccountants.com=/accountants,www.crm.to,crm.to",
    }).Build();

    private static AgentDomain StoredRow(int agentId, int websiteId, string name, string root, string www) => new()
    {
        AgentUserId = agentId, AgentWebsiteId = websiteId, DomainName = name, RootDomain = root, WwwDomain = www,
        DnsTarget = "ipro-prod-web.azurewebsites.net",
        DnsStatus = AgentDomainStatus.Bound, AzureBindingStatus = AgentDomainStatus.Bound, SslStatus = AgentDomainStatus.Bound,
    };

    // An adviser whose package has a website and extra domains (no limit), with a saved website.
    private static async Task<(int AgentId, int TemplateId, int WebsiteId)> AdviserAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T589-{Guid.NewGuid():N}")[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        var template = new WebsiteTemplate { Name = "Modern", TemplateKey = ($"t589-{Guid.NewGuid():N}")[..16], BusinessType = "All", IsActive = true };
        db.AddRange(rule, template);
        await db.SaveChangesAsync();
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.InstantWebsite, FeatureName = "Website", IsIncluded = true });
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.MultiDomainSupport, FeatureName = "Domains", IsIncluded = true });
        var agent = new AgentUser
        {
            UserName = ($"t589-{Guid.NewGuid():N}")[..20], Email = ($"t589-{Guid.NewGuid():N}")[..14] + "@example.test",
            FirstName = "Sam", LastName = "Smith", CompanyName = "Smith Advisory",
            DomainName = ($"t589-{Guid.NewGuid():N}")[..24], Country = "Canada", Province = "Ontario",
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

    private static AzureDomainAutomationService Service(RecordingAzure azure, RecordingLogger log, IConfiguration config, bool enabled = true) => new(
        azure,
        Options.Create(new AzureDomainAutomationOptions
        {
            Enabled = enabled,
            TenantId = "tenant", ClientId = "client", ClientSecret = "secret",
            SubscriptionId = "sub", ResourceGroup = "rg",
            WebAppName = "ipro-prod-web",
            AppServicePlanResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/serverfarms/plan",
        }),
        log.For<AzureDomainAutomationService>(),
        config);

    private static WebsiteController Controller(IPRODbContext db, int agentId, IConfiguration config, IAzureDomainAutomationService azure)
    {
        var controller = new WebsiteController(
            new WebsiteService(new UnitOfWork(db)), new NullBlob(), new PackageEntitlementService(new UnitOfWork(db), db),
            new AgentService(new UnitOfWork(db), new PasswordHasher<AgentUser>()), config, db,
            new ScriptedCheck(), azure, NullLogger<WebsiteController>.Instance);
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

    // Stands in for login.microsoftonline.com and management.azure.com: every request recorded, each
    // answered the way Azure answers a request it accepts.
    private sealed class RecordingAzure : HttpMessageHandler, IHttpClientFactory
    {
        public List<(HttpMethod Method, string Url)> Requests { get; } = new();

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add((request.Method, url));
            var body = url.Contains("login.microsoftonline.com") ? "{\"access_token\":\"token\",\"expires_in\":3600}" : "{\"properties\":{}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class ScriptedAzure : IAzureDomainAutomationService
    {
        public bool IsConfigured { get; set; } = true;
        public List<string> Calls { get; } = new();

        public Task<AzureDomainAutomationResult> EnsureDomainAsync(string hostName, CancellationToken cancellationToken = default) =>
            Record("ensure " + hostName);

        public Task<AzureDomainAutomationResult> EnsureRootDomainAsync(string hostName, CancellationToken cancellationToken = default) =>
            Record("ensure root " + hostName);

        public Task<AzureDomainAutomationResult> RemoveDomainAsync(string hostName, CancellationToken cancellationToken = default) =>
            Record("remove " + hostName);

        private Task<AzureDomainAutomationResult> Record(string call)
        {
            Calls.Add(call);
            return Task.FromResult(new AzureDomainAutomationResult { Success = true, BindingCreated = true, CertificateCreated = true, SslBound = true });
        }
    }

    private sealed class ScriptedCheck : IDomainCheckService
    {
        public Task<bool> CheckAsync(AgentDomain domain, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("an iPro name is refused before anything is fetched");
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
