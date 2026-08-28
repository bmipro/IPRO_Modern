using IPRO.Entities;

namespace IPRO.Web.Models;

public class PublicWebsiteViewModel
{
    public AgentWebsite Website { get; set; } = null!;
    public List<WebsitePage> Pages { get; set; } = new();
    public WebsitePage? CurrentPage { get; set; }

    // The site is live and rendering normally, but the requested slug is not one of its pages --
    // an ordinary 404 on a working website. Distinct from the "no published website for this host"
    // case, which never reaches this model and renders Views/PublicWebsite/NotFound.cshtml instead.
    // The two were the same screen until 2026-08-08, so a visitor who mistyped a URL on a live firm
    // website was told the website was not published and invited to sign in to the agent portal.
    public bool PageNotFound { get; set; }
    // The one origin every SEO surface names (canonical, og:url, structured data). Set by
    // PublicWebsiteController.ResolveCanonicalOriginAsync for real sites; previews leave it
    // empty and the SEO head falls back to the request origin (they are noindex anyway).
    public string CanonicalOrigin { get; set; } = string.Empty;
    public List<TestimonialSubmission> ApprovedTestimonials { get; set; } = new();
    public Dictionary<int, PollResultsBlockData> PollResultsByBlockId { get; set; } = new();
    public Dictionary<int, PublicFormBlockData> FormsByBlockId { get; set; } = new();
    public Dictionary<int, DidYouKnowBlockData> DidYouKnowByBlockId { get; set; } = new();
    public Dictionary<int, Article> ArticleContentByBlockId { get; set; } = new();
    public Dictionary<int, BlogBlockData> BlogByBlockId { get; set; } = new();
}
