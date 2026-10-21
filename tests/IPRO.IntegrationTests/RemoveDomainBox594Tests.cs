using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Utility;
using IPRO.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 594 (2026-10-21), asked by the owner. My Website's Remove box said "Type www.411trades.com to confirm" in red
// above an empty box with no label; he did not see what to type, and the browser only said "Please fill out this
// field". The box now says it in a label tied to the box, shows the domain greyed inside it, says in plain words
// what removal does, puts the cursor in the box, and lets Remove be pressed only once the domain is typed as
// shown. The server still refuses anything else (case and spaces at either end do not matter, as before).
[Collection(ConfigHazardTests.AzureStatics)]
public class RemoveDomainBox594Tests
{
    [Fact]
    public void The_box_says_what_to_type_in_a_label_tied_to_the_box()
    {
        var box = RemoveBox();

        // The dialog is named by its title, and the label sits right above the box it belongs to.
        Assert.Contains("<div class=\"modal fade\" id=\"removeDomainModal-@domain.Id\" tabindex=\"-1\" aria-labelledby=\"removeDomainTitle-@domain.Id\" aria-hidden=\"true\">", box);
        Assert.Contains("<h5 class=\"modal-title\" id=\"removeDomainTitle-@domain.Id\">Remove @domain.DomainName?</h5>", box);
        Assert.Contains("<label for=\"removeDomainInput-@domain.Id\" class=\"form-label fw-semibold\">Type <strong>@domain.DomainName</strong> here to confirm</label>", box);
        var input = Regex.Match(box, "<input type=\"text\" id=\"removeDomainInput-@domain\\.Id\"[^>]*/>");
        Assert.True(input.Success, "the box must carry the id its label points at");
        Assert.True(box.IndexOf("<label for=\"removeDomainInput-", StringComparison.Ordinal) < input.Index, "the label goes above the box");

        // The domain greyed inside the box; what is typed is still what the server checks.
        Assert.Contains("name=\"confirmDomainName\"", input.Value);
        Assert.Contains("placeholder=\"@domain.DomainName\"", input.Value);
        Assert.Contains("autocomplete=\"off\"", input.Value);
        Assert.Contains("aria-describedby=\"removeDomainWhat-@domain.Id removeDomainMismatch-@domain.Id\"", input.Value);
    }

    [Fact]
    public void The_warning_is_plain_text_that_says_what_removal_does()
    {
        var box = RemoveBox();
        Assert.DoesNotContain("text-danger small", box);
        Assert.DoesNotContain("Type <strong>@domain.DomainName</strong> to confirm.", box);
        Assert.Contains("<p id=\"removeDomainWhat-@domain.Id\"><strong>This cannot be undone.</strong> Your website will stop opening at @domain.DomainName, and using it again means adding it and setting it up from the start.</p>", box);

        // The message for a mismatch is read out where it appears (it is empty until then).
        Assert.Contains("<div id=\"removeDomainMismatch-@domain.Id\" class=\"form-text text-danger\" aria-live=\"polite\" data-remove-domain-mismatch></div>", box);
    }

    [Fact]
    public void Remove_starts_disabled_and_Escape_and_Cancel_still_close_the_box()
    {
        var box = RemoveBox();
        Assert.Contains("<form method=\"post\" action=\"/portal/Website/RemoveDomain/@domain.Id\" data-remove-domain=\"@domain.DomainName\">", box);
        Assert.Contains("<button type=\"submit\" class=\"btn btn-danger\" disabled>Remove Domain</button>", box);
        Assert.Contains("<button type=\"button\" class=\"btn btn-secondary\" data-bs-dismiss=\"modal\">Cancel</button>", box);
        // Bootstrap closes a modal on Escape unless told not to.
        Assert.DoesNotContain("data-bs-keyboard", box);
        Assert.DoesNotContain("data-bs-backdrop=\"static\"", box);
    }

    [Fact]
    public void The_script_enables_Remove_only_on_a_match_and_puts_the_cursor_in_the_box()
    {
        var view = View();
        var scripts = view.Substring(view.IndexOf("@section Scripts {", StringComparison.Ordinal));
        // Its own script element, so a failure in the page's other script cannot leave Remove disabled for good.
        var script = Regex.Match(scripts, "<script nonce=\"@Context.GetCspNonce\\(\\)\">\\s*// 594:.*?</script>", RegexOptions.Singleline);
        Assert.True(script.Success, "the Remove box needs its own script element in the Scripts section");
        var code = script.Value;

        Assert.Contains("document.querySelectorAll('[data-remove-domain]')", code);
        Assert.Contains("const typed = () => input.value.trim().toLowerCase();", code);
        Assert.Contains("const domain = form.dataset.removeDomain.toLowerCase();", code);
        Assert.Contains("submit.disabled = !matches;", code);
        Assert.Contains("'Type the domain exactly as shown'", code);
        // Not while it is still being typed: only once what is there cannot become the domain, or on Enter.
        Assert.Contains("!domain.startsWith(text)", code);
        Assert.Contains("event.key === 'Enter' && submit.disabled", code);
        // Opened afresh each time, with the cursor in the box.
        Assert.Contains("modal.addEventListener('show.bs.modal', () => { input.value = ''; update(false); });", code);
        Assert.Contains("modal.addEventListener('shown.bs.modal', () => input.focus());", code);
    }

