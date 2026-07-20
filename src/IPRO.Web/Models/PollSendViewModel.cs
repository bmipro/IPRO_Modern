using IPRO.Entities;

namespace IPRO.Web.Models;

public class PollSendViewModel
{
    public int PollSurveyId { get; set; }
    public string Title { get; set; } = string.Empty;
    public NewsLetterAudienceType AudienceType { get; set; } = NewsLetterAudienceType.AllSubscribers;
    public int? ClientCategoryId { get; set; }
    public int? ClientId { get; set; }
    public bool SendNow { get; set; } = true;
    public DateTime? ScheduledAt { get; set; }
}
