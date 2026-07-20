namespace IPRO.Entities;

public class PollAnswer
{
    public int Id { get; set; }
    public int PollRecipientId { get; set; }
    public int PollQuestionId { get; set; }
    public int PollOptionId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
