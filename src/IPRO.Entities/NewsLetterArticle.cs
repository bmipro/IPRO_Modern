namespace IPRO.Entities;

public class NewsLetterArticle
{
    public int Id { get; set; }
    public int NewsLetterId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public NewsLetter NewsLetter { get; set; } = null!;
}
