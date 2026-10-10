using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using IPRO.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 506 and 434, built together (the owner, 2026-10-10: "build 506 and 434 together").
//
// 506: a customer's own domain whose DNS lists which companies may issue its certificate (CAA
// records) and leaves DigiCert out never gets its certificate, and nothing told anyone why. Found on
// our own domains on 2026-09-20. The domain check now reads the records while a certificate is
// still wanted, and My Website shows the one record to add.
//
// 434: a website lead could only be dismissed, never removed -- a stranger's name, email and message
// stayed in the account for ever -- and "All" listed dismissed leads, so dismissing looked like a
// delete that had failed.
public class CaaAndLeadDelete506Tests : IDisposable
{
    public void Dispose() => CaaCheck.LookupHook = null;

    private static string Answer(params string[] records) =>
        "{\"Status\":0,\"Answer\":[" + string.Join(",", records.Select(r => "{\"name\":\"x.\",\"type\":257,\"TTL\":300,\"data\":" + System.Text.Json.JsonSerializer.Serialize(r) + "}")) + "]}";
    private const string NoRecords = "{\"Status\":0,\"Authority\":[{\"name\":\"example.test.\",\"type\":6}]}";

    // ----------------------------------------------------------------------------- 506 --

    [Fact]
    public void A_list_that_leaves_DigiCert_out_blocks_and_one_that_names_it_does_not()
    {
        // What a cPanel host publishes for its own free certificates.
        Assert.True(CaaCheck.Blocks(new[] { "0 issue \"sectigo.com\"", "0 issue \"letsencrypt.org\"", "0 issuewild \"sectigo.com\"" }));
        Assert.False(CaaCheck.Blocks(new[] { "0 issue \"letsencrypt.org\"", "0 issue \"digicert.com\"" }));
        Assert.False(CaaCheck.Blocks(new[] { "0 issue \"DigiCert.com; cansignhttpexchanges=yes\"" }));
        Assert.True(CaaCheck.Blocks(new[] { "0 issue \";\"" }));                       // nobody may issue
        // Only "issue" restricts an ordinary certificate.
        Assert.False(CaaCheck.Blocks(new[] { "0 issuewild \"letsencrypt.org\"", "0 iodef \"mailto:dns@example.test\"" }));
        Assert.False(CaaCheck.Blocks(Array.Empty<string>()));
        Assert.Equal("0 issue \"digicert.com\"", CaaCheck.RecordToAdd);

        Assert.Equal(new[] { "0 issue \"letsencrypt.org\"" }, CaaCheck.ParseAnswer(Answer("0 issue \"letsencrypt.org\"")));
        // A CNAME in the answer is not a CAA record; a broken or empty answer is no records.
        Assert.Empty(CaaCheck.ParseAnswer("{\"Answer\":[{\"name\":\"www.x.\",\"type\":5,\"data\":\"ipro-prod-web.azurewebsites.net.\"}]}"));
        Assert.Empty(CaaCheck.ParseAnswer(NoRecords));
        Assert.Empty(CaaCheck.ParseAnswer("not json"));
        Assert.Empty(CaaCheck.ParseAnswer(null));

        Assert.Equal(new[] { "www.shop.example.test", "shop.example.test", "example.test" }, CaaCheck.NamesToAsk("WWW.Shop.Example.test."));
    }

