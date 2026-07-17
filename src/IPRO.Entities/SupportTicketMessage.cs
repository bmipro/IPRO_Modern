namespace IPRO.Entities;

public class SupportTicketMessage
{
    public int Id { get; set; }
    public int SupportTicketId { get; set; }
    public bool IsFromAdmin { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public SupportTicket SupportTicket { get; set; } = null!;
}
