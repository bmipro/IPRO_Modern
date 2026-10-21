namespace IPRO.Entities;

/// <summary>
/// 592: the value one adviser publishes (TXT _ipro-verify) to prove one domain is theirs, kept per adviser and
/// domain so that an adviser can be given their value for a name another account holds unproven before they have a
/// row for it (the proof-first takeover in WebsiteController). Forgotten with the row that used it: a removed
/// domain's record proves nothing afterwards. Random; nothing in it can be worked out from the adviser or the
/// domain.
/// </summary>
public class DomainClaimToken
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }

    /// <summary>The name the record goes under, without "_ipro-verify." (DomainOwnershipProof.ProvenName).</summary>
    public string ProvenName { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
