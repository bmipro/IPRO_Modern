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
    public const string EditorialVisualTemplateKey = "editorial-visual";

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

        if (!templates.Any(t => t.TemplateKey == EditorialVisualTemplateKey))
        {
            db.WebsiteTemplates.Add(CreateEditorialVisualTemplate());
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
        await db.SaveChangesAsync();
    }

    public static WebsiteTemplate CreateDefaultTemplate() => new()
    {
        TemplateKey = DefaultTemplateKey,
        Name = DefaultTemplateName,
        Description = "Clean professional website template for agent public sites.",
        BusinessType = string.Empty,
        PreviewImageUrl = string.Empty,
        LayoutJson = new WebsiteTemplateDesign
        {
            Renderer = "modern-professional",
            AccentColor = "#1457d9",
            BackgroundColor = "#f4f7fb",
            FontFamily = "'Segoe UI', Arial, Helvetica, sans-serif",
            HeaderStyle = "light",
            HeroLayout = "split",
            SectionSpacing = "spacious",
            ButtonStyle = "soft"
        }.ToLayoutJson(),
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
        LayoutJson = new WebsiteTemplateDesign
        {
            Renderer = "classic-sidebar",
            AccentColor = "#315b46",
            BackgroundColor = "#f5f3ed",
            FontFamily = "Georgia, 'Times New Roman', serif",
            HeaderStyle = "sidebar",
            HeroLayout = "split",
            SectionSpacing = "comfortable",
            ButtonStyle = "square"
        }.ToLayoutJson(),
        IsActive = true,
        IsDefault = false,
        CreatedAt = DateTime.UtcNow
    };

    public static WebsiteTemplate CreateEditorialVisualTemplate() => new()
    {
        TemplateKey = EditorialVisualTemplateKey,
        Name = "Editorial Visual",
        Description = "Image-led editorial presentation with generous typography and focused calls to action.",
        BusinessType = string.Empty,
        PreviewImageUrl = string.Empty,
        LayoutJson = new WebsiteTemplateDesign
        {
            Renderer = "editorial-visual",
            AccentColor = "#b42318",
            BackgroundColor = "#f7f5f0",
            FontFamily = "Georgia, 'Times New Roman', serif",
            HeaderStyle = "overlay",
            HeroLayout = "image-left",
            SectionSpacing = "spacious",
            ButtonStyle = "pill"
        }.ToLayoutJson(),
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
