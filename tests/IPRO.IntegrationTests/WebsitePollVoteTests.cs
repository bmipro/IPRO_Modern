using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 450 (2026-09-02). Polls were email-only: a named client, a vote token, one vote each, and a
// PollResults block that shows aggregates once ten people answered (an anonymity floor, because
// with two or three responses a visitor who knows the clients can work out who said what). Now a
// visitor can vote on the page: a PollVote block renders the question and options; the vote is
// recorded as an anonymous Website recipient (no client, no email), one per browser via a cookie,
// with a per-survey hourly cap. Results show straight away for a poll nobody was emailed, and
// still respect the floor for one that was.
public class WebsitePollVoteTests
{
    // ---- voting ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_visitor_vote_is_recorded_anonymously_and_counted()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var s = await SeedAsync(db);

        var ctx = new DefaultHttpContext();
        var controller = new IPRO.Web.Controllers.PollVoteController(db) { ControllerContext = new ControllerContext { HttpContext = ctx } };
        var result = await controller.WebsiteVote(new WebsitePollVoteSubmission
        {
            SurveyId = s.SurveyId, BlockId = s.BlockId, ReturnPath = "/poll",
            Answers = new List<PollAnswerInput> { new() { QuestionId = s.QuestionId, OptionId = s.OptionA } }
        });

        var redirect = Assert.IsType<LocalRedirectResult>(result);
        Assert.StartsWith("/poll?poll=thanks", redirect.Url);
        Assert.Contains("ipro_poll_voted", ctx.Response.Headers["Set-Cookie"].ToString());

