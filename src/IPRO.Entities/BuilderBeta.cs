namespace IPRO.Entities;

// 585: the new website builder (the Template Builder) for iPro's customers, as a Beta. A SuperAdmin
// turns it on for one adviser at a time; that adviser then sees "Try the new builder (Beta)" under My
// Website. What they make there comes back as ordinary pages, so the page editor keeps working and the
// previous site can be put back.
//
// One row per adviser who was ever offered it, keyed by the adviser. No navigation on AgentUser: nothing
// that loads an adviser should touch this table. Goes with the account (AgentDataEraser), and the
// builder is told to erase the adviser's draft (BuilderTenantErasure).
public class AgentBuilderBeta
{
    public int AgentUserId { get; set; }
    public bool Enabled { get; set; }
    // The owner's decision 3 C (2026-10-17): iPro's staff publish what an adviser sends, until a
    // SuperAdmin ticks this for that adviser; from then on the adviser reviews and publishes it.
    public bool AdviserPublishes { get; set; }
    // The name the builder knows this adviser by: "t_" and 32 random hexadecimal digits, made once and
    // never changed. Not the adviser's id: the builder holds nothing that points into iPro.
    public string Tenant { get; set; } = string.Empty;
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
    public string ChangedBy { get; set; } = string.Empty;
    // When the adviser last opened the builder (a pass traded for a session). The builder keeps a draft
    // for BuilderBeta.KeepDays after the last visit and erases it then; My Website counts the same days
    // from here, so "Continue my draft" is offered exactly while the builder still holds one.
    public DateTime? LastOpenedAt { get; set; }
}

public static class WebsiteBuilderSessionKinds
{
    // The one-time pass My Website gives the adviser's browser to carry to the builder: once, 10 minutes.
    public const string Pass = "pass";
    // What the builder's server holds after trading the pass: up to 12 hours, ended sooner by iPro.
    public const string Session = "session";
}

// 585: a pass or a session (CONTRACT.md section 11). Only the SHA-256 of the token is kept, so a copy
// of the database opens nothing. HandoverId: the hand-over the session works on ("" until the builder
// fetches a fresh one). StartFresh: the adviser chose to start again from the live site.
public class WebsiteBuilderSession
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public int AgentWebsiteId { get; set; }
    public string Kind { get; set; } = WebsiteBuilderSessionKinds.Pass;
    public string TokenSha256 { get; set; } = string.Empty;
    public bool StartFresh { get; set; }
    public string HandoverId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public DateTime? EndedAt { get; set; }
}

// 585: an adviser whose account was deleted, whose draft the builder still holds. A job asks the
// builder to erase it and keeps asking until it says it did. The tenant name is all it keeps: it
// points at nothing in iPro any more.
public class BuilderTenantErasure
{
    public int Id { get; set; }
    public string Tenant { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int Attempts { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public string LastError { get; set; } = string.Empty;
}
