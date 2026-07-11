using System.Linq;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;

namespace IPRO.DataAccess;

public static class WebsiteTemplateSeeder
{
    public const string DefaultTemplateName = "Professional Adviser";
    public const string DefaultTemplateKey = "modern-professional";
    public const string ClassicSidebarTemplateKey = "classic-sidebar";

    public static async Task SeedAsync(IPRODbContext db)
    {
        var templates = await db.WebsiteTemplates.ToListAsync();
        if (!templates.Any(t => t.TemplateKey == DefaultTemplateKey || t.Name == DefaultTemplateName))
        {
            var defaultTemplate = CreateDefaultTemplate();
            defaultTemplate.IsDefault = !templates.Any(t => t.IsDefault);
            db.WebsiteTemplates.Add(defaultTemplate);
        }

        if (!templates.Any(t => t.TemplateKey == ClassicSidebarTemplateKey))
        {
            db.WebsiteTemplates.Add(CreateClassicSidebarTemplate());
        }

        foreach (var template in templates.Where(t => string.IsNullOrWhiteSpace(t.TemplateKey)))
        {
            template.TemplateKey = NormalizeTemplateKey(template.Name);
        }

        if (!templates.Any(t => t.IsDefault))
        {
            var defaultTemplate = templates.FirstOrDefault(t => t.TemplateKey == DefaultTemplateKey)
                ?? templates.FirstOrDefault();
            if (defaultTemplate != null)
            {
                defaultTemplate.IsDefault = true;
            }
        }
        else
        {
            var firstDefault = templates
                .OrderByDescending(t => t.TemplateKey == DefaultTemplateKey)
                .ThenBy(t => t.Name)
                .First(t => t.IsDefault);
            foreach (var extraDefault in templates.Where(t => t.Id != firstDefault.Id && t.IsDefault))
            {
                extraDefault.IsDefault = false;
            }
        }

        await db.SaveChangesAsync();
    }

    public static WebsiteTemplate CreateDefaultTemplate() => new()
    {
        TemplateKey = DefaultTemplateKey,
        Name = DefaultTemplateName,
        Description = "Clean professional website template for agent public sites.",
        BusinessType = string.Empty,
        PreviewImageUrl = string.Empty,
        LayoutJson = "{}",
        IsActive = true,
        IsDefault = true,
        CreatedAt = DateTime.UtcNow
    };

    public static WebsiteTemplate CreateClassicSidebarTemplate() => new()
    {
        TemplateKey = ClassicSidebarTemplateKey,
        Name = "Classic Sidebar",
        Description = "Modernized version of the legacy template 14 layout with top navigation, banner, main content, and a right profile sidebar.",
        BusinessType = string.Empty,
        PreviewImageUrl = string.Empty,
        LayoutJson = "{}",
        IsActive = true,
        IsDefault = false,
        CreatedAt = DateTime.UtcNow
    };

    private static string NormalizeTemplateKey(string name)
    {
        var value = string.IsNullOrWhiteSpace(name) ? "template" : name.Trim().ToLowerInvariant();
        var chars = value.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}