        db.ChangeTracker.Clear();
        var recipient = Assert.Single(await db.PollRecipients.AsNoTracking().Where(r => r.PollSurveyId == s.SurveyId).ToListAsync());
        Assert.Equal(PollRecipientSource.Website, recipient.Source);
        Assert.Null(recipient.ClientId);
        Assert.Equal(string.Empty, recipient.Email);
        Assert.Equal(PollRecipientStatus.Responded, recipient.Status);
        var answer = Assert.Single(await db.PollAnswers.AsNoTracking().Where(a => a.PollRecipientId == recipient.Id).ToListAsync());
        Assert.Equal(s.OptionA, answer.PollOptionId);
        Assert.Equal(1, (await db.PollSurveys.AsNoTracking().SingleAsync(x => x.Id == s.SurveyId)).TotalResponded);
    }

    [Fact]
    public async Task A_repeat_vote_from_the_same_browser_is_refused()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var s = await SeedAsync(db);

        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = $"ipro_poll_voted={s.SurveyId}";
        var controller = new IPRO.Web.Controllers.PollVoteController(db) { ControllerContext = new ControllerContext { HttpContext = ctx } };
        var result = await controller.WebsiteVote(new WebsitePollVoteSubmission
        {
            SurveyId = s.SurveyId, BlockId = s.BlockId, ReturnPath = "/poll",
            Answers = new List<PollAnswerInput> { new() { QuestionId = s.QuestionId, OptionId = s.OptionA } }
        });

        Assert.StartsWith("/poll?poll=already", Assert.IsType<LocalRedirectResult>(result).Url);
        Assert.Empty(await db.PollRecipients.AsNoTracking().Where(r => r.PollSurveyId == s.SurveyId).ToListAsync());
    }

    [Fact]
    public async Task An_incomplete_vote_records_nothing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var s = await SeedAsync(db);

        var controller = new IPRO.Web.Controllers.PollVoteController(db) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var result = await controller.WebsiteVote(new WebsitePollVoteSubmission { SurveyId = s.SurveyId, BlockId = s.BlockId, ReturnPath = "/poll", Answers = new List<PollAnswerInput>() });

        Assert.StartsWith("/poll?poll=incomplete", Assert.IsType<LocalRedirectResult>(result).Url);
        Assert.Empty(await db.PollRecipients.AsNoTracking().Where(r => r.PollSurveyId == s.SurveyId).ToListAsync());
    }

    [Fact]
    public async Task A_block_that_does_not_carry_the_survey_is_refused()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var s = await SeedAsync(db);
        var other = await SeedAsync(db); // a different agent's site, survey and block

        var controller = new IPRO.Web.Controllers.PollVoteController(db) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var result = await controller.WebsiteVote(new WebsitePollVoteSubmission
        {
            SurveyId = s.SurveyId, BlockId = other.BlockId, ReturnPath = "/poll",
            Answers = new List<PollAnswerInput> { new() { QuestionId = s.QuestionId, OptionId = s.OptionA } }
        });

        Assert.StartsWith("/poll?poll=invalid", Assert.IsType<LocalRedirectResult>(result).Url);
        Assert.Empty(await db.PollRecipients.AsNoTracking().Where(r => r.PollSurveyId == s.SurveyId).ToListAsync());
    }

    [Fact]
    public async Task The_return_path_is_kept_local()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var s = await SeedAsync(db);

        var controller = new IPRO.Web.Controllers.PollVoteController(db) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var result = await controller.WebsiteVote(new WebsitePollVoteSubmission
        {
            SurveyId = s.SurveyId, BlockId = s.BlockId, ReturnPath = "https://evil.example/phish",
            Answers = new List<PollAnswerInput> { new() { QuestionId = s.QuestionId, OptionId = s.OptionA } }
        });

        Assert.StartsWith("/?poll=thanks", Assert.IsType<LocalRedirectResult>(result).Url);
    }

    // ---- what the page shows -------------------------------------------------------------------

    [Fact]
    public async Task The_builder_maps_the_block_to_its_survey_and_knows_who_voted()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var s = await SeedAsync(db);
        var page = await db.WebsitePages.Include(p => p.Blocks).AsNoTracking().SingleAsync(p => p.Id == s.PageId);

        var fresh = await PollVoteBuilder.BuildAsync(db, s.AgentId, page, new HashSet<int>());
        var data = fresh[s.BlockId];
        Assert.Equal(s.SurveyId, data.SurveyId);
        Assert.Equal("Best day for a review?", data.Title);
        var question = Assert.Single(data.Questions);
        Assert.Equal(2, question.Options.Count);
        Assert.False(data.HasVoted);
        Assert.False(data.ShowResults); // nobody has voted yet

        var voted = await PollVoteBuilder.BuildAsync(db, s.AgentId, page, new HashSet<int> { s.SurveyId });
        Assert.True(voted[s.BlockId].HasVoted);
    }

    [Fact]
    public async Task Results_show_at_once_for_a_website_only_poll_but_respect_the_floor_when_clients_were_emailed()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var s = await SeedAsync(db);
        var page = await db.WebsitePages.Include(p => p.Blocks).AsNoTracking().SingleAsync(p => p.Id == s.PageId);

        // One website vote on a poll nobody was emailed: results are safe to show.
        await VoteAsync(db, s.SurveyId, s.QuestionId, s.OptionA, PollRecipientSource.Website);
        var websiteOnly = (await PollVoteBuilder.BuildAsync(db, s.AgentId, page, new HashSet<int>()))[s.BlockId];
        Assert.True(websiteOnly.ShowResults);
        Assert.NotNull(websiteOnly.Results);
        Assert.Equal(1, websiteOnly.Results!.TotalResponded);
        Assert.Equal(100, websiteOnly.Results.Questions.Single().Options.Single(o => o.Text == "Monday").Percent);

        // Add one emailed client's answer: below the floor, so results hide again.
        await VoteAsync(db, s.SurveyId, s.QuestionId, s.OptionB, PollRecipientSource.Email);
        var mixed = (await PollVoteBuilder.BuildAsync(db, s.AgentId, page, new HashSet<int>()))[s.BlockId];
        Assert.False(mixed.ShowResults);
        Assert.Null(mixed.Results);

        // The owner's preview always sees the numbers.
        var preview = (await PollVoteBuilder.BuildAsync(db, s.AgentId, page, new HashSet<int>(), isOwnerPreview: true))[s.BlockId];
        Assert.True(preview.ShowResults);
    }

    [Fact]
    public async Task Another_agents_survey_cannot_be_shown_through_the_block()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var s = await SeedAsync(db);
        var other = await SeedAsync(db);
        // Point this agent's block at the other agent's survey.
        var block = await db.WebsiteContentBlocks.SingleAsync(b => b.Id == s.BlockId);
        block.SettingsJson = new WebsitePollResultsSettings { PollSurveyId = other.SurveyId }.ToJson();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var page = await db.WebsitePages.Include(p => p.Blocks).AsNoTracking().SingleAsync(p => p.Id == s.PageId);

        var data = await PollVoteBuilder.BuildAsync(db, s.AgentId, page, new HashSet<int>());
        Assert.False(data.ContainsKey(s.BlockId));
    }

    // ---- pins: the pieces a later edit could detach --------------------------------------------

    [Fact]
    public void The_block_type_exists_and_every_template_renders_it()
    {
        Assert.Contains(WebsiteBlockTypes.PollVote, WebsiteBlockTypes.All);
        Assert.Contains("Vote", WebsiteBlockTypes.DisplayName(WebsiteBlockTypes.PollVote));

        foreach (var shell in new[] { "_ModernManagedPage.cshtml", "_ClassicManagedPage.cshtml", "_EditorialManagedPage.cshtml" })
        {
            var src = File.ReadAllText(FindRepoFile(Path.Combine(@"src\IPRO.Web\Views\PublicWebsite", shell)));
            Assert.Contains("WebsiteBlockTypes.PollVote", src);
            Assert.Contains("_PublicPollVote", src);
        }

        var partial = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\PublicWebsite\_PublicPollVote.cshtml"));
        Assert.Contains("/Poll/WebsiteVote", partial);
        Assert.Contains("AntiForgeryToken", partial);
        Assert.Contains("_PollResults", partial);
    }

    [Fact]
    public void The_editor_offers_the_block_and_gates_it_like_poll_results()
    {
        var edit = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\WebsitePages\Edit.cshtml"));
        Assert.Contains("WebsiteBlockTypes.PollVote", edit);
        Assert.Contains("AvailableVotePolls", edit);

        var controller = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\WebsitePagesController.cs"));
        Assert.Matches(new Regex(@"blockType == WebsiteBlockTypes\.PollResults \|\| blockType == WebsiteBlockTypes\.PollVote"), controller);
        Assert.Matches(new Regex(@"block\.BlockType == WebsiteBlockTypes\.PollResults \|\| block\.BlockType == WebsiteBlockTypes\.PollVote"), controller);

        var results = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Polls\Results.cshtml"));
        Assert.Contains("from your website", results);

        var repair = File.ReadAllText(FindRepoFile(@"src\IPRO.DataAccess\StartupSchemaRepair.cs"));
        Assert.Contains("ALTER TABLE `PollRecipients` ADD COLUMN `Source`", repair);
        foreach (var app in new[] { @"src\IPRO.Web\Program.cs", @"src\IPRO.Admin\Program.cs" })
            Assert.Contains("EnsurePollWebsiteVoteSchemaAsync", File.ReadAllText(FindRepoFile(app)));
    }

    // ---- harness ------------------------------------------------------------------------------

    private sealed record Seeded(int AgentId, int PageId, int BlockId, int SurveyId, int QuestionId, int OptionA, int OptionB);

    private static async Task<Seeded> SeedAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T450-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t450-{Guid.NewGuid():N}")[..20], Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Poll", LastName = "Agent", CompanyName = "Poll Co",
            DomainName = ($"t450-{Guid.NewGuid():N}")[..24], PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();

        var template = new WebsiteTemplate { TemplateKey = ($"t450-{Guid.NewGuid():N}")[..16], Name = "T450", BusinessType = "Insurance" };
        db.Add(template);
        await db.SaveChangesAsync();
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, SiteTitle = "Poll Co", IsPublished = true };
        db.AgentWebsites.Add(website);
        await db.SaveChangesAsync();
        var page = new WebsitePage { AgentWebsiteId = website.Id, Title = "Poll", Slug = $"poll-{Guid.NewGuid():N}"[..20], IsPublished = true, IsHomePage = true };
        db.WebsitePages.Add(page);
        await db.SaveChangesAsync();

        var survey = new PollSurvey { AgentUserId = agent.Id, Title = "Best day for a review?", Subject = "Quick question", Status = PollSurveyStatus.Draft };
        db.Add(survey);
        await db.SaveChangesAsync();
        var question = new PollQuestion { PollSurveyId = survey.Id, Text = "Which day suits you?", SortOrder = 0 };
        db.Add(question);
        await db.SaveChangesAsync();
        var a = new PollOption { PollQuestionId = question.Id, Text = "Monday", SortOrder = 0 };
        var b = new PollOption { PollQuestionId = question.Id, Text = "Friday", SortOrder = 1 };
        db.AddRange(a, b);
        await db.SaveChangesAsync();

        var block = new WebsiteContentBlock
        {
            WebsitePageId = page.Id, BlockType = WebsiteBlockTypes.PollVote, Heading = "Have your say",
            SortOrder = 0, IsVisible = true, SettingsJson = new WebsitePollResultsSettings { PollSurveyId = survey.Id }.ToJson()
        };
        db.Add(block);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seeded(agent.Id, page.Id, block.Id, survey.Id, question.Id, a.Id, b.Id);
    }

    private static async Task VoteAsync(IPRODbContext db, int surveyId, int questionId, int optionId, PollRecipientSource source)
    {
        var recipient = new PollRecipient
        {
            PollSurveyId = surveyId, Source = source, Status = PollRecipientStatus.Responded,
            Email = source == PollRecipientSource.Email ? $"c-{Guid.NewGuid():N}@example.test" : string.Empty,
            RecipientName = source == PollRecipientSource.Email ? "A Client" : "Website visitor",
            VoteToken = Guid.NewGuid().ToString("N"), RespondedAt = DateTime.UtcNow
        };
        db.PollRecipients.Add(recipient);
        await db.SaveChangesAsync();
        db.PollAnswers.Add(new PollAnswer { PollRecipientId = recipient.Id, PollQuestionId = questionId, PollOptionId = optionId });
        var survey = await db.PollSurveys.SingleAsync(s => s.Id == surveyId);
        survey.TotalResponded += 1;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
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
