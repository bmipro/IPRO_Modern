namespace IPRO.Web.Models;

public class PollResultsBlockData
{
    public string Title { get; set; } = string.Empty;
    public int TotalResponded { get; set; }
    public List<PollResultsQuestion> Questions { get; set; } = new();
}

public class PollResultsQuestion
{
    public string Text { get; set; } = string.Empty;
    public List<PollResultsOption> Options { get; set; } = new();
}

public class PollResultsOption
{
    public string Text { get; set; } = string.Empty;
    public int Count { get; set; }
    public int Percent { get; set; }
}
