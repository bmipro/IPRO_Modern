namespace IPRO.Entities;

// FieldLabel/FieldType are snapshotted at submission time (not read live off WebsiteFormField) so a
// later rename or deletion of the form/field never corrupts what a past visitor actually saw and answered.
public class WebsiteFormSubmissionAnswer
{
    public int Id { get; set; }
    public int WebsiteLeadId { get; set; }
    public int WebsiteFormId { get; set; }
    public int WebsiteFormFieldId { get; set; }
    public string FieldLabel { get; set; } = string.Empty;
    public string FieldType { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
