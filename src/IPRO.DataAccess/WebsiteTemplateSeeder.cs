using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;

namespace IPRO.DataAccess;

public static class WebsiteTemplateSeeder
{
    public const string DefaultTemplateName = "Professional Adviser";

    public static async Task SeedAsync(IPRODbContext db)
    {
        if (await db.WebsiteTemplates.AnyAsync(t => t.Name == DefaultTemplateName))
        {
            return;
        }

        db.WebsiteTemplates.Add(CreateDefaultTemplate());
        await db.SaveChangesAsync();
    }

    public static WebsiteTemplate CreateDefaultTemplate() => new()
    {
        Name = DefaultTemplateName,
        Description = "Clean professional website template for agent public sites.",
        PreviewImageUrl = string.Empty,
        LayoutJson = "{}",
        IsActive = true,
        CreatedAt = DateTime.UtcNow
    };
}
