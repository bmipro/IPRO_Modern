namespace IPRO.Web.Models;

// 529: the Pop-Up card on Website > Header & Menu, as posted to WebsitePagesController.SavePopup. Every value
// is checked and normalised there (WebsitePopup.Normalized); nothing here is trusted as it comes.
public class WebsitePopupForm
{
    public bool Enabled { get; set; }
    public string? Layout { get; set; }
    public string? Headline { get; set; }
    public string? Text { get; set; }
    public int? ImageAssetId { get; set; }
    public string? Action { get; set; }
    public string? Button1Text { get; set; }
    public string? Button1Url { get; set; }
    public string? Button2Text { get; set; }
    public string? Button2Url { get; set; }
    public string? Trigger { get; set; }
    public int DelaySeconds { get; set; } = 5;
    public int ScrollPercent { get; set; } = 50;
    public string? Pages { get; set; }
    public int RepeatDays { get; set; } = 7;
}
