using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

public static class PublicFormBuilder
{
    // A Custom Form block silently skips rendering if its linked WebsiteForm has been deleted,
    // deactivated, or reassigned to a different agent - same "skip, don't error" behavior as
    // PollResultsBuilder skipping a missing survey.
    public static async Task<Dictionary<int, PublicFormBlockData>> BuildAsync(IPRODbContext db, int agentUserId, WebsitePage? currentPage)
    {
        var result = new Dictionary<int, PublicFormBlockData>();
        var formBlocks = currentPage?.Blocks.Where(b => b.BlockType == WebsiteBlockTypes.Form && b.IsVisible).ToList()
            ?? new List<WebsiteContentBlock>();
        if (formBlocks.Count == 0) return result;

        foreach (var block in formBlocks)
        {
            var settings = WebsiteFormSettings.FromJson(block.SettingsJson);
            if (settings.WebsiteFormId <= 0) continue;

            var data = await BuildForFormAsync(db, settings.WebsiteFormId, agentUserId);
            if (data == null) continue;

            result[block.Id] = data;
        }

        return result;
    }

    // Renders a form standalone (e.g. a Campaign step's link), independent of whether it's
    // embedded in any website page/block.
    public static async Task<PublicFormBlockData?> BuildForFormAsync(IPRODbContext db, int formId, int agentUserId)
    {
        var form = await db.WebsiteForms.FirstOrDefaultAsync(f => f.Id == formId && f.AgentUserId == agentUserId && f.IsActive);
        if (form == null) return null;

        var fields = await db.WebsiteFormFields.Where(f => f.WebsiteFormId == form.Id).OrderBy(f => f.SortOrder).ToListAsync();
        var fieldIds = fields.Select(f => f.Id).ToList();
        var options = await db.WebsiteFormFieldOptions.Where(o => fieldIds.Contains(o.WebsiteFormFieldId)).OrderBy(o => o.SortOrder).ToListAsync();

        var data = new PublicFormBlockData
        {
            WebsiteFormId = form.Id,
            Title = form.Title,
            Description = form.Description,
            SubmitButtonText = form.SubmitButtonText,
            SuccessMessage = form.SuccessMessage
        };
        foreach (var field in fields)
        {
            data.Fields.Add(new PublicFormField
            {
                FieldId = field.Id,
                FieldType = field.FieldType,
                Label = field.Label,
                Placeholder = field.Placeholder,
                HelpText = field.HelpText,
                IsRequired = field.IsRequired,
                Options = options.Where(o => o.WebsiteFormFieldId == field.Id).Select(o => o.Text).ToList()
            });
        }
        return data;
    }
}
