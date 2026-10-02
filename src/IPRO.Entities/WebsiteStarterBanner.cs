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
        new("people-silhouettes.jpg", "People in silhouette", "Business"),
        new("getinsurance.jpg", "Auto insurance", "Insurance"),
        new("security-camera.jpg", "Security camera", "Insurance"),
        new("keeping-watch.jpg", "Keeping watch", "Insurance"),
        new("banner.jpg", "Finding an adviser", "Adviser"),
        new("globe_people.jpg", "Global connections", "General"),
        new("hands.jpg", "Community hands", "General"),
        new("listening.jpg", "Listening to clients", "General"),
        new("global-vision.jpg", "Global vision", "General"),
        new("angel-statue.jpg", "Angel statue", "General"),
        new("blue-globe.jpg", "Blue globe", "General"),
        new("glass-marble.jpg", "Glass marble", "General"),

        // 539 (2026-10-02): the owner's own folder of images, "for shared use among the agents": 27 of
        // its 34 (the rest were duplicates, one already here, two that cannot make a strip, and two
        // held for his word on the rights). Each was cut to the gallery's shape (3.6 : 1, like the
        // 640 x 178 banners above) and saved at up to twice that size; re-encoding dropped the cameras'
        // metadata. Two new groups below, plus the seven placed in their groups above.
        new("autumn-valley.jpg", "Autumn valley", "Nature and landscapes"),
        new("mountain-range.jpg", "Mountain range", "Nature and landscapes"),
        new("sandy-beach.jpg", "Sandy beach", "Nature and landscapes"),
        new("blue-sky.jpg", "Blue sky", "Nature and landscapes"),
        new("summer-clouds.jpg", "Summer clouds", "Nature and landscapes"),
        new("niagara-falls.jpg", "Niagara Falls", "Nature and landscapes"),
        new("frozen-shoreline.jpg", "Frozen shoreline", "Nature and landscapes"),
        new("grand-canyon.jpg", "Grand Canyon", "Nature and landscapes"),
        new("canyon-horizon.jpg", "Canyon horizon", "Nature and landscapes"),
        new("above-the-clouds.jpg", "Above the clouds", "Nature and landscapes"),
        new("whale-off-the-coast.jpg", "Whale off the coast", "Nature and landscapes"),
        new("country-road.jpg", "Country road", "Nature and landscapes"),
        new("pebbles.jpg", "Pebbles", "Nature and landscapes"),
        new("toronto-skyline.jpg", "Toronto skyline", "Cities and places"),
        new("toronto-harbour.jpg", "Toronto from the harbour", "Cities and places"),
        new("old-city-rooftops.jpg", "Old city rooftops", "Cities and places"),
        new("colourful-houses.jpg", "Colourful houses", "Cities and places"),
        new("working-harbour.jpg", "Working harbour", "Cities and places"),
        new("stairway.jpg", "The way up", "Cities and places"),
        new("colourful-bricks.jpg", "Colourful bricks", "Cities and places")
    };
}
