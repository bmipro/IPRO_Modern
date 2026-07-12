namespace IPRO.Entities;

public record WebsiteStarterBanner(string FileName, string Name, string Category)
{
    public string Url => $"/images/starter-banners/{FileName}";
}

public static class WebsiteStarterBannerCatalog
{
    public static IReadOnlyList<WebsiteStarterBanner> All { get; } = new List<WebsiteStarterBanner>
    {
        new("201042010145233395.jpg", "Family at home", "Family and lifestyle"),
        new("20104201094951770.jpg", "Peace of mind", "Family and lifestyle"),
        new("201062010115428234.jpg", "Happy family", "Family and lifestyle"),
        new("201062010133625656.jpg", "Planning for tomorrow", "Family and lifestyle"),
        new("201062010133643250.jpg", "Multi-generation family", "Family and lifestyle"),
        new("201062010133724296.jpg", "Family planning", "Family and lifestyle"),
        new("5header-image.jpg", "Young family", "Family and lifestyle"),
        new("agent_banner.jpg", "Family protection", "Family and lifestyle"),
        new("agent_banner5.jpg", "Mountain peace of mind", "Family and lifestyle"),
        new("allfamily.jpg", "Extended family", "Family and lifestyle"),
        new("family3grass.jpg", "Family outdoors", "Family and lifestyle"),
        new("familybaby.jpg", "New family", "Family and lifestyle"),
        new("familychinese.jpg", "Family generations", "Family and lifestyle"),
        new("sunset.jpg", "Couple outdoors", "Family and lifestyle"),
        new("top_banner_agent_r4.jpg", "Plan for tomorrow", "Family and lifestyle"),
        new("agreed.jpg", "Business agreement", "Business"),
        new("building1.jpg", "Modern office building", "Business"),
        new("building2.jpg", "City office towers", "Business"),
        new("building3.jpg", "Corporate skyline", "Business"),
        new("open.jpg", "Open for business", "Business"),
        new("puzzle.jpg", "Working together", "Business"),
        new("results.jpg", "Results", "Business"),
        new("right_wrong.jpg", "Making the right decision", "Business"),
        new("thumbup.jpg", "Client approval", "Business"),
        new("getinsurance.jpg", "Auto insurance", "Insurance"),
        new("banner.jpg", "Finding an adviser", "Adviser"),
        new("globe_people.jpg", "Global connections", "General"),
        new("hands.jpg", "Community hands", "General"),
        new("listening.jpg", "Listening to clients", "General")
    };
}
