using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

public static class PollResultsBuilder
{
    private const int PollResultsMinResponses = 10;

    public static async Task<Dictionary<int, PollResultsBlockData>> BuildAsync(IPRODbContext db, int agentUserId, WebsitePage? currentPage)
    {
        var result = new Dictionary<int, PollResultsBlockData>();
        var pollBlocks = currentPage?.Blocks.Where(b => b.BlockType == WebsiteBlockTypes.PollResults && b.IsVisible).ToList()
            ?? new List<WebsiteContentBlock>();
        if (pollBlocks.Count == 0) return result;

        foreach (var block in pollBlocks)
        {
            var settings = WebsitePollResultsSettings.FromJson(block.SettingsJson);
            if (settings.PollSurveyId <= 0) continue;

            var survey = await db.PollSurveys.FirstOrDefaultAsync(s => s.Id == settings.PollSurveyId && s.AgentUserId == agentUserId);
            if (survey == null || survey.TotalResponded < PollResultsMinResponses) continue;

            var questions = await db.PollQuestions.Where(q => q.PollSurveyId == survey.Id).OrderBy(q => q.SortOrder).ToListAsync();
            var questionIds = questions.Select(q => q.Id).ToList();
            var options = await db.PollOptions.Where(o => questionIds.Contains(o.PollQuestionId)).OrderBy(o => o.SortOrder).ToListAsync();
            var recipientIds = await db.PollRecipients.Where(r => r.PollSurveyId == survey.Id).Select(r => r.Id).ToListAsync();
            var counts = await db.PollAnswers
                .Where(a => recipientIds.Contains(a.PollRecipientId))
                .GroupBy(a => a.PollOptionId)
                .Select(g => new { OptionId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.OptionId, x => x.Count);

            var data = new PollResultsBlockData { Title = survey.Title, TotalResponded = survey.TotalResponded };
            foreach (var question in questions)
            {
                var questionOptions = options.Where(o => o.PollQuestionId == question.Id).ToList();
                var questionTotal = questionOptions.Sum(o => counts.GetValueOrDefault(o.Id, 0));
                var questionData = new PollResultsQuestion { Text = question.Text };
                foreach (var option in questionOptions)
                {
                    var count = counts.GetValueOrDefault(option.Id, 0);
                    questionData.Options.Add(new PollResultsOption
                    {
                        Text = option.Text,
                        Count = count,
                        Percent = questionTotal > 0 ? (int)Math.Round(count * 100.0 / questionTotal) : 0
                    });
                }
                data.Questions.Add(questionData);
            }
            result[block.Id] = data;
        }

        return result;
    }
}
