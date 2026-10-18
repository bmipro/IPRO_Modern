using IPRO.Business.SiteImport;
using IPRO.Entities;

namespace IPRO.Web.Models;

// 585: the page that carries the one-time pass to the builder (a form that sends itself).
public sealed record WebsiteBuilderOpenViewModel(string Action, string Pass);

// 585: an adviser's own review of what they sent from the builder. LookAddress ends in "&page=": the
// look at one page is that address and the page's own.
public sealed record WebsiteBuilderReviewViewModel(WebsiteBuilderImport Import, SiteImportPlan Plan, string LookAddress, bool SitePublished);

// 585: the "New builder (Beta)" card on My Website. Null State = the Beta is not on for this adviser.
public sealed record WebsiteBuilderCardViewModel(BuilderBeta.State State, bool Available, IReadOnlyList<string> ChangedSincePublish, int KeepDays, string TimeZone = "")
{
    // A day as the adviser reads it: their own time zone (AgentLocalTime), not the server's.
    public string Day(DateTime utc) =>
        IPRO.DataAccess.AgentLocalTime.FromUtc(utc, TimeZone).ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
}
