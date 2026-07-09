using IPRO.Entities;

namespace IPRO.Web.Models;

public class NewsLetterSendViewModel
{
    public int NewsLetterId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public NewsLetterAudienceType AudienceType { get; set; } = NewsLetterAudienceType.AllSubscribers;
    public int? ClientCategoryId { get; set; }
    public int? ClientId { get; set; }
    public bool SendNow { get; set; } = true;
    public DateTime? ScheduledAt { get; set; }
}
