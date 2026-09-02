namespace IPRO.Entities;

public static class WebsiteBlockTypes
{
    public const string Hero = "Hero";
    public const string Text = "Text";
    public const string Services = "Services";
    public const string CallToAction = "CallToAction";
    public const string ContactForm = "ContactForm";
    public const string NewsletterSignup = "NewsletterSignup";
    public const string TestimonialForm = "TestimonialForm";
    public const string PollResults = "PollResults";
    public const string PollVote = "PollVote";
    public const string LeadMagnet = "LeadMagnet";
    public const string Reviews = "Reviews";
    public const string AgentInfo = "AgentInfo";
    public const string Maps = "Maps";
    public const string Form = "Form";
    public const string DidYouKnow = "DidYouKnow";
    public const string ArticleContent = "ArticleContent";
    public const string Video = "Video";
    public const string Gallery = "Gallery";
    public const string Calculator = "Calculator";
    // Lists the current page's own child pages. Needs no settings -- it reads the page tree, so a
    // section landing page stays correct as pages are added or renamed under it.
    public const string SectionIndex = "SectionIndex";
    // 2026-08-28: the LISTING the product was missing. Articles already carried everything a
    // post needs; only an index of them did not exist, so showing ten posts meant ten pages
    // linked by hand.
    public const string Blog = "Blog";

    public static readonly string[] All =
    {
        Hero, Text, Services, CallToAction, ContactForm, NewsletterSignup, TestimonialForm, PollResults, PollVote, LeadMagnet, Reviews, AgentInfo, Maps, Form, DidYouKnow, ArticleContent, Video, Gallery, Calculator, SectionIndex, Blog
    };

    public static string DisplayName(string type) => type switch
    {
        TestimonialForm => "Testimonial Submission Form",
        PollResults => "Poll Results",
        PollVote => "Poll (Visitors Vote)",
        LeadMagnet => "Lead Magnet Download",
        Reviews => "Review Badge",
        AgentInfo => "Agent Info Card",
        Maps => "Map",
        Form => "Custom Form",
        DidYouKnow => "Did You Know Teaser",
        ArticleContent => "Article Page",
        Gallery => "Photo Gallery",
        Calculator => "Calculator",
        SectionIndex => "Sub-Page Links",
        Blog => "Blog",
        _ => System.Text.RegularExpressions.Regex.Replace(type, "([a-z])([A-Z])", "$1 $2")
    };
}

public class WebsiteContentBlock
{
    public int Id { get; set; }
    public int WebsitePageId { get; set; }
    public string BlockType { get; set; } = WebsiteBlockTypes.Text;
    public string Heading { get; set; } = string.Empty;
    public string Subheading { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string ButtonText { get; set; } = string.Empty;
    public string ButtonUrl { get; set; } = string.Empty;
    public string SettingsJson { get; set; } = "{}";
    public string LayoutVariant { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsVisible { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public WebsitePage WebsitePage { get; set; } = null!;
}
