namespace IPRO.Web.Models;

// 450: what a PollVote block needs to render -- the question(s) to vote on, whether this browser
// already voted, and whether the aggregate may be shown.
public class PollVoteBlockData
{
    public int BlockId { get; set; }
    public int SurveyId { get; set; }
    public string Title { get; set; } = string.Empty;
    public List<PollVoteQuestion> Questions { get; set; } = new();
    public bool HasVoted { get; set; }
    public bool ShowResults { get; set; }
    public PollResultsBlockData? Results { get; set; }
    public int WebsiteVotes { get; set; }
}

public class PollVoteQuestion
{
    public int Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public List<PollVoteOption> Options { get; set; } = new();
}

public class PollVoteOption
{
    public int Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

public class WebsitePollVoteSubmission
{
    public int SurveyId { get; set; }
    public int BlockId { get; set; }
    public string? ReturnPath { get; set; }
    public List<PollAnswerInput>? Answers { get; set; }
}