    [Fact]
    public void No_other_box_asks_for_typing_without_a_label()
    {
        // The portal's and SuperAdmin's other "type it to confirm" screen (Start the website again) already has a
        // label tied to its box with the username in it; this keeps it that way.
        var startAgain = File.ReadAllText(Path.Combine(SolutionRoot(), "src", "IPRO.Admin", "Views", "Agents", "StartAgain.cshtml"));
        Assert.Contains("<label for=\"confirmUserName\" class=\"form-label fw-semibold\">To confirm, type the agent's username: <code>@agent.UserName</code></label>", startAgain);
        Assert.Contains("id=\"confirmUserName\" name=\"confirmUserName\"", startAgain);
    }

    [Fact]
    public async Task The_server_still_removes_only_on_the_typed_domain()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await AdviserAsync(db);
        var row = new AgentDomain
        {
            AgentUserId = agentId, AgentWebsiteId = websiteId, DomainName = "www.remove594.example.test", WwwDomain = "www.remove594.example.test",
            RootDomain = "remove594.example.test", DnsTarget = "ipro-prod-web.azurewebsites.net", IsPrimary = true,
            OwnershipVerifiedAt = DateTime.UtcNow.AddDays(-60),
            DnsStatus = AgentDomainStatus.Bound, AzureBindingStatus = AgentDomainStatus.Bound, SslStatus = AgentDomainStatus.Bound,
        };
        db.Add(row);
        await db.SaveChangesAsync();

        foreach (var wrong in new string?[] { null, "", "   ", "www.remove594.example", "remove594.example.test", "www.remove594.example.test.", "www.remove594 .example.test" })
        {
            var azure = new ScriptedAzure();
            var controller = Website(db, agentId, azure);
            Assert.IsType<RedirectToActionResult>(await controller.RemoveDomain(row.Id, wrong!));
            Assert.Equal("Type the exact domain name to confirm removal.", controller.TempData["Error"]);
            Assert.Empty(azure.Calls);
            db.ChangeTracker.Clear();
            Assert.True(await db.AgentDomains.AnyAsync(d => d.Id == row.Id), $"\"{wrong}\" must not remove the domain");
        }

        // Case and spaces at either end do not matter, the same as in the box.
        var remover = Website(db, agentId, new ScriptedAzure());
        await remover.RemoveDomain(row.Id, "  WWW.Remove594.Example.TEST ");
        Assert.Equal("www.remove594.example.test was removed.", remover.TempData["Success"]);
        db.ChangeTracker.Clear();
        Assert.False(await db.AgentDomains.AnyAsync(d => d.Id == row.Id));
    }

    // The Remove box, from its opening tag to the end of the domain loop.
    private static string RemoveBox()
    {
        var view = View();
        var start = view.IndexOf("<div class=\"modal fade\" id=\"removeDomainModal-@domain.Id\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "the Remove box must still be in My Website's view");
        var end = view.IndexOf("@if (primaryDomain != null)", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return view[start..end];
    }

    private static string View() =>
        File.ReadAllText(Path.Combine(SolutionRoot(), "src", "IPRO.Web", "Views", "Website", "Index.cshtml")).Replace("\r\n", "\n");

    private static string SolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    // A paying adviser with a published website whose package has extra domains.
    private static async Task<(int AgentId, int WebsiteId)> AdviserAsync(IPRO.DataAccess.IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T594-{Guid.NewGuid():N}")[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        var template = new WebsiteTemplate { Name = "Modern", TemplateKey = ($"t594-{Guid.NewGuid():N}")[..16], BusinessType = "All", IsActive = true };
        db.AddRange(rule, template);
        await db.SaveChangesAsync();
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.InstantWebsite, FeatureName = "Website", IsIncluded = true });
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.MultiDomainSupport, FeatureName = "Domains", IsIncluded = true });
        var agent = new AgentUser
        {
            UserName = ($"t594-{Guid.NewGuid():N}")[..20], Email = ($"t594-{Guid.NewGuid():N}")[..14] + "@example.test",
            FirstName = "Sam", LastName = "Smith", CompanyName = "Smith Trades",
            DomainName = ($"t594-{Guid.NewGuid():N}")[..24] + ".247advisers.com", Country = "Canada", Province = "Ontario",
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
        db.ChangeTracker.Clear();
        return (agent.Id, website.Id);
    }

    private static WebsiteController Website(IPRO.DataAccess.IPRODbContext db, int agentId, IAzureDomainAutomationService azure)
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

    private sealed class NeverCheck : IDomainCheckService
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