    [Fact]
    public async Task The_nearest_name_that_has_records_decides_and_a_failed_lookup_decides_nothing()
    {
        var asked = new List<string>();
        Func<string, CancellationToken, Task<string?>> dns = (name, _) =>
        {
            asked.Add(name);
            return Task.FromResult<string?>(name == "bakery506.example.test" ? Answer("0 issue \"letsencrypt.org\"") : NoRecords);
        };
        // www has none of its own, so the registered domain's list applies.
        Assert.Equal("bakery506.example.test", await CaaCheck.FindBlockingNameAsync("www.bakery506.example.test", dns));
        Assert.Equal(new[] { "www.bakery506.example.test", "bakery506.example.test" }, asked);

        // A list on the www name itself wins over the parent's.
        Assert.Equal(string.Empty, await CaaCheck.FindBlockingNameAsync("www.bakery506.example.test",
            (name, _) => Task.FromResult<string?>(name.StartsWith("www.") ? Answer("0 issue \"digicert.com\"") : Answer("0 issue \"letsencrypt.org\""))));
        // No records anywhere: any company may issue.
        Assert.Equal(string.Empty, await CaaCheck.FindBlockingNameAsync("www.open506.example.test", (_, _) => Task.FromResult<string?>(NoRecords)));
        // DNS did not answer: unknown, not "fine".
        Assert.Null(await CaaCheck.FindBlockingNameAsync("www.slow506.example.test", (_, _) => Task.FromResult<string?>(null)));
        Assert.Null(await CaaCheck.FindBlockingNameAsync("www.slow506.example.test", (_, _) => throw new HttpRequestException("timeout")));
    }

    [Fact]
    public async Task The_domain_check_records_it_while_a_certificate_is_wanted_and_clears_it_after()
    {
        var blockedAt = "caa506.example.test";
        var failing = false;
        CaaCheck.LookupHook = (name, _) => Task.FromResult<string?>(
            failing ? null : name == blockedAt ? Answer("0 issue \"sectigo.com\"") : NoRecords);
        var service = new DomainCheckService(new NoHttp(), new NoAzure(), NullLogger<DomainCheckService>.Instance);
        var domain = new AgentDomain
        {
            DomainName = "www.caa506.example.test", WwwDomain = "www.caa506.example.test", RootDomain = "caa506.example.test",
            DnsStatus = AgentDomainStatus.Bound, AzureBindingStatus = AgentDomainStatus.Bound, SslStatus = AgentDomainStatus.BindingPending,
        };

        await service.CheckCaaAsync(domain);
        Assert.Equal("caa506.example.test", domain.CaaBlockingName);

        // DNS not answering keeps the warning rather than clearing it.
        failing = true;
        await service.CheckCaaAsync(domain);
        Assert.Equal("caa506.example.test", domain.CaaBlockingName);

        // The record is added: the warning goes.
        failing = false; blockedAt = "nothing";
        await service.CheckCaaAsync(domain);
        Assert.Equal(string.Empty, domain.CaaBlockingName);

        // Once the certificate is in place nothing is asked at all.
        domain.CaaBlockingName = "stale";
        domain.SslStatus = AgentDomainStatus.Bound;
        CaaCheck.LookupHook = (_, _) => throw new InvalidOperationException("no lookup is needed when every certificate is in place");
        await service.CheckCaaAsync(domain);
        Assert.Equal(string.Empty, domain.CaaBlockingName);

        // A short address pointed straight at the platform needs its own certificate, so it is asked for too.
        var asked = new List<string>();
        CaaCheck.LookupHook = (name, _) => { asked.Add(name); return Task.FromResult<string?>(NoRecords); };
        domain.RootDnsStatus = AgentDomainStatus.Bound; domain.RootSslStatus = AgentDomainStatus.BindingPending;
        await service.CheckCaaAsync(domain);
        // The www name is secured, so only the short address is asked about (it and the name above it).
        Assert.Equal(new[] { "caa506.example.test", "example.test" }, asked);
    }

