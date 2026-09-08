namespace IPRO.Web.Models;

public class SocialLinksBlockData
{
    public string Style { get; set; } = "icons";
    public List<SocialLinkItem> Links { get; set; } = new();
}

public class SocialLinkItem
{
    public string Platform { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string IconClass { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}
