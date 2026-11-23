using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IPRO.Billing;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 668, 669 and 671 (2026-11-23), built together on the owner's word.
//
// 668: a poll used only on the website had no Results page, and editing it wiped the votes (website
//      votes leave a poll a Draft, and saving an edit deletes and re-creates every question and option).
//      Now any poll with a response has Results, and its questions and its deletion are locked.
// 669: the Broker package says "1000 MB/per user"; the code enforced 1000 MB for the whole team. A
//      package whose storage label says "per user" now gets the figure for the owner and each active
//      team member.
// 671: the AI daily digest loaded every lead, client and overdue follow-up of every eligible adviser to
//      count them; the hourly billing-issue scan re-read every Failed and old Pending billing ever and
//      asked PayPal about each. Counts are now counted by the database, and the scan looks back 30 days.
public class PollsStorageNightJobs668Tests
{
    // ================================================================ 668: polls with votes ==

    [Fact]
    public async Task A_website_only_poll_with_a_vote_has_results_and_cannot_be_edited_or_deleted()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var s = await SeedPollAsync(db, websiteVotes: 1);
        var controller = NewPollsController(db, s.AgentId);

        // The list offers Results and no delete for it.
        var index = Assert.IsType<ViewResult>(await controller.Index());
        Assert.Contains(s.SurveyId, Assert.IsType<HashSet<int>>(controller.ViewBag.WithResponses));

        // The edit form is refused, and so is a posted edit (a vote can land while the form is open).
        var get = Assert.IsType<RedirectToActionResult>(await controller.Edit(s.SurveyId));
        Assert.Equal(nameof(PollsController.Preview), get.ActionName);
        Assert.Equal(PollsController.LockedByVotes, controller.TempData["Error"]);

        var post = Assert.IsType<RedirectToActionResult>(await controller.Edit(new IPRO.Web.Models.PollSurveyBuilderViewModel
        {
            Id = s.SurveyId, Title = "Changed", Subject = "Changed", IntroText = "Changed",
            Questions = new List<IPRO.Web.Models.PollQuestionInput> { new() { Text = "New question?", Options = new List<string> { "Yes", "No" } } }
        }));
        Assert.Equal(nameof(PollsController.Preview), post.ActionName);

        var delete = Assert.IsType<RedirectToActionResult>(await controller.Delete(s.SurveyId));
        Assert.Equal(nameof(PollsController.Preview), delete.ActionName);

        // Nothing was replaced: the vote still counts for the option it was cast for.
        db.ChangeTracker.Clear();
        Assert.True(await db.PollSurveys.AnyAsync(p => p.Id == s.SurveyId && p.Title == "Original"));
        Assert.True(await db.PollOptions.AnyAsync(o => o.Id == s.OptionA));
        Assert.Equal(1, await db.PollAnswers.CountAsync(a => a.PollOptionId == s.OptionA));

