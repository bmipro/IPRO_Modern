namespace IPRO.Entities;

public class PortalMessage
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public bool IsFromClient { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsReadByAgent { get; set; }
    public bool IsReadByClient { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Client Client { get; set; } = null!;
}
