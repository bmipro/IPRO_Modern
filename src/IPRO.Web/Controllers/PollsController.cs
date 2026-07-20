using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class PollsController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IClientService _clients;
    private readonly IPackageEntitlementService _entitlements;
    private readonly PollDispatcher _dispatcher;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public PollsController(IPRODbContext db, IClientService clients, IPackageEntitlementService entitlements, PollDispatcher dispatcher)
    {
        _db = db;
        _clients = clients;
        _entitlements = entitlements;
        _dispatcher = dispatcher;
    }

    public async Task<IActionResult> Index()
    {
        var gate = await RequirePollAccessAsync();
        if (gate != null) return gate;

        var polls = await _db.PollSurveys
            .Where(p => p.AgentUserId == AgentId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();
        return View(polls);
    }

    public async Task<IActionResult> Create()
    {
        var gate = await RequirePollAccessAsync();
        if (gate != null) return gate;

        return View(new PollSurveyBuilderViewModel
        {
            Questions = new List<PollQuestionInput> { new() { Options = new List<string> { "", "" } } }
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(PollSurveyBuilderViewModel model)
    {
        var gate = await RequirePollAccessAsync();
        if (gate != null) return gate;

        if (!ValidateBuilder(model))
        {
            return View(model);
        }

        var survey = new PollSurvey
        {
            AgentUserId = AgentId,
            Title = model.Title.Trim(),
            Subject = model.Subject.Trim(),
            IntroText = model.IntroText.Trim(),
            Status = PollSurveyStatus.Draft
        };
        _db.PollSurveys.Add(survey);
        await _db.SaveChangesAsync();

        await SaveQuestionsAsync(survey.Id, model);

        TempData["Success"] = "Poll saved as a draft.";
        return RedirectToAction(nameof(Preview), new { id = survey.Id });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var gate = await RequirePollAccessAsync();
        if (gate != null) return gate;

        var survey = await _db.PollSurveys.FirstOrDefaultAsync(p => p.Id == id && p.AgentUserId == AgentId);
        if (survey == null) return NotFound();
        if (survey.Status != PollSurveyStatus.Draft)
        {
            TempData["Error"] = "Only draft polls that have not been sent can be edited.";
            return RedirectToAction(nameof(Preview), new { id });
        }

        var questions = await _db.PollQuestions.Where(q => q.PollSurveyId == id).OrderBy(q => q.SortOrder).ToListAsync();
        var questionIds = questions.Select(q => q.Id).ToList();
        var options = await _db.PollOptions.Where(o => questionIds.Contains(o.PollQuestionId)).OrderBy(o => o.SortOrder).ToListAsync();

        var model = new PollSurveyBuilderViewModel
        {
            Id = survey.Id,
            Title = survey.Title,
            Subject = survey.Subject,
            IntroText = survey.IntroText,
            Questions = questions.Select(q => new PollQuestionInput
            {
                Text = q.Text,
                Options = options.Where(o => o.PollQuestionId == q.Id).Select(o => o.Text).ToList()
            }).ToList()
        };
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(PollSurveyBuilderViewModel model)
    {
        var gate = await RequirePollAccessAsync();
        if (gate != null) return gate;

        var survey = await _db.PollSurveys.FirstOrDefaultAsync(p => p.Id == model.Id && p.AgentUserId == AgentId);
        if (survey == null) return NotFound();
        if (survey.Status != PollSurveyStatus.Draft)
        {
            TempData["Error"] = "Only draft polls that have not been sent can be edited.";
            return RedirectToAction(nameof(Preview), new { id = model.Id });
        }

        if (!ValidateBuilder(model))
        {
            return View(model);
        }

        survey.Title = model.Title.Trim();
        survey.Subject = model.Subject.Trim();
        survey.IntroText = model.IntroText.Trim();
        survey.UpdatedAt = DateTime.UtcNow;

        var existingQuestions = await _db.PollQuestions.Where(q => q.PollSurveyId == survey.Id).ToListAsync();
        var existingQuestionIds = existingQuestions.Select(q => q.Id).ToList();
        var existingOptions = await _db.PollOptions.Where(o => existingQuestionIds.Contains(o.PollQuestionId)).ToListAsync();
        _db.PollOptions.RemoveRange(existingOptions);
        _db.PollQuestions.RemoveRange(existingQuestions);
        await _db.SaveChangesAsync();

        await SaveQuestionsAsync(survey.Id, model);

        TempData["Success"] = "Poll updated.";
        return RedirectToAction(nameof(Preview), new { id = survey.Id });
    }

    public async Task<IActionResult> Preview(int id)
    {
        var gate = await RequirePollAccessAsync();
        if (gate != null) return gate;

        var survey = await _db.PollSurveys.FirstOrDefaultAsync(p => p.Id == id && p.AgentUserId == AgentId);
        if (survey == null) return NotFound();

        var questions = await _db.PollQuestions.Where(q => q.PollSurveyId == id).OrderBy(q => q.SortOrder).ToListAsync();
        var questionIds = questions.Select(q => q.Id).ToList();
        var options = await _db.PollOptions.Where(o => questionIds.Contains(o.PollQuestionId)).OrderBy(o => o.SortOrder).ToListAsync();
        var sends = await _db.PollSends.Where(s => s.PollSurveyId == id).OrderByDescending(s => s.ScheduledAt).ToListAsync();

        ViewBag.Questions = questions;
        ViewBag.OptionsByQuestion = options.GroupBy(o => o.PollQuestionId).ToDictionary(g => g.Key, g => g.ToList());
        ViewBag.Sends = sends;
        return View(survey);
    }

    public async Task<IActionResult> Send(int id)
    {
        var gate = await RequirePollAccessAsync();
        if (gate != null) return gate;

        var survey = await _db.PollSurveys.FirstOrDefaultAsync(p => p.Id == id && p.AgentUserId == AgentId);
        if (survey == null) return NotFound();
        if (!await _db.PollQuestions.AnyAsync(q => q.PollSurveyId == id))
        {
            TempData["Error"] = "Add at least one question before sending this poll.";
            return RedirectToAction(nameof(Preview), new { id });
        }

        await LoadSendContextAsync();
        return View(new PollSendViewModel
        {
            PollSurveyId = survey.Id,
            Title = survey.Title,
            SendNow = true,
            ScheduledAt = ViewBag.AgentNow is DateTime now ? now.AddMinutes(5) : DateTime.Now.AddMinutes(5)
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(PollSendViewModel model)
    {
        var gate = await RequirePollAccessAsync();
        if (gate != null) return gate;

        var survey = await _db.PollSurveys.FirstOrDefaultAsync(p => p.Id == model.PollSurveyId && p.AgentUserId == AgentId);
        if (survey == null) return NotFound();

        ValidateSendRequest(model);
        if (!ModelState.IsValid)
        {
            await LoadSendContextAsync();
            model.Title = survey.Title;
            return View(model);
        }

        var agentTimeZone = await GetAgentTimeZoneAsync();
        var localSendAt = model.SendNow
            ? AgentTimeZoneHelper.FromUtc(DateTime.UtcNow.AddMinutes(1), agentTimeZone)
            : model.ScheduledAt!.Value;

        var audienceLabel = model.AudienceType switch
        {
            NewsLetterAudienceType.AccountType => "Account type",
            NewsLetterAudienceType.IndividualClient => "One individual client",
            _ => "All newsletter subscribers"
        };
        if (model.AudienceType == NewsLetterAudienceType.AccountType && model.ClientCategoryId.HasValue)
        {
            var category = await _db.ClientCategories.FirstOrDefaultAsync(c => c.Id == model.ClientCategoryId.Value);
            if (category != null) audienceLabel = $"Account type: {category.Name}";
        }

        var send = new PollSend
        {
            PollSurveyId = survey.Id,
            AgentUserId = AgentId,
            AudienceType = model.AudienceType,
            AudienceLabel = audienceLabel,
            ClientCategoryId = model.AudienceType == NewsLetterAudienceType.AccountType ? model.ClientCategoryId : null,
            ClientId = model.AudienceType == NewsLetterAudienceType.IndividualClient ? model.ClientId : null,
            Status = PollSendStatus.Scheduled,
            ScheduledAt = AgentTimeZoneHelper.ToUtc(localSendAt, agentTimeZone)
        };
        _db.PollSends.Add(send);

        survey.Status = PollSurveyStatus.Scheduled;
        survey.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        if (model.SendNow)
        {
            await _dispatcher.DispatchSendAsync(send.Id);
        }

        TempData["Success"] = model.SendNow
            ? "Poll sent."
            : $"Poll send scheduled for {localSendAt:MMM d, yyyy h:mm tt} {GetShortTimeZoneLabel(agentTimeZone)}.";
        return RedirectToAction(nameof(Preview), new { id = survey.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelSend(int sendId, int pollSurveyId)
    {
        var gate = await RequirePollAccessAsync();
        if (gate != null) return gate;

        var send = await _db.PollSends.FirstOrDefaultAsync(s => s.Id == sendId && s.PollSurveyId == pollSurveyId && s.AgentUserId == AgentId);
        if (send == null || send.Status != PollSendStatus.Scheduled)
        {
            TempData["Error"] = "Scheduled poll send could not be cancelled.";
            return RedirectToAction(nameof(Preview), new { id = pollSurveyId });
        }

        send.Status = PollSendStatus.Cancelled;
        await _db.SaveChangesAsync();

        var hasOtherActiveSend = await _db.PollSends.AnyAsync(s => s.PollSurveyId == pollSurveyId && s.Id != sendId && s.Status != PollSendStatus.Cancelled);
        if (!hasOtherActiveSend)
        {
            var survey = await _db.PollSurveys.FirstOrDefaultAsync(p => p.Id == pollSurveyId);
            if (survey != null && survey.Status == PollSurveyStatus.Scheduled)
            {
                survey.Status = PollSurveyStatus.Draft;
                await _db.SaveChangesAsync();
            }
        }

        TempData["Success"] = "Scheduled poll send cancelled.";
        return RedirectToAction(nameof(Preview), new { id = pollSurveyId });
    }

    public async Task<IActionResult> Results(int id)
    {
        var gate = await RequirePollAccessAsync();
        if (gate != null) return gate;

        var survey = await _db.PollSurveys.FirstOrDefaultAsync(p => p.Id == id && p.AgentUserId == AgentId);
        if (survey == null) return NotFound();

        var questions = await _db.PollQuestions.Where(q => q.PollSurveyId == id).OrderBy(q => q.SortOrder).ToListAsync();
        var questionIds = questions.Select(q => q.Id).ToList();
        var options = await _db.PollOptions.Where(o => questionIds.Contains(o.PollQuestionId)).OrderBy(o => o.SortOrder).ToListAsync();
        var recipientIds = await _db.PollRecipients.Where(r => r.PollSurveyId == id).Select(r => r.Id).ToListAsync();
        var answerCounts = await _db.PollAnswers
            .Where(a => recipientIds.Contains(a.PollRecipientId))
            .GroupBy(a => a.PollOptionId)
            .Select(g => new { OptionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.OptionId, x => x.Count);

        ViewBag.Questions = questions;
        ViewBag.OptionsByQuestion = options.GroupBy(o => o.PollQuestionId).ToDictionary(g => g.Key, g => g.ToList());
        ViewBag.AnswerCounts = answerCounts;
        return View(survey);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var gate = await RequirePollAccessAsync();
        if (gate != null) return gate;

        var survey = await _db.PollSurveys.FirstOrDefaultAsync(p => p.Id == id && p.AgentUserId == AgentId);
        if (survey == null) return NotFound();
        if (survey.Status != PollSurveyStatus.Draft)
        {
            TempData["Error"] = "Only draft polls that have not been sent can be deleted.";
            return RedirectToAction(nameof(Preview), new { id });
        }

        var questions = await _db.PollQuestions.Where(q => q.PollSurveyId == id).ToListAsync();
        var questionIds = questions.Select(q => q.Id).ToList();
        var options = await _db.PollOptions.Where(o => questionIds.Contains(o.PollQuestionId)).ToListAsync();

        _db.PollOptions.RemoveRange(options);
        _db.PollQuestions.RemoveRange(questions);
        _db.PollSurveys.Remove(survey);
        await _db.SaveChangesAsync();

        TempData["Success"] = "Poll deleted.";
        return RedirectToAction(nameof(Index));
    }

    private async Task SaveQuestionsAsync(int surveyId, PollSurveyBuilderViewModel model)
    {
        var sortOrder = 0;
        foreach (var questionInput in model.Questions)
        {
            var text = questionInput.Text?.Trim() ?? string.Empty;
            var validOptions = (questionInput.Options ?? new List<string>())
                .Select(o => o?.Trim() ?? string.Empty)
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .ToList();
            if (string.IsNullOrWhiteSpace(text) || validOptions.Count < 2) continue;

            var question = new PollQuestion { PollSurveyId = surveyId, Text = text, SortOrder = sortOrder++ };
            _db.PollQuestions.Add(question);
            await _db.SaveChangesAsync();

            var optionOrder = 0;
            foreach (var optionText in validOptions)
            {
                _db.PollOptions.Add(new PollOption { PollQuestionId = question.Id, Text = optionText, SortOrder = optionOrder++ });
            }
            await _db.SaveChangesAsync();
        }
    }

    private bool ValidateBuilder(PollSurveyBuilderViewModel model)
    {
        model.Title = model.Title?.Trim() ?? string.Empty;
        model.Subject = model.Subject?.Trim() ?? string.Empty;
        model.IntroText = model.IntroText?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(model.Title))
        {
            ModelState.AddModelError(nameof(model.Title), "Poll title is required.");
        }
        if (string.IsNullOrWhiteSpace(model.Subject))
        {
            ModelState.AddModelError(nameof(model.Subject), "Email subject is required.");
        }

        var validQuestionCount = (model.Questions ?? new List<PollQuestionInput>())
            .Count(q => !string.IsNullOrWhiteSpace(q.Text) &&
                        (q.Options ?? new List<string>()).Count(o => !string.IsNullOrWhiteSpace(o)) >= 2);
        if (validQuestionCount == 0)
        {
            ModelState.AddModelError("", "Add at least one question with 2 or more answer options.");
        }

        return ModelState.IsValid;
    }

    private void ValidateSendRequest(PollSendViewModel model)
    {
        if (model.AudienceType == NewsLetterAudienceType.AccountType && !model.ClientCategoryId.HasValue)
        {
            ModelState.AddModelError(nameof(model.ClientCategoryId), "Choose an account type.");
        }

        if (model.AudienceType == NewsLetterAudienceType.IndividualClient && !model.ClientId.HasValue)
        {
            ModelState.AddModelError(nameof(model.ClientId), "Choose a client.");
        }

        if (!model.SendNow && !model.ScheduledAt.HasValue)
        {
            ModelState.AddModelError(nameof(model.ScheduledAt), "Choose a send date and time.");
        }
    }

    private async Task LoadSendContextAsync()
    {
        var subscribers = await _clients.GetNewsletterSubscribersAsync(AgentId);
        ViewBag.SubscriberCount = subscribers.Count();
        ViewBag.AccountTypes = await _db.ClientCategories.Where(c => c.AgentUserId == AgentId).OrderBy(c => c.Name).ToListAsync();
        ViewBag.Clients = await _db.Clients.Where(c => c.AgentUserId == AgentId && c.Email != "").OrderBy(c => c.LastName).ThenBy(c => c.FirstName).ToListAsync();

        var timeZone = await GetAgentTimeZoneAsync();
        ViewBag.AgentTimeZone = timeZone;
        ViewBag.AgentTimeZoneLabel = GetShortTimeZoneLabel(timeZone);
        ViewBag.AgentNow = AgentTimeZoneHelper.FromUtc(DateTime.UtcNow, timeZone);
    }

    private async Task<string> GetAgentTimeZoneAsync()
    {
        var agent = await _db.AgentUsers.FirstOrDefaultAsync(a => a.Id == AgentId);
        return AgentTimeZoneHelper.Normalize(agent?.TimeZone);
    }

    private static string GetShortTimeZoneLabel(string timeZone) => timeZone switch
    {
        "(GMT-06:00) Central Time (US & Canada)" => "Central",
        "(GMT-07:00) Mountain Time (US & Canada)" => "Mountain",
        "(GMT-08:00) Pacific Time (US & Canada)" => "Pacific",
        "(GMT-04:00) Atlantic Time (Canada)" => "Atlantic",
        "(GMT-03:30) Newfoundland" => "Newfoundland",
        _ => "Eastern"
    };

    private async Task<IActionResult?> RequirePollAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.PollSurveys);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Index", "Billing");
    }
}
