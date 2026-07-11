namespace IPRO.Entities;

public class WebsiteStarterBlock
{
    public int Id { get; set; }
    public int WebsiteStarterPageId { get; set; }
    public string BlockType { get; set; } = WebsiteBlockTypes.Text;
    public string Heading { get; set; } = string.Empty;
    public string Subheading { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string ButtonText { get; set; } = string.Empty;
    public string ButtonUrl { get; set; } = string.Empty;
    public string SettingsJson { get; set; } = "{}";
    public int SortOrder { get; set; }
    public bool IsVisible { get; set; } = true;
    public WebsiteStarterPage WebsiteStarterPage { get; set; } = null!;
}
