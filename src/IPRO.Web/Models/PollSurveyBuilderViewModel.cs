namespace IPRO.Web.Models;

public class PollSurveyBuilderViewModel
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string IntroText { get; set; } = string.Empty;
    public List<PollQuestionInput> Questions { get; set; } = new();
}

public class PollQuestionInput
{
    public string Text { get; set; } = string.Empty;
    public List<string> Options { get; set; } = new();
}
