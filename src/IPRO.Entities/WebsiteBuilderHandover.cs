namespace IPRO.Entities;

// 572: a customer's site handed to the Template Builder (export contract v0.4, section 9). One row
// per package iPro makes. It is what a returning package is checked against: the hand-over's id, the
// section types it offered, the articles and forms it listed, the pages it carried and the images it
// delivered (so an unchanged picture is linked again instead of stored twice).
//
// No navigation properties and no foreign keys: the row is a record of an operation and goes with
// the adviser (AgentDataEraser). The package itself is not kept, and of the session token only a
// hash is.
public class WebsiteBuilderHandover
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public int AgentWebsiteId { get; set; }
    // "h_" and 16 hexadecimal digits; a new one for every package.
    public string HandoverId { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public DateTime SessionExpiresAt { get; set; }
    public string SessionTokenSha256 { get; set; } = string.Empty;
    public string IssuedBy { get; set; } = string.Empty;
    public string TemplateName { get; set; } = string.Empty;
    public string PackageSha256 { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public int ImageCount { get; set; }
    // JSON lists: the section types offered; the ids of the published articles and active forms
    // listed; the pages carried ({ Id, Slug }); the images delivered ({ Ref, Url, Sha256, Bytes }).
    public string AllowedBlockTypesJson { get; set; } = "[]";
    public string ArticleIdsJson { get; set; } = "[]";
    public string FormIdsJson { get; set; } = "[]";
    public string PagesJson { get; set; } = "[]";
    public string DeliveredJson { get; set; } = "[]";
    // What was left out or tidied on the way (a button with no link, for one), in plain sentences.
    public string NotesJson { get; set; } = "[]";
    // 574: the site's logo address at the hand-over. A logo changed in iPro since is said at the return.
    public string LogoUrl { get; set; } = string.Empty;
    // 576: the sections the builder does not carry (a calculator, a gallery, a price list), each as
    // it was at the hand-over. In the package such a section is a locked box that carries only its
    // number; when the box comes back, the section is written from this record. A JSON list of
    // HandoverHostSection. Null on a row made before host sections existed.
    public string? HostSectionsJson { get; set; }
}
