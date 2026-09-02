using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// 450 (2026-09-02): what a PollVote block shows. The question(s) and options to vote on; whether
// this browser already voted (cookie); and whether the aggregate may be shown. Results show at
// once for a poll nobody was emailed (every response is anonymous, so a count of one reveals no
// one) and respect the ten-response anonymity floor when named clients were emailed. The owner's
// preview always sees the numbers.
public static class PollVoteBuilder
{
    public static async Task<Dictionary<int, PollVoteBlockData>> BuildAsync(IPRODbContext db, int agentUserId, WebsitePage? currentPage, IReadOnlySet<int> votedSurveyIds, bool isOwnerPreview = false)
    {
        var result = new Dictionary<int, PollVoteBlockData>();
        var blocks = currentPage?.Blocks.Where(b => b.BlockType == WebsiteBlockTypes.PollVote && b.IsVisible).ToList()
            ?? new List<WebsiteContentBlock>();
        if (blocks.Count == 0) return result;

        foreach (var block in blocks)
        {
            var settings = WebsitePollResultsSettings.FromJson(block.SettingsJson);
            if (settings.PollSurveyId <= 0) continue;

            var survey = await db.PollSurveys.AsNoTracking().FirstOrDefaultAsync(s => s.Id == settings.PollSurveyId && s.AgentUserId == agentUserId);
            if (survey == null) continue;

            var questions = await db.PollQuestions.AsNoTracking().Where(q => q.PollSurveyId == survey.Id).OrderBy(q => q.SortOrder).ToListAsync();
            if (questions.Count == 0) continue;
            var questionIds = questions.Select(q => q.Id).ToList();
            var options = await db.PollOptions.AsNoTracking().Where(o => questionIds.Contains(o.PollQuestionId)).OrderBy(o => o.SortOrder).ToListAsync();

            var emailedAnyone = await db.PollRecipients.AsNoTracking().AnyAsync(r => r.PollSurveyId == survey.Id && r.Source == PollRecipientSource.Email);
            var websiteVotes = await db.PollRecipients.AsNoTracking().CountAsync(r => r.PollSurveyId == survey.Id && r.Source == PollRecipientSource.Website && r.Status == PollRecipientStatus.Responded);
            var showResults = isOwnerPreview
                || (survey.TotalResponded > 0 && (!emailedAnyone || survey.TotalResponded >= PollResultsBuilder.PollResultsMinResponses));

            var data = new PollVoteBlockData
            {
                BlockId = block.Id,
                SurveyId = survey.Id,
                Title = survey.Title,
                HasVoted = votedSurveyIds.Contains(survey.Id),
                ShowResults = showResults,
                WebsiteVotes = websiteVotes,
                Results = showResults ? await PollResultsBuilder.AggregateAsync(db, survey) : null
            };
            foreach (var q in questions)
            {
                var qd = new PollVoteQuestion { Id = q.Id, Text = q.Text };
                foreach (var o in options.Where(o => o.PollQuestionId == q.Id))
                    qd.Options.Add(new PollVoteOption { Id = o.Id, Text = o.Text });
                data.Questions.Add(qd);
            }
            result[block.Id] = data;
        }
        return result;
    }
}
