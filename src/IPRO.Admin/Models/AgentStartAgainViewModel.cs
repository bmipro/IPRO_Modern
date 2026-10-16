using IPRO.Business.SiteImport;

namespace IPRO.Admin.Models;

// 584: the confirm screen of "Start the website again" (AgentsController.StartAgain).
public class AgentStartAgainViewModel
{
    public SiteStartAgainService.Plan Plan { get; set; } = null!;
    // What was refused on the last press, in plain words; empty on first opening.
    public IReadOnlyList<string> Problems { get; set; } = Array.Empty<string>();
    public string TypedUserName { get; set; } = string.Empty;
    public bool UnpublishOtherPackArticles { get; set; } = true;
    // The platform's time zone (AdminClock), for the dates the screen shows.
    public string? Zone { get; set; }
}
