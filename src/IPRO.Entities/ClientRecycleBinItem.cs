namespace IPRO.Entities;

// 472 (2026-09-10): a deleted client, kept for 30 days as a faithful snapshot of every row the
// deletion removed, so the agent can restore it themselves. OriginalClientId is deliberately NOT
// named ClientId: the row must survive the client it describes, so it is not a client-linked table.
public class ClientRecycleBinItem
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public int OriginalClientId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTime DeletedAt { get; set; } = DateTime.UtcNow;
    public DateTime PurgeAfter { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string BlobUrlsJson { get; set; } = "[]";
}
