using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[AllowAnonymous]
[Route("Poll/[action]")]
public class PollVoteController : Controller
{
    private readonly IPRODbContext _db;

    public PollVoteController(IPRODbContext db)
    {
        _db = db;
    }

    private async Task<string?> ResolveAgentSiteUrlAsync(int agentUserId)
    {
        var website = await _db.AgentWebsites.FirstOrDefaultAsync(w => w.AgentUserId == agentUserId);
        if (website == null || !website.IsPublished) return null;

        var boundDomain = await _db.AgentDomains
            .Where(d => d.AgentUserId == agentUserId && d.IsPrimary && d.AzureBindingStatus == AgentDomainStatus.Bound)
            .FirstOrDefaultAsync();
        if (boundDomain != null && !string.IsNullOrWhiteSpace(boundDomain.DomainName))
        {
            return $"https://{boundDomain.DomainName}";
        }

        // AgentUser.DomainName already stores the full temporary domain (e.g. "janedoe.247advisers.com").
        var agentUser = await _db.AgentUsers.FirstOrDefaultAsync(u => u.Id == agentUserId);
        if (agentUser == null || string.IsNullOrWhiteSpace(agentUser.DomainName)) return null;

        return $"https://{agentUser.DomainName}";
    }

    [HttpGet]
    public async Task<IActionResult> Vote(string token)
    {
        var trimmedToken = token?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedToken))
        {
            ViewBag.State = "invalid";
            return View();
        }

        var recipient = await _db.PollRecipients.FirstOrDefaultAsync(r => r.VoteToken == trimmedToken);
        if (recipient == null)
        {
            ViewBag.State = "invalid";
            return View();
        }

        var survey = await _db.PollSurveys.FirstOrDefaultAsync(s => s.Id == recipient.PollSurveyId);
        if (survey == null)
        {
            ViewBag.State = "invalid";
            return View();
        }

        if (recipient.Status == PollRecipientStatus.Responded)
        {
            ViewBag.State = "already-voted";
            ViewBag.AgentSiteUrl = await ResolveAgentSiteUrlAsync(survey.AgentUserId);
            return View();
        }

        var questions = await _db.PollQuestions.Where(q => q.PollSurveyId == survey.Id).OrderBy(q => q.SortOrder).ToListAsync();
        var questionIds = questions.Select(q => q.Id).ToList();
        var options = await _db.PollOptions.Where(o => questionIds.Contains(o.PollQuestionId)).OrderBy(o => o.SortOrder).ToListAsync();

        ViewBag.State = "form";
        ViewBag.Survey = survey;
        ViewBag.Questions = questions;
        ViewBag.OptionsByQuestion = options.GroupBy(o => o.PollQuestionId).ToDictionary(g => g.Key, g => g.ToList());
        ViewBag.Token = trimmedToken;
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Vote(PollVoteSubmissionViewModel model)
    {
        var trimmedToken = model.Token?.Trim() ?? string.Empty;
        var recipient = string.IsNullOrWhiteSpace(trimmedToken)
            ? null
            : await _db.PollRecipients.FirstOrDefaultAsync(r => r.VoteToken == trimmedToken);

        if (recipient == null)
        {
            ViewBag.State = "invalid";
            return View();
        }

        var survey = await _db.PollSurveys.FirstOrDefaultAsync(s => s.Id == recipient.PollSurveyId);
        if (survey == null)
        {
            ViewBag.State = "invalid";
            return View();
        }

        if (recipient.Status == PollRecipientStatus.Responded)
        {
            ViewBag.State = "already-voted";
            ViewBag.AgentSiteUrl = await ResolveAgentSiteUrlAsync(survey.AgentUserId);
            return View();
        }

        var questions = await _db.PollQuestions.Where(q => q.PollSurveyId == survey.Id).OrderBy(q => q.SortOrder).ToListAsync();
        var questionIds = questions.Select(q => q.Id).ToHashSet();
        var options = await _db.PollOptions.Where(o => questionIds.Contains(o.PollQuestionId)).ToListAsync();
        var optionsByQuestion = options.GroupBy(o => o.PollQuestionId).ToDictionary(g => g.Key, g => g.Select(o => o.Id).ToHashSet());

        var answers = model.Answers ?? new List<PollAnswerInput>();
        var answeredQuestionIds = answers.Select(a => a.QuestionId).ToHashSet();

        var allQuestionsAnswered = questionIds.All(qId => answeredQuestionIds.Contains(qId));
        var allAnswersValid = answers.All(a =>
            questionIds.Contains(a.QuestionId) &&
            optionsByQuestion.TryGetValue(a.QuestionId, out var validOptions) &&
            validOptions.Contains(a.OptionId));

        if (!allQuestionsAnswered || !allAnswersValid || answers.Count == 0)
        {
            ViewBag.State = "form";
            ViewBag.Survey = survey;
            ViewBag.Questions = questions;
            ViewBag.OptionsByQuestion = options.GroupBy(o => o.PollQuestionId).ToDictionary(g => g.Key, g => g.ToList());
            ViewBag.Token = trimmedToken;
            ViewBag.Error = "Please answer every question before submitting.";
            return View();
        }

        foreach (var answer in answers.DistinctBy(a => a.QuestionId))
        {
            _db.PollAnswers.Add(new PollAnswer
            {
                PollRecipientId = recipient.Id,
                PollQuestionId = answer.QuestionId,
                PollOptionId = answer.OptionId
            });
        }

        recipient.Status = PollRecipientStatus.Responded;
        recipient.RespondedAt = DateTime.UtcNow;
        recipient.UpdatedAt = DateTime.UtcNow;

        survey.TotalResponded += 1;
        if (recipient.PollSendId.HasValue)
        {
            var send = await _db.PollSends.FirstOrDefaultAsync(s => s.Id == recipient.PollSendId.Value);
            if (send != null) send.TotalResponded += 1;
        }

        await _db.SaveChangesAsync();

        ViewBag.State = "submitted";
        ViewBag.AgentSiteUrl = await ResolveAgentSiteUrlAsync(survey.AgentUserId);
        return View();
    }

    // 450 (2026-09-02): an anonymous vote from a website page. The block on the page vouches for the
    // survey (a visible PollVote block whose settings name it, on a page of the survey owner's site),
    // one vote per browser via cookie, and a per-survey hourly cap against floods. The visitor is
    // recorded as a Website recipient: no client, no email, no token they could reuse.
    private const int WebsiteVotesPerSurveyPerHour = 300;

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> WebsiteVote(IPRO.Web.Models.WebsitePollVoteSubmission model)
    {
        var returnPath = NormalizeReturnPath(model.ReturnPath);
        IActionResult Back(string outcome) => LocalRedirect($"{returnPath}{(returnPath.Contains('?') ? "&" : "?")}poll={outcome}&block={model.BlockId}#poll-{model.BlockId}");

        var block = await _db.WebsiteContentBlocks.AsNoTracking()
            .Where(b => b.Id == model.BlockId && b.IsVisible && b.BlockType == WebsiteBlockTypes.PollVote)
            .Select(b => new { b.SettingsJson, b.WebsitePage.AgentWebsite.AgentUserId })
            .FirstOrDefaultAsync();
        if (block == null || WebsitePollResultsSettings.FromJson(block.SettingsJson).PollSurveyId != model.SurveyId)
            return Back("invalid");

        var survey = await _db.PollSurveys.FirstOrDefaultAsync(s => s.Id == model.SurveyId && s.AgentUserId == block.AgentUserId);
        if (survey == null) return Back("invalid");

        if (IPRO.Web.Infrastructure.PollVoteCookies.Read(Request).Contains(survey.Id))
            return Back("already");

        var since = DateTime.UtcNow.AddHours(-1);
        var recent = await _db.PollRecipients.CountAsync(r => r.PollSurveyId == survey.Id && r.Source == PollRecipientSource.Website && r.CreatedAt >= since);
        if (recent >= WebsiteVotesPerSurveyPerHour) return Back("busy");

        var questions = await _db.PollQuestions.Where(q => q.PollSurveyId == survey.Id).Select(q => q.Id).ToListAsync();
        var options = await _db.PollOptions.Where(o => questions.Contains(o.PollQuestionId)).Select(o => new { o.Id, o.PollQuestionId }).ToListAsync();
        var validOptions = options.GroupBy(o => o.PollQuestionId).ToDictionary(g => g.Key, g => g.Select(o => o.Id).ToHashSet());
        var answers = (model.Answers ?? new List<IPRO.Web.Models.PollAnswerInput>()).DistinctBy(a => a.QuestionId).ToList();
        var complete = questions.Count > 0
            && questions.All(q => answers.Any(a => a.QuestionId == q))
            && answers.All(a => validOptions.TryGetValue(a.QuestionId, out var ok) && ok.Contains(a.OptionId));
        if (!complete) return Back("incomplete");

        var recipient = new PollRecipient
        {
            PollSurveyId = survey.Id,
            Source = PollRecipientSource.Website,
            Status = PollRecipientStatus.Responded,
            Email = string.Empty,
            RecipientName = "Website visitor",
            VoteToken = Guid.NewGuid().ToString("N"),
            RespondedAt = DateTime.UtcNow
        };
        _db.PollRecipients.Add(recipient);
        await _db.SaveChangesAsync();
        foreach (var a in answers)
            _db.PollAnswers.Add(new PollAnswer { PollRecipientId = recipient.Id, PollQuestionId = a.QuestionId, PollOptionId = a.OptionId });
        survey.TotalResponded += 1;
        await _db.SaveChangesAsync();

        IPRO.Web.Infrastructure.PollVoteCookies.Append(Request, Response, survey.Id);
        return Back("thanks");
    }

    private static string NormalizeReturnPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var p = path.Trim();
        if (!p.StartsWith('/') || p.StartsWith("//") || p.StartsWith("/\\")) return "/";
        return p;
    }
}
