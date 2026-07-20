namespace IPRO.Entities;

public class PollOption
{
    public int Id { get; set; }
    public int PollQuestionId { get; set; }
    public string Text { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}
