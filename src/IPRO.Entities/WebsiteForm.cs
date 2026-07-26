namespace IPRO.Entities;

public static class WebsiteFormFieldTypes
{
    public const string Text = "Text";
    public const string Textarea = "Textarea";
    public const string CheckboxGroup = "CheckboxGroup";
    public const string Dropdown = "Dropdown";
    public const string Section = "Section";

    public static readonly string[] All = { Text, Textarea, CheckboxGroup, Dropdown, Section };

    public static string DisplayName(string type) => type switch
    {
        Text => "Text (single line)",
        Textarea => "Text (multi-line)",
        CheckboxGroup => "Checkboxes (select multiple)",
        Dropdown => "Dropdown (select one)",
        Section => "Section header",
        _ => type
    };

    public static bool SupportsOptions(string type) => type == CheckboxGroup || type == Dropdown;
}

public class WebsiteForm
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SubmitButtonText { get; set; } = "Submit";
    public string SuccessMessage { get; set; } = "Thank you. Your response was sent.";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class WebsiteFormField
{
    public int Id { get; set; }
    public int WebsiteFormId { get; set; }
    public string FieldType { get; set; } = WebsiteFormFieldTypes.Text;
    public string Label { get; set; } = string.Empty;
    public string Placeholder { get; set; } = string.Empty;
    public string HelpText { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class WebsiteFormFieldOption
{
    public int Id { get; set; }
    public int WebsiteFormFieldId { get; set; }
    public string Text { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}
