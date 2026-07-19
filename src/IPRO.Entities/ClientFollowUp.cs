namespace IPRO.Entities;

public class ClientFollowUp
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime DueAt { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? GoogleEventId { get; set; }
    public Client Client { get; set; } = null!;
}
