using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

public static class TemplatePreviewBuilder
{
    public static async Task<PublicWebsiteViewModel?> BuildAsync(IPRODbContext db, int agentUserId, int templateId, bool useDefaults)
    {
        var website = await db.AgentWebsites
            .AsNoTracking()
            .Include(w => w.AgentUser)
            .FirstOrDefaultAsync(w => w.AgentUserId == agentUserId);
        var template = await db.WebsiteTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId && t.IsActive);
        if (website == null || template == null) return null;

        var pages = await db.WebsitePages
            .AsNoTracking()
            .Where(p => p.AgentWebsiteId == website.Id && p.IsPublished)
            .Include(p => p.Blocks)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Title)
            .ToListAsync();

        foreach (var page in pages)
        {
            page.Title ??= string.Empty;
            page.NavigationLabel ??= string.Empty;
            page.Slug ??= string.Empty;
            foreach (var block in page.Blocks)
            {
                block.Heading ??= string.Empty;
                block.Subheading ??= string.Empty;
                block.Body ??= string.Empty;
                block.ImageUrl ??= string.Empty;
                block.ButtonText ??= string.Empty;
                block.ButtonUrl ??= string.Empty;
                block.SettingsJson ??= "{}";
            }
        }

        var previewWebsite = new AgentWebsite
        {
            Id = website.Id,
            AgentUserId = website.AgentUserId,
            TemplateId = template.Id,
            CustomDomain = website.CustomDomain ?? string.Empty,
            SiteTitle = website.SiteTitle ?? string.Empty,
            TagLine = website.TagLine ?? string.Empty,
            LogoUrl = website.LogoUrl ?? string.Empty,
            ThemeColor = useDefaults
                ? WebsiteTemplateDesign.FromTemplate(template).AccentColor
                : website.ThemeColor,
            FontFamilyOverride = useDefaults ? string.Empty : website.FontFamilyOverride,
            HeadingFontSizeOverride = useDefaults ? 0 : website.HeadingFontSizeOverride,
            BodyFontSizeOverride = useDefaults ? 0 : website.BodyFontSizeOverride,
            BackgroundColorOverride = useDefaults ? string.Empty : website.BackgroundColorOverride,
            ButtonStyleOverride = useDefaults ? string.Empty : website.ButtonStyleOverride,
            SectionSpacingOverride = useDefaults ? string.Empty : website.SectionSpacingOverride,
            HeroStyleOverride = useDefaults ? string.Empty : website.HeroStyleOverride,
            HeaderSettingsJson = string.IsNullOrWhiteSpace(website.HeaderSettingsJson) ? "{}" : website.HeaderSettingsJson,
            FooterSettingsJson = string.IsNullOrWhiteSpace(website.FooterSettingsJson) ? "{}" : website.FooterSettingsJson,
            IsPublished = website.IsPublished,
            CreatedAt = website.CreatedAt,
            UpdatedAt = website.UpdatedAt,
            AgentUser = website.AgentUser,
            Template = template
        };

        return new PublicWebsiteViewModel
        {
            Website = previewWebsite,
            Pages = pages,
            CurrentPage = pages.FirstOrDefault(p => p.IsHomePage) ?? pages.FirstOrDefault()
        };
    }
}
