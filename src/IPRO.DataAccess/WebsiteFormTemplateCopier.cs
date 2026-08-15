using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// Copies a WebsiteStarterForm into a real, independent WebsiteForm the agent owns. Extracted from
// FormsController.AdoptTemplate so signup provisioning (the Request Meeting page) and the agent's
// template gallery cannot drift apart in how a template becomes a form.
public static class WebsiteFormTemplateCopier
{
    public static async Task<WebsiteForm> CopyToAgentAsync(IPRODbContext db, WebsiteStarterForm template, int agentUserId)
    {
        var templateFields = await db.WebsiteStarterFormFields
            .Where(f => f.WebsiteStarterFormId == template.Id).OrderBy(f => f.SortOrder).ToListAsync();
        var templateFieldIds = templateFields.Select(f => f.Id).ToList();
        var templateOptions = await db.WebsiteStarterFormFieldOptions
            .Where(o => templateFieldIds.Contains(o.WebsiteStarterFormFieldId)).OrderBy(o => o.SortOrder).ToListAsync();

        var form = new WebsiteForm
        {
            AgentUserId = agentUserId,
            Title = template.Title,
            Description = template.Description,
            SubmitButtonText = template.SubmitButtonText,
            SuccessMessage = template.SuccessMessage,
            IsActive = true
        };
        db.WebsiteForms.Add(form);
        await db.SaveChangesAsync();

        foreach (var templateField in templateFields)
        {
            var field = new WebsiteFormField
            {
                WebsiteFormId = form.Id,
                FieldType = templateField.FieldType,
                Label = templateField.Label,
                Placeholder = templateField.Placeholder,
                HelpText = templateField.HelpText,
                IsRequired = templateField.IsRequired,
                SortOrder = templateField.SortOrder
            };
            db.WebsiteFormFields.Add(field);
            await db.SaveChangesAsync();

            foreach (var option in templateOptions.Where(o => o.WebsiteStarterFormFieldId == templateField.Id))
            {
                db.WebsiteFormFieldOptions.Add(new WebsiteFormFieldOption { WebsiteFormFieldId = field.Id, Text = option.Text, SortOrder = option.SortOrder });
            }
            await db.SaveChangesAsync();
        }

        return form;
    }
}
