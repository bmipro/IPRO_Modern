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
    // 601 (2026-10-24): when the sync read this calendar in full once after 601, so the events the old code
    // wrote (DueAt sent as UTC, so shifted by the adviser's offset) were found and re-written at the adviser's
    // own time. Null = not yet: the next run ignores the stored sync token for one full read
    // (GoogleCalendarSyncJob.PullGoogleChangesIntoIproAsync).
    public DateTime? LocalTimesResyncedAt { get; set; }
}
