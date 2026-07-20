namespace IPRO.Web.Models;

public class PollVoteSubmissionViewModel
{
    public string Token { get; set; } = string.Empty;
    public List<PollAnswerInput> Answers { get; set; } = new();
}

public class PollAnswerInput
{
    public int QuestionId { get; set; }
    public int OptionId { get; set; }
}