        // Results open for it.
        var results = Assert.IsType<ViewResult>(await controller.Results(s.SurveyId));
        Assert.Equal(1, Assert.IsType<Dictionary<int, int>>(controller.ViewBag.AnswerCounts)[s.OptionA]);
    }

    [Fact]
    public async Task A_draft_with_no_responses_is_still_editable_and_deletable()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var s = await SeedPollAsync(db, websiteVotes: 0);
        var controller = NewPollsController(db, s.AgentId);

        Assert.IsType<ViewResult>(await controller.Edit(s.SurveyId));
        await controller.Index();
        Assert.DoesNotContain(s.SurveyId, Assert.IsType<HashSet<int>>(controller.ViewBag.WithResponses));

        var delete = Assert.IsType<RedirectToActionResult>(await controller.Delete(s.SurveyId));
        Assert.Equal(nameof(PollsController.Index), delete.ActionName);
        db.ChangeTracker.Clear();
        Assert.False(await db.PollSurveys.AnyAsync(p => p.Id == s.SurveyId));
    }

    [Fact]
    public void The_poll_pages_show_results_for_any_response_and_hide_edit_and_delete_once_voted()
    {
        var preview = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Polls\Preview.cshtml"));
        Assert.Contains("@if (sends.Any() || responseCount > 0)", preview);
        Assert.Contains("@if (responseCount == 0)", preview);
        Assert.Contains("Use <strong>Duplicate</strong> to make an editable copy.", preview);

        var index = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Polls\Index.cshtml"));
        Assert.Contains("withResponses.Contains(poll.Id)", index);
        Assert.Contains("PollSurveyStatus.Draft && !withResponses.Contains(poll.Id)", index);

        // A poll only on the website was never sent: no "Sent to 0 ... (0%)".
        var results = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Polls\Results.cshtml"));
        Assert.Contains("@if (Model.TotalSent == 0)", results);
    }

    // ============================================================ 669: storage per user ==

    [Theory]
    [InlineData("1000 MB/per user", true)]
    [InlineData("1000 MB per user", true)]
    [InlineData("1000 MB per-user", true)]
    [InlineData("1000 MB Per User", true)]
    [InlineData("1000 MB", false)]
    [InlineData("superuser 1000 MB", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_storage_label_is_per_user_only_when_it_says_so(string? label, bool expected) =>
        Assert.Equal(expected, AgentStorage.IsPerUser(label));

    [Fact]
    public async Task A_per_user_package_counts_the_owner_and_each_active_team_member()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedStorageAgentAsync(db, 1000, "1000 MB/per user", activeMembers: 2, inactiveMembers: 1);

        var access = await new PackageEntitlementService(new UnitOfWork(db), db).GetAccessAsync(agentId, PackageFeatureCodes.FileUploadCapacity);

        Assert.True(access.PerUser);
        Assert.Equal(3, access.Users);                 // the owner and two active members; the inactive one does not count
        Assert.Equal(1000, access.PerUserLimitMb);
        Assert.Equal(3000, access.LimitValue);         // every check and display reads this
        Assert.Equal(3000L * 1024 * 1024, AgentStorage.LimitBytes(access.LimitValue));
        Assert.Equal("1000 MB/per user", access.LimitLabel);
    }

    [Fact]
    public async Task A_shared_package_keeps_one_pool_whatever_the_team()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedStorageAgentAsync(db, 1000, "1000 MB", activeMembers: 2, inactiveMembers: 0);

        var access = await new PackageEntitlementService(new UnitOfWork(db), db).GetAccessAsync(agentId, PackageFeatureCodes.FileUploadCapacity);

        Assert.False(access.PerUser);
        Assert.Equal(1000, access.LimitValue);
    }

    [Fact]
    public async Task A_per_user_package_with_no_figure_gives_the_default_to_each_user()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedStorageAgentAsync(db, null, "per user", activeMembers: 1, inactiveMembers: 0);

        var access = await new PackageEntitlementService(new UnitOfWork(db), db).GetAccessAsync(agentId, PackageFeatureCodes.FileUploadCapacity);

        Assert.Equal(2 * AgentStorage.DefaultLimitMb, access.LimitValue);
    }

    [Fact]
    public void The_documents_page_says_how_many_people_the_per_user_limit_counts()
    {
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Documents\Index.cshtml"));
        Assert.Contains("@if (access?.PerUser == true)", view);
        Assert.Contains("MB for each person on the account", view);

        var guide = File.ReadAllText(FindRepoFile(@"DOCS\12_AGENT_DOCUMENT_LIBRARY.md"));
        Assert.Contains("| Broker | 1000 MB per user: you and each active team member", guide);
        Assert.DoesNotContain("shared with your team members", guide);
    }

    // ============================================================== 671: night job reads ==

    [Fact]
    public async Task The_digest_counts_in_the_database_and_still_names_the_right_row()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        var ruleId = await SeedAiRuleAsync(db);

        // Adviser A: two overdue follow-ups (the older one is named), one completed overdue one (ignored),
        // three new leads of which two are over a day old, and one client with nothing scheduled.
        var a = await SeedAdviserAsync(db, ruleId, "A");
        var a1 = await AddClientAsync(db, a.AgentId, "Ann", "One", now.AddDays(-50));
        var a2 = await AddClientAsync(db, a.AgentId, "Ben", "Two", now.AddDays(-40));
        var a3 = await AddClientAsync(db, a.AgentId, "Cal", "Three", now.AddDays(-30));
        db.ClientFollowUps.AddRange(
            new ClientFollowUp { ClientId = a1, Title = "Most overdue", DueAt = now.Date.AddDays(-5) },
            new ClientFollowUp { ClientId = a2, Title = "Less overdue", DueAt = now.Date.AddDays(-2) },
            new ClientFollowUp { ClientId = a3, Title = "Done long ago", DueAt = now.Date.AddDays(-9), IsCompleted = true });
        AddLead(db, a, "Old", now.AddHours(-60));
        AddLead(db, a, "Older", now.AddHours(-80));
        AddLead(db, a, "Fresh", now.AddHours(-2));
        db.WebsiteLeads.Add(new WebsiteLead { AgentUserId = a.AgentId, AgentWebsiteId = a.WebsiteId, FirstName = "Handled", Status = WebsiteLeadStatuses.Contacted, CreatedAt = now.AddDays(-9) });

        // Adviser B: no follow-up due, two stale leads: the older is named.
        var b = await SeedAdviserAsync(db, ruleId, "B");
        AddLead(db, b, "Newer", now.AddHours(-30));
        AddLead(db, b, "Oldest", now.AddHours(-90));

        // Adviser C: nothing due, no leads, two clients with nothing scheduled: the first created is named.
        var c = await SeedAdviserAsync(db, ruleId, "C");
        await AddClientAsync(db, c.AgentId, "Late", "Comer", now.AddDays(-3));
        var cFirst = await AddClientAsync(db, c.AgentId, "Early", "Bird", now.AddDays(-20));

        // Adviser D: nothing at all.
        var d = await SeedAdviserAsync(db, ruleId, "D");
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await new IPRO.Scheduler.AiDailyDigestJob(db, new PackageEntitlementService(new UnitOfWork(db), db), new NullAi(),
            NullLogger<IPRO.Scheduler.AiDailyDigestJob>.Instance).RunAsync();
        db.ChangeTracker.Clear();

        var ia = await db.AgentDailyInsights.SingleAsync(i => i.AgentUserId == a.AgentId);
        Assert.Equal(3, ia.NewLeadCount);
        Assert.Equal(2, ia.StaleLeadCount);
        Assert.Equal(1, ia.NoFollowUpClientCount);     // Cal: his only follow-up is completed
        Assert.Equal(AgentDailyInsightActionTypes.OverdueFollowUp, ia.SuggestedActionType);
        Assert.Contains("\"Most overdue\" is 5 days overdue", ia.SuggestedActionText);
        Assert.Contains($"/Clients/Details/{a1}", ia.SuggestedActionUrl);

        var ib = await db.AgentDailyInsights.SingleAsync(i => i.AgentUserId == b.AgentId);
        Assert.Equal(2, ib.StaleLeadCount);
        Assert.Equal(AgentDailyInsightActionTypes.StaleLead, ib.SuggestedActionType);
        Assert.StartsWith("Call Oldest ", ib.SuggestedActionText);

        var ic = await db.AgentDailyInsights.SingleAsync(i => i.AgentUserId == c.AgentId);
        Assert.Equal(2, ic.NoFollowUpClientCount);
        Assert.Equal(AgentDailyInsightActionTypes.NoFollowUp, ic.SuggestedActionType);
        Assert.Contains("Early Bird", ic.SuggestedActionText);
        Assert.Contains($"/Clients/Details/{cFirst}", ic.SuggestedActionUrl);

        var id = await db.AgentDailyInsights.SingleAsync(i => i.AgentUserId == d.AgentId);
        Assert.Equal(AgentDailyInsightActionTypes.None, id.SuggestedActionType);
        Assert.Equal(0, id.NewLeadCount + id.StaleLeadCount + id.NoFollowUpClientCount);
    }

    [Fact]
    public void The_digest_no_longer_loads_the_lists_it_only_counts()
    {
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Scheduler\AiDailyDigestJob.cs"));
        Assert.DoesNotContain("var noFollowUpClients = await", src);
        Assert.DoesNotContain("var overdueFollowUps = await", src);
        Assert.DoesNotContain("var newLeads = await", src);
        Assert.Equal(3, Regex.Matches(src, @"\.GroupBy\(\w => \w\.AgentUserId\)\s*\.Select\(g => new").Count
            + Regex.Matches(src, @"\.Select\(f => f\.Client\.AgentUserId\)\s*\.Distinct\(\)").Count);
    }

    [Fact]
    public async Task A_checkout_abandoned_over_30_days_ago_is_no_longer_scanned_and_a_recent_one_still_is()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        var old = await SeedPendingCheckoutAsync(db, "old", now.AddDays(-45));
        var recent = await SeedPendingCheckoutAsync(db, "recent", now.AddDays(-2));

        var email = new RecordingEmail();
        await NewBilling(db, email).NotifyBillingIssuesAsync();

        Assert.Contains(email.Sent, m => m.StartsWith("recent"));
        Assert.DoesNotContain(email.Sent, m => m.StartsWith("old"));
        db.ChangeTracker.Clear();
        Assert.False(await db.OperateLogs.AnyAsync(l => l.AgentUserId == old && l.Action == "BillingIssueEmail"));
        Assert.True(await db.OperateLogs.AnyAsync(l => l.AgentUserId == recent && l.Action == "BillingIssueEmail"));

        // The second hour sends nothing again (each notice goes out once).
        var again = new RecordingEmail();
        await NewBilling(db, again).NotifyBillingIssuesAsync();
        Assert.Empty(again.Sent);
    }

    [Fact]
    public void The_billing_scan_checks_the_database_before_asking_paypal_and_looks_back_30_days()
    {
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Billing\PayPalBillingService.cs"));
        var start = src.IndexOf("public async Task<int> NotifyBillingIssuesAsync()", StringComparison.Ordinal);
        var body = src[start..src.IndexOf("var failedSubscriptionInvoices", start, StringComparison.Ordinal)];
        Assert.True(body.IndexOf("BillingIssueEmailAlreadySentAsync(billing, invoice)", StringComparison.Ordinal)
                    < body.IndexOf("GetActiveSubscriptionAsync(billing.AgentUserId)", StringComparison.Ordinal),
            "an already-notified billing must be skipped before GetActiveSubscriptionAsync can reach PayPal");
        Assert.Contains("b.CreatedAt > lookBack", body);
        Assert.Equal(30, PayPalBillingService.BillingIssueLookBackDays);
        Assert.Contains("i.IssuedAt > lookBack", src);
        Assert.Contains("c.AppliedAt != null && c.AppliedAt > lookBack", src);
    }

    // ================================================================== plumbing ==

    private sealed record PollSeed(int AgentId, int SurveyId, int OptionA);

    private static async Task<PollSeed> SeedPollAsync(IPRODbContext db, int websiteVotes)
    {
        var rule = new BillingRule { PackageName = $"PL-{Guid.NewGuid():N}"[..20], MonthlyPrice = 40m, AnnualPrice = 400m };
        db.Add(rule);
        await db.SaveChangesAsync();
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.PollSurveys, FeatureName = "Polls", IsIncluded = true });
        var agentId = await AddAgentAsync(db, rule.Id, "pl");

        var survey = new PollSurvey { AgentUserId = agentId, Title = "Original", Subject = "S", IntroText = "I", Status = PollSurveyStatus.Draft };
        db.Add(survey);
        await db.SaveChangesAsync();
        var question = new PollQuestion { PollSurveyId = survey.Id, Text = "Which?", SortOrder = 0 };
        db.Add(question);
        await db.SaveChangesAsync();
        var a = new PollOption { PollQuestionId = question.Id, Text = "A", SortOrder = 0 };
        var b = new PollOption { PollQuestionId = question.Id, Text = "B", SortOrder = 1 };
        db.AddRange(a, b);
        await db.SaveChangesAsync();

        for (var i = 0; i < websiteVotes; i++)
        {
            var recipient = new PollRecipient
            {
                PollSurveyId = survey.Id, Source = PollRecipientSource.Website, Status = PollRecipientStatus.Responded,
                RecipientName = "Website visitor", VoteToken = Guid.NewGuid().ToString("N"), RespondedAt = DateTime.UtcNow
            };
            db.Add(recipient);
            await db.SaveChangesAsync();
            db.Add(new PollAnswer { PollRecipientId = recipient.Id, PollQuestionId = question.Id, PollOptionId = a.Id });
            survey.TotalResponded += 1;
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new PollSeed(agentId, survey.Id, a.Id);
    }

    private static PollsController NewPollsController(IPRODbContext db, int agentId)
    {
        // The client list and the email dispatcher are only used by Send, which these tests do not call.
        var controller = new PollsController(db, null!, new PackageEntitlementService(new UnitOfWork(db), db), null!);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NullTempData());
        return controller;
    }

    private static async Task<int> SeedStorageAgentAsync(IPRODbContext db, int? limitMb, string label, int activeMembers, int inactiveMembers)
    {
        var rule = new BillingRule { PackageName = $"ST-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        await db.SaveChangesAsync();
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.FileUploadCapacity, FeatureName = "Storage", IsIncluded = true, LimitValue = limitMb, LimitLabel = label });
        var agentId = await AddAgentAsync(db, rule.Id, "st");
        for (var i = 0; i < activeMembers + inactiveMembers; i++)
        {
            db.Add(new TeamMember
            {
                AgentUserId = agentId, FullName = $"Member {i}", Email = $"tm-{Guid.NewGuid():N}"[..16] + "@example.test",
                PasswordHash = "x", IsActive = i < activeMembers
            });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return agentId;
    }

    private static async Task<int> AddAgentAsync(IPRODbContext db, int ruleId, string prefix)
    {
        var agent = new AgentUser
        {
            UserName = $"{prefix}-{Guid.NewGuid():N}"[..20],
            Email = $"{prefix}-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Test", LastName = prefix,
            DomainName = $"{prefix}-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario", PackageId = ruleId
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        db.Add(new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = ruleId, Amount = 40m,
            Status = BillingStatus.Active, Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddDays(-10), NextBillingDate = DateTime.UtcNow.AddDays(20)
        });
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private sealed record Adviser(int AgentId, int WebsiteId);

    private static async Task<int> SeedAiRuleAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = $"AI-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        await db.SaveChangesAsync();
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.AiDailyAssistant, FeatureName = "AI", IsIncluded = true });
        await db.SaveChangesAsync();
        return rule.Id;
    }

    private static async Task<Adviser> SeedAdviserAsync(IPRODbContext db, int ruleId, string marker)
    {
        var agentId = await AddAgentAsync(db, ruleId, "ai" + marker.ToLowerInvariant());
        var template = new WebsiteTemplate { TemplateKey = $"t-{Guid.NewGuid():N}"[..16], Name = "T" };
        db.Add(template);
        await db.SaveChangesAsync();
        var website = new AgentWebsite { AgentUserId = agentId, TemplateId = template.Id };
        db.Add(website);
        await db.SaveChangesAsync();
        return new Adviser(agentId, website.Id);
    }

    private static async Task<int> AddClientAsync(IPRODbContext db, int agentId, string first, string last, DateTime createdAt)
    {
        var client = new Client { AgentUserId = agentId, FirstName = first, LastName = last, Email = $"{Guid.NewGuid():N}"[..12] + "@example.test", CreatedAt = createdAt };
        db.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    private static void AddLead(IPRODbContext db, Adviser adviser, string firstName, DateTime createdAt) =>
        db.WebsiteLeads.Add(new WebsiteLead
        {
            AgentUserId = adviser.AgentId, AgentWebsiteId = adviser.WebsiteId, FirstName = firstName, LastName = "Lead",
            Email = $"{Guid.NewGuid():N}"[..12] + "@example.test", Status = WebsiteLeadStatuses.New, CreatedAt = createdAt
        });

    // A first checkout left Pending, with the unpaid invoice the issue email is about.
    private static async Task<int> SeedPendingCheckoutAsync(IPRODbContext db, string marker, DateTime createdAt)
    {
        var rule = new BillingRule { PackageName = $"BI-{Guid.NewGuid():N}"[..20], MonthlyPrice = 40m, AnnualPrice = 400m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"bi-{Guid.NewGuid():N}"[..20],
            Email = $"{marker}-{Guid.NewGuid():N}"[..16] + "@example.test",
            FirstName = marker, LastName = "Checkout",
            DomainName = $"bi-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario", PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var billing = new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 40m, Status = BillingStatus.Pending,
            Period = BillingPeriod.Monthly, StartDate = createdAt, CreatedAt = createdAt
        };
        db.Add(billing);
        await db.SaveChangesAsync();
        db.Add(new Invoice
        {
            BillingId = billing.Id, AgentUserId = agent.Id, InvoiceNumber = $"BI-{billing.Id}-{Guid.NewGuid():N}"[..20],
            SubTotal = 40m, TaxRate = 0.13m, TaxAmount = 5.20m, Total = 45.20m, IssuedAt = createdAt, IsPaid = false
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return agent.Id;
    }

    private static PayPalBillingService NewBilling(IPRODbContext db, IPRO.Email.IEmailService email) =>
        new(new UnitOfWork(db), db, new OfflineHttp(), email, Options.Create(new PayPalSettings()),
            new ConfigurationBuilder().Build(), NullLogger<PayPalBillingService>.Instance);

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }

    // Any call that reaches PayPal fails: nothing in these tests may need it.
    private sealed class OfflineHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Refuse());
        private sealed class Refuse : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken ct)
                => throw new HttpRequestException("offline test handler");
        }
    }

    private sealed class RecordingEmail : IPRO.Email.IEmailService
    {
        public readonly List<string> Sent = new();
        public Task<bool> SendAsync(string to, string name, string subject, string html, string? text = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null)
        { Sent.Add(to); return Task.FromResult(true); }
        public Task<IPRO.Email.EmailSendResult> SendDetailedAsync(string to, string name, string subject, string html, string? text = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null)
        { Sent.Add(to); return Task.FromResult(IPRO.Email.EmailSendResult.Sent()); }
        public Task<bool> SendBulkAsync(IEnumerable<IPRO.Email.EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
    }

    private sealed class NullAi : IPRO.Business.Interfaces.IAiSuggestionService
    {
        public Task<IPRO.Business.Interfaces.AiActionReasonResult> GenerateActionReasonAsync(string situation, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(IPRO.Business.Interfaces.AiActionReasonResult.Empty);
        public Task<IPRO.Business.Interfaces.AiActionReasonResult> DraftSocialPostAsync(string topic, string? businessType = null, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(IPRO.Business.Interfaces.AiActionReasonResult.Empty);
        public Task<IPRO.Business.Interfaces.AiNewsletterDraftResult> DraftNewsletterAsync(string topic, string? businessType = null, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(IPRO.Business.Interfaces.AiNewsletterDraftResult.Empty);
        public Task<IPRO.Business.Interfaces.AiBlogPostDraftResult> DraftBlogPostAsync(string topic, string? businessType = null, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(IPRO.Business.Interfaces.AiBlogPostDraftResult.Empty);
    }

    private sealed class NullTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
