namespace IPRO.Entities;

// 623 (2026-10-31): iPro's access at Google, owed a withdrawal after an adviser was deleted. The privacy
// policy says closing an account does what Disconnect does, and an account is closed in SuperAdmin, whose
// app cannot read the token: it is protected by the web app's keys. So the eraser leaves the protected
// token here and the web app's Google Calendar job makes the call, then deletes the row. Like
// BuilderTenantErasures it has no AgentUserId: it outlives the account it came from, for minutes.
public class GoogleRevocation
{
    public int Id { get; set; }
    public string EncryptedRefreshToken { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int Attempts { get; set; }
    public DateTime? LastAttemptAt { get; set; }
}
