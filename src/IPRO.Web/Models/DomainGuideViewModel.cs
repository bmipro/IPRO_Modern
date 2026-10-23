using IPRO.Entities;
using IPRO.Utility;

namespace IPRO.Web.Models;

// 595: one domain's setup guide on My Website (Views/Website/_DomainGuide.cshtml). Open: shown when the page opens (the
// domain just added, or one not live yet). Focus: the domain just added, whose open step takes the keyboard focus.
public sealed record DomainGuideViewModel(AgentDomain Domain, DomainSetupGuide.Guide Guide, bool Open, bool Focus,
    DomainSetupGuide.Settings Settings, string TemporaryDomain);
