namespace IPRO.Web.Models;

// 646: the short card iPro's address shows for a professional who keeps their own website.
public class OwnWebsiteCardViewModel
{
    public string BusinessName { get; set; } = string.Empty;
    public string? WebsiteUrl { get; set; }
    public string WebsiteHost { get; set; } = string.Empty;
    public string ClientSignInUrl { get; set; } = string.Empty;
}

// 646: My Website for a professional who keeps their own website.
public class OwnWebsitePortalViewModel
{
    public string? WebsiteUrl { get; set; }
    public string WebsiteHost { get; set; } = string.Empty;
    public string FormUrl { get; set; } = string.Empty;
    public string EmbedCode { get; set; } = string.Empty;
    public string TemporaryAddress { get; set; } = string.Empty;
}

// 646: the contact form a professional who keeps their own website places on it (a link, or inside one of their
// pages). Posted back to the same address; the answer is the same page, so nothing depends on a cookie (inside
// another site's page a browser sends iPro none).
public class LeadFormEmbedViewModel
{
    public string FormKey { get; set; } = string.Empty;
    public string BusinessName { get; set; } = string.Empty;
    public bool Sent { get; set; }
    public List<string> Problems { get; set; } = new();

    // What the visitor typed (kept when the form is sent back to them).
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Message { get; set; }
    public bool ConsentGiven { get; set; }

    // The protections the website's own forms carry: a hidden field only a robot fills, the time the form was
    // shown, and a small sum (its answer sealed in CaptchaToken, single-use).
    public string? HoneypotField { get; set; }
    public long FormStartedAt { get; set; }
    public string? CaptchaToken { get; set; }
    public string? CaptchaAnswer { get; set; }
    public int CaptchaLeft { get; set; }
    public int CaptchaRight { get; set; }
}
