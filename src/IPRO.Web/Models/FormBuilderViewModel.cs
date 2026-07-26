namespace IPRO.Web.Models;

public class FormBuilderViewModel
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SubmitButtonText { get; set; } = "Submit";
    public string SuccessMessage { get; set; } = "Thank you. Your response was sent.";
    public List<FormFieldInput> Fields { get; set; } = new();
}

public class FormFieldInput
{
    public string FieldType { get; set; } = IPRO.Entities.WebsiteFormFieldTypes.Text;
    public string Label { get; set; } = string.Empty;
    public string Placeholder { get; set; } = string.Empty;
    public string HelpText { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public List<string> Options { get; set; } = new();
}
