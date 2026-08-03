namespace IPRO.Entities;

public class WebsiteStarterForm
{
    public int Id { get; set; }
    public string BusinessType { get; set; } = "All";
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SubmitButtonText { get; set; } = "Submit";
    public string SuccessMessage { get; set; } = "Thank you. Your response was sent.";
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class WebsiteStarterFormField
{
    public int Id { get; set; }
    public int WebsiteStarterFormId { get; set; }
    public string FieldType { get; set; } = WebsiteFormFieldTypes.Text;
    public string Label { get; set; } = string.Empty;
    public string Placeholder { get; set; } = string.Empty;
    public string HelpText { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class WebsiteStarterFormFieldOption
{
    public int Id { get; set; }
    public int WebsiteStarterFormFieldId { get; set; }
    public string Text { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}
