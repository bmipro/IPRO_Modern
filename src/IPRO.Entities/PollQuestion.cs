namespace IPRO.Entities;

public class PollQuestion
{
    public int Id { get; set; }
    public int PollSurveyId { get; set; }
    public string Text { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
