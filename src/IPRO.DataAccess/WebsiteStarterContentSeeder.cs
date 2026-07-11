using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

public static class WebsiteStarterContentSeeder
{
    public static async Task SeedAsync(IPRODbContext db)
    {
        if (await db.WebsiteStarterPages.AnyAsync()) return;

        AddSet(db, "All", "your business", "professional services", new[]
        {
            "Personalized service", "Clear answers and practical support", "A convenient way to stay connected"
        });
        AddSet(db, "Insurance / Financial", "your financial future", "insurance and financial guidance", new[]
        {
            "Insurance planning", "Retirement and investment guidance", "Ongoing reviews and client support"
        });
        AddSet(db, "Mortgage", "your home financing goals", "mortgage advice and financing solutions", new[]
        {
            "First-time home buyers", "Renewals and refinancing", "Investment property financing"
        });
        AddSet(db, "Accountants", "your financial records and decisions", "accounting and business support", new[]
        {
            "Tax preparation and planning", "Bookkeeping and reporting", "Business advisory services"
        });

        await db.SaveChangesAsync();
    }

    private static void AddSet(IPRODbContext db, string businessType, string goal, string serviceDescription, string[] services)
    {
        db.WebsiteStarterPages.AddRange(
            Page(businessType, "Home", "home", true, 0,
                Block(WebsiteBlockTypes.Hero, $"Build confidence in {goal}", $"Professional {serviceDescription} with responsive, personal service.", "", "Contact us", "/contact", 0),
                Block(WebsiteBlockTypes.Services, "How we can help", "Solutions designed around your needs.", string.Join("\n", services), "", "", 1),
                Block(WebsiteBlockTypes.CallToAction, "Ready to start a conversation?", "Tell us what you are working toward and we will help you identify the next step.", "", "Get in touch", "/contact", 2)),
            Page(businessType, "About", "about", false, 1,
                Block(WebsiteBlockTypes.Text, "A relationship built around your needs", "Professional guidance should feel clear, useful, and personal.", "We take time to understand your priorities, explain your options, and stay connected as your needs change.", "", "", 0)),
            Page(businessType, "Services", "services", false, 2,
                Block(WebsiteBlockTypes.Services, "Services", $"Explore our {serviceDescription}.", string.Join("\n", services), "", "", 0),
                Block(WebsiteBlockTypes.CallToAction, "Need something specific?", "Contact us to discuss a solution designed for your situation.", "", "Contact us", "/contact", 1)),
            Page(businessType, "Contact", "contact", false, 3,
                Block(WebsiteBlockTypes.ContactForm, "Let us connect", "Send an email or call to begin the conversation.", "We look forward to learning more about your goals.", "Send an email", "", 0)));
    }

    private static WebsiteStarterPage Page(string businessType, string title, string slug, bool isHome, int order, params WebsiteStarterBlock[] blocks) => new()
    {
        BusinessType = businessType,
        Title = title,
        Slug = slug,
        NavigationLabel = title,
        MetaTitle = title,
        MetaDescription = $"{title} - professional service and support.",
        IsHomePage = isHome,
        ShowInNavigation = true,
        IsActive = true,
        SortOrder = order,
        Blocks = blocks.ToList()
    };

    private static WebsiteStarterBlock Block(string type, string heading, string subheading, string body, string buttonText, string buttonUrl, int order) => new()
    {
        BlockType = type,
        Heading = heading,
        Subheading = subheading,
        Body = body,
        ButtonText = buttonText,
        ButtonUrl = buttonUrl,
        SortOrder = order,
        IsVisible = true
    };
}
