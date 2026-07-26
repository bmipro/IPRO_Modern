namespace IPRO.Web.Models;

public class PublicFormBlockData
{
    public int WebsiteFormId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SubmitButtonText { get; set; } = "Submit";
    public string SuccessMessage { get; set; } = "Thank you. Your response was sent.";
    public List<PublicFormField> Fields { get; set; } = new();
}

public class PublicFormField
{
    public int FieldId { get; set; }
    public string FieldType { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Placeholder { get; set; } = string.Empty;
    public string HelpText { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public List<string> Options { get; set; } = new();
}
