namespace IPRO.Entities;

public class GoogleCalendarConnection
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public string GoogleAccountEmail { get; set; } = string.Empty;
    public string EncryptedAccessToken { get; set; } = string.Empty;
    public string EncryptedRefreshToken { get; set; } = string.Empty;
    public DateTime AccessTokenExpiresAt { get; set; }
    public string GoogleCalendarId { get; set; } = "primary";
    public string? SyncToken { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSyncedAt { get; set; }
}
