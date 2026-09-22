using System;

namespace IPRO.Entities;

// 512: where a self-registered adviser came from -- the page, the referring site and the campaign
// tags of the most recent public-page view by the same hashed visitor before they signed up. One
// row per sign-up through the public form, written at registration; the origin fields are empty
// when no earlier view matched (a direct visit, or one more than a month before: the visitor hash
// changes monthly, which is the point of it). Its own table, keyed by the agent, erased with them.
public class PlatformSignupOrigin
{
    public int AgentUserId { get; set; }
    public string Host { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string ReferrerHost { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Medium { get; set; } = string.Empty;
    public string Campaign { get; set; } = string.Empty;
    public DateTime? FirstSeenAt { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    public AgentUser AgentUser { get; set; } = null!;
}
