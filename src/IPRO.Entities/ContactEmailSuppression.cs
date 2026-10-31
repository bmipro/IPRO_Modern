namespace IPRO.Entities;

// 624 (2026-10-31): a contact's opt-out, kept after the contact is deleted. It lived only on the contact, so
// an adviser who deleted someone who had unsubscribed and typed them in again started them over as if
// nothing had been said. CASL keeps the person's word, not the adviser's list. Holds no address: EmailHash
// is SHA-256 of the adviser's id and the canonical address (ContactOptOuts), which can answer "this one?"
// and nothing else. Goes with the adviser (AgentDataEraser).
public class ContactEmailSuppression
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public string EmailHash { get; set; } = string.Empty;
    public DateTime OptedOutAt { get; set; }
    public string Source { get; set; } = string.Empty;
}
