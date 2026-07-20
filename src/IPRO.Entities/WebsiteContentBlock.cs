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
    public const string LeadMagnet = "LeadMagnet";

    public static readonly string[] All =
    {
        Hero, Text, Services, CallToAction, ContactForm, NewsletterSignup, TestimonialForm, PollResults, LeadMagnet
    };

    public static string DisplayName(string type) => type switch
    {
        TestimonialForm => "Testimonial Submission Form",
        PollResults => "Poll Results",
        LeadMagnet => "Lead Magnet Download",
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
