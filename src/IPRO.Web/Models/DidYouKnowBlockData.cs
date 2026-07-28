namespace IPRO.Web.Models;

public class DidYouKnowTeaser
{
    public int ArticleId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Excerpt { get; set; } = string.Empty;
}

public class DidYouKnowBlockData
{
    public List<DidYouKnowTeaser> Teasers { get; set; } = new();
    public string LayoutStyle { get; set; } = "auto";
}