    [Fact]
    public void The_adviser_is_shown_the_record_and_the_operator_is_told_the_cause()
    {
        var page = Read(@"src\IPRO.Web\Views\Website\Index.cshtml");
        Assert.Contains("@if (!sslReady && !string.IsNullOrEmpty(primaryDomain?.CaaBlockingName))", page);
        Assert.Contains("<td>CAA</td>", page);
        Assert.Contains("<code>@IPRO.Utility.CaaCheck.RecordToAdd</code>", page);
        Assert.Contains("Keep the CAA records that are already there", page);

        var service = Read(@"src\IPRO.Utility\DomainCheckService.cs");
        Assert.Contains("await CheckCaaAsync(domain, cancellationToken);", service);
        Assert.Contains("if (found == null) return;", service);
        Assert.Contains("https://dns.google/resolve?name={Uri.EscapeDataString(name)}&type=CAA", service);
        Assert.Contains("ADD COLUMN `CaaBlockingName` varchar(255)", Read(@"src\IPRO.DataAccess\StartupSchemaRepair.cs"));
        Assert.Contains("<strong>Likely cause: CAA.</strong>", Read(@"src\IPRO.Scheduler\DomainAutomationJob.cs"));
        Assert.Contains("### The certificate never arrives: a CAA record", Read(@"DOCS\05_DOMAINS_AND_LEADS.md"));
    }

    // ----------------------------------------------------------------------------- 434 --

    [Fact]
    public async Task A_lead_is_deleted_with_its_answers_and_only_by_its_own_adviser()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (mine, site) = await SeedAgentAsync(db);
        var (theirs, theirSite) = await SeedAgentAsync(db);
        var client = new Client { AgentUserId = mine, FirstName = "Kept", LastName = "Contact", Email = "kept@example.test" };
        db.Add(client);
        await db.SaveChangesAsync();
        var junk = Lead(mine, site, "junk@example.test"); junk.ClientId = client.Id;
        var keep = Lead(mine, site, "keep@example.test");
        var other = Lead(theirs, theirSite, "other@example.test");
        db.AddRange(junk, keep, other);
        await db.SaveChangesAsync();
        db.AddRange(
            new WebsiteFormSubmissionAnswer { WebsiteLeadId = junk.Id, FieldLabel = "Budget", FieldType = "text", Value = "private" },
            new WebsiteFormSubmissionAnswer { WebsiteLeadId = other.Id, FieldLabel = "Budget", FieldType = "text", Value = "theirs" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Another adviser's lead cannot be deleted by id.
        Assert.IsType<NotFoundResult>(await NewController(db, mine).Delete(other.Id));
        Assert.IsType<LocalRedirectResult>(await NewController(db, mine).Delete(junk.Id, "/WebsiteLeads?status=dismissed"));
        db.ChangeTracker.Clear();

        Assert.False(await db.WebsiteLeads.AnyAsync(x => x.Id == junk.Id));
        Assert.False(await db.WebsiteFormSubmissionAnswers.AnyAsync(a => a.WebsiteLeadId == junk.Id));   // the answers go with it
        Assert.True(await db.Clients.AnyAsync(c => c.Id == client.Id));                                  // the CRM contact stays
        Assert.True(await db.WebsiteLeads.AnyAsync(x => x.Id == keep.Id));
        Assert.True(await db.WebsiteLeads.AnyAsync(x => x.Id == other.Id));
        Assert.True(await db.WebsiteFormSubmissionAnswers.AnyAsync(a => a.WebsiteLeadId == other.Id));

        // The ticked ones: mine go, an id that is not mine is ignored.
        var controller = NewController(db, mine);
        Assert.IsType<LocalRedirectResult>(await controller.BulkDelete(new[] { keep.Id, other.Id }));
        Assert.Equal("1 lead(s) deleted.", controller.TempData["Success"]);
        db.ChangeTracker.Clear();
        Assert.False(await db.WebsiteLeads.AnyAsync(x => x.AgentUserId == mine));
        Assert.True(await db.WebsiteLeads.AnyAsync(x => x.Id == other.Id));

        var none = NewController(db, mine);
        await none.BulkDelete(Array.Empty<int>());
        Assert.Equal("Select at least one lead first.", none.TempData["Error"]);
    }

    [Fact]
    public async Task All_no_longer_lists_dismissed_leads_and_Dismissed_still_does()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agent, site) = await SeedAgentAsync(db);
        var open = Lead(agent, site, "open@example.test");
        var contacted = Lead(agent, site, "contacted@example.test"); contacted.Status = WebsiteLeadStatuses.Contacted;
        var dismissed = Lead(agent, site, "dismissed@example.test"); dismissed.Status = WebsiteLeadStatuses.Dismissed;
        db.AddRange(open, contacted, dismissed);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var all = Assert.IsAssignableFrom<IEnumerable<WebsiteLead>>(Assert.IsType<ViewResult>(await NewController(db, agent).Index("all")).Model);
        Assert.Equal(new[] { "contacted@example.test", "open@example.test" }, all.Select(x => x.Email).OrderBy(x => x));
        var hidden = Assert.IsAssignableFrom<IEnumerable<WebsiteLead>>(Assert.IsType<ViewResult>(await NewController(db, agent).Index("dismissed")).Model);
        Assert.Equal(new[] { "dismissed@example.test" }, hidden.Select(x => x.Email));

