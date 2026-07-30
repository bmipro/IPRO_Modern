using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

public static class WebsiteStarterContentSeeder
{
    // Guarded by SeedGuard: this same check-then-insert shape, run by both apps against the same
    // shared database on every deploy, is what produced a duplicate-key crash elsewhere in this
    // seeder family on 2026-07-29. This table has no unique constraint, so an unguarded race here
    // would not crash -- it would silently double every starter page instead. See SeedGuard.
    public static async Task SeedAsync(IPRODbContext db, Microsoft.Extensions.Logging.ILogger? logger = null) =>
        await SeedGuard.RunAsync(db, "WebsiteStarterPages", logger, async () =>
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
        });

    // Nav v2: Services is retired from the default starter-page set (a future "Resources" nav
    // mechanism takes over that role) and replaced with Testimonials / Free Newsletter / Request
    // Meeting. Separately guarded from SeedAsync above -- that one is already fully seeded in
    // production and its body never runs again, so this needs its own one-time run.
    public static async Task SeedNavV2AdditionsAsync(IPRODbContext db, Microsoft.Extensions.Logging.ILogger? logger = null) =>
        await SeedGuard.RunAsync(db, "WebsiteStarterPages_NavV2", logger, async () =>
        {
            if (await db.WebsiteStarterPages.AnyAsync(p => p.Slug == "testimonials")) return;

            foreach (var servicesPage in await db.WebsiteStarterPages.Where(p => p.Slug == "services").ToListAsync())
            {
                servicesPage.IsActive = false;
            }

            AddNavV2Set(db, "All", "your business", "professional services");
            AddNavV2Set(db, "Insurance / Financial", "your financial future", "insurance and financial guidance");
            AddNavV2Set(db, "Mortgage", "your home financing goals", "mortgage advice and financing solutions");
            AddNavV2Set(db, "Accountants", "your financial records and decisions", "accounting and business support");

            await db.SaveChangesAsync();
        });

    private static void AddNavV2Set(IPRODbContext db, string businessType, string goal, string serviceDescription)
    {
        db.WebsiteStarterPages.AddRange(
            Page(businessType, "Testimonials", "testimonials", false, 2,
                Block(WebsiteBlockTypes.TestimonialForm, "What clients say", $"Real feedback from people we've helped with {goal}.", "Have you worked with us? We would love to hear about your experience.", "", "", 0)),
            Page(businessType, "Free Newsletter", "free-newsletter", false, 4,
                Block(WebsiteBlockTypes.NewsletterSignup, "Stay in the loop", "Get occasional updates on topics that matter to you.", $"Sign up for our free newsletter covering {serviceDescription} and more.", "", "", 0)),
            Page(businessType, "Request Meeting", "request-meeting", false, 5,
                Block(WebsiteBlockTypes.ContactForm, "Request a meeting", $"Book time to talk through {goal}.", "Share a few details and we will follow up to find a time that works.", "Request a meeting", "", 0)));
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
