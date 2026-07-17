using System;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

public static class NewsLetterTemplateSeeder
{
    public static async Task SeedAsync(IPRODbContext db)
    {
        if (await db.NewsLetterTemplates.AnyAsync())
        {
            return;
        }

        db.NewsLetterTemplates.AddRange(
            new NewsLetterTemplate
            {
                Name = "Simple Announcement",
                Description = "A short, clean update for one piece of news.",
                Subject = "An update from your adviser",
                HtmlBody = "<h2>Here's a quick update</h2><p>I wanted to share some news with you. [Add your announcement here.]</p><p>As always, reach out if you have any questions.</p>",
                TextBody = "Here's a quick update.\n\nI wanted to share some news with you. [Add your announcement here.]\n\nAs always, reach out if you have any questions.",
                SortOrder = 10
            },
            new NewsLetterTemplate
            {
                Name = "Market Update",
                Description = "A structured layout for sharing market or industry commentary.",
                Subject = "Your monthly market update",
                HtmlBody = "<h2>Market Update</h2><p>Here's what's been happening this month and what it could mean for you.</p><h3>Key Highlights</h3><ul><li>[Highlight one]</li><li>[Highlight two]</li><li>[Highlight three]</li></ul><p>Let me know if you'd like to discuss how this affects your plans.</p>",
                TextBody = "Market Update\n\nHere's what's been happening this month and what it could mean for you.\n\nKey Highlights:\n- [Highlight one]\n- [Highlight two]\n- [Highlight three]\n\nLet me know if you'd like to discuss how this affects your plans.",
                SortOrder = 20
            },
            new NewsLetterTemplate
            {
                Name = "Thank You for Your Business",
                Description = "A warm note of appreciation for existing clients.",
                Subject = "Thank you for your continued trust",
                HtmlBody = "<h2>Thank You</h2><p>I wanted to take a moment to say thank you for your continued trust and business. It means a great deal to me.</p><p>If there's anything I can help you with, don't hesitate to reach out.</p>",
                TextBody = "Thank You\n\nI wanted to take a moment to say thank you for your continued trust and business. It means a great deal to me.\n\nIf there's anything I can help you with, don't hesitate to reach out.",
                SortOrder = 30
            },
            new NewsLetterTemplate
            {
                Name = "Seasonal Greeting",
                Description = "A friendly seasonal or holiday note to stay in touch.",
                Subject = "Season's greetings from our office",
                HtmlBody = "<h2>Wishing You a Wonderful Season</h2><p>As the season changes, I wanted to reach out and wish you and your family all the best.</p><p>Thank you for being a valued client. Here's to a great season ahead!</p>",
                TextBody = "Wishing You a Wonderful Season\n\nAs the season changes, I wanted to reach out and wish you and your family all the best.\n\nThank you for being a valued client. Here's to a great season ahead!",
                SortOrder = 40
            });

        await db.SaveChangesAsync();
    }
}
