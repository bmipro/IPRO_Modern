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
}
