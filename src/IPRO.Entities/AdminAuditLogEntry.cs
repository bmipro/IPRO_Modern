namespace IPRO.Entities;

public class AdminAuditLogEntry
{
    public int Id { get; set; }
    public int AdminUserId { get; set; }
    public string AdminUsername { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
