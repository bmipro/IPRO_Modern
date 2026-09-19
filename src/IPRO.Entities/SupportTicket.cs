namespace IPRO.Entities;

public enum SupportTicketStatus { Open, InProgress, Resolved, Closed }

// 502: the badge printed the enum's name, so an adviser read "InProgress".
public static class SupportTicketStatusExtensions
{
    public static string ToDisplayText(this SupportTicketStatus status) => status switch
    {
        SupportTicketStatus.InProgress => "In progress",
        _ => status.ToString()
    };
}

public class SupportTicket
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public SupportTicketStatus Status { get; set; } = SupportTicketStatus.Open;
    public bool HasUnreadForAgent { get; set; }
    public bool HasUnreadForAdmin { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;

    public AgentUser AgentUser { get; set; } = null!;
    public ICollection<SupportTicketMessage> Messages { get; set; } = new List<SupportTicketMessage>();
}