        var view = Read(@"src\IPRO.Web\Views\WebsiteLeads\Index.cshtml");
        Assert.Contains(">All open</a>", view);
        Assert.Contains("<form asp-action=\"Delete\" method=\"post\">", view);
        Assert.Contains("formaction=\"/portal/WebsiteLeads/BulkDelete\" class=\"btn btn-sm btn-outline-danger js-confirm-submit\"", view);
        Assert.Contains("<i class=\"fas fa-eye-slash me-1\"></i>Dismiss</button>", view);
        Assert.DoesNotContain("fa-archive", view);                 // the glyph that read as delete
        Assert.DoesNotContain("SendGrid", view);
        var source = Read(@"src\IPRO.Web\Controllers\WebsiteLeadsController.cs");
        var delete = source.Substring(source.IndexOf("public async Task<IActionResult> Delete(", StringComparison.Ordinal) - 60, 60);
        Assert.Contains("[HttpPost, ValidateAntiForgeryToken]", delete);
        Assert.Contains("Delete a lead for good", Read(@"DOCS\05_DOMAINS_AND_LEADS.md"));
    }

    // -------------------------------------------------------------------------- helpers --

    private static WebsiteLead Lead(int agentId, int siteId, string email) => new()
    {
        AgentUserId = agentId, AgentWebsiteId = siteId, FirstName = "Test", LastName = "Lead", Email = email, Message = "hello",
    };

    private static async Task<(int AgentId, int SiteId)> SeedAgentAsync(IPRODbContext db)
    {
        var agent = new AgentUser
        {
            UserName = $"ld-{Guid.NewGuid():N}"[..20], Email = $"ld-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Lead", LastName = "Owner", DomainName = $"ld-{Guid.NewGuid():N}"[..24]
        };
        db.Add(agent);
        var template = new WebsiteTemplate { TemplateKey = $"tk-{Guid.NewGuid():N}"[..16], Name = "T", BusinessType = "All" };
        db.Add(template);
        await db.SaveChangesAsync();
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, IsPublished = true };
        db.Add(website);
        await db.SaveChangesAsync();
        return (agent.Id, website.Id);
    }

    private static WebsiteLeadsController NewController(IPRODbContext db, int agentId)
    {
        var controller = new WebsiteLeadsController(db);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NullTempData());
        return controller;
    }

    private sealed class NullTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("the test answers DNS itself");
    }

    private sealed class NoAzure : IAzureDomainAutomationService
    {
        public bool IsConfigured => false;
        public Task<AzureDomainAutomationResult> EnsureDomainAsync(string hostName, CancellationToken cancellationToken = default) => Task.FromResult(new AzureDomainAutomationResult());
        public Task<AzureDomainAutomationResult> EnsureRootDomainAsync(string hostName, CancellationToken cancellationToken = default) => Task.FromResult(new AzureDomainAutomationResult());
        public Task<AzureDomainAutomationResult> RemoveDomainAsync(string hostName, CancellationToken cancellationToken = default) => Task.FromResult(new AzureDomainAutomationResult());
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative)).Replace("\r\n", "\n");
    }
}
