using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;

namespace IPRO.Web.Infrastructure;

// 496 (2026-09-18): one definition of "this adviser has a website, and it is published".
//
// Until now only the Publish button did this. Sign-up created the account and nothing else, so a
// new adviser had no site until they found My Website and pressed Publish -- while the home page
// and both landing pages said "your site is live from day one", the preview said "this becomes
// real the moment you sign up", and the welcome email's main button ("Open Your Temporary
// Website... you can use this temporary domain right away") opened a 404 for every new customer.
//
// Sign-up now calls this as well. It is safe before payment: PublicWebsiteController gates every
// public host on billing being active (IsPublicSiteGatedAsync), so an unpaid account's site answers
// 404 until the subscription starts and is live within two minutes of it starting. Idempotent: an
// existing site is published, and the two helpers below only ever add what is missing.
public static class WebsiteProvisioning
{
    public static async Task<AgentWebsite> EnsurePublishedAsync(IPRODbContext db, IWebsiteService websites, AgentUser agent)
    {
        var site = await websites.GetByAgentIdAsync(agent.Id);
        if (site == null)
        {
            var template = await websites.EnsureDefaultTemplateForPackageAsync(agent.PackageId, agent.BusinessType);
            site = await websites.CreateAsync(new AgentWebsite
            {
                AgentUserId = agent.Id,
                TemplateId = template.Id,
                SiteTitle = DefaultSiteTitle(agent),
                TagLine = "Professional service and client support.",
                ThemeColor = "#1457d9",
                IsPublished = true
            });
        }
        else if (!site.IsPublished)
        {
            await websites.PublishAsync(agent.Id);
        }

        await WebsiteStarterPagesHelper.EnsureStarterPagesAsync(db, site, agent.Id);
        await WebsiteStarterResourcesHelper.EnsureResourcesAsync(db, site, agent.Id);
        return site;
    }

    public static string DefaultSiteTitle(AgentUser agent)
    {
        var fullName = string.Join(" ", new[] { agent.FirstName, agent.LastName }
            .Where(part => !string.IsNullOrWhiteSpace(part)))
            .Trim();
        if (!string.IsNullOrWhiteSpace(fullName)) return fullName;
        return string.IsNullOrWhiteSpace(agent.CompanyName) ? "IPRO Advisers" : agent.CompanyName;
    }
}
