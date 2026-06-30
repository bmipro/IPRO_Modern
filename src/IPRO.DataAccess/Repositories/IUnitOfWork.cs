#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Entities;

namespace IPRO.DataAccess.Repositories;

public interface IUnitOfWork : IAsyncDisposable, IDisposable
{
    IRepository<AgentUser> AgentUsers { get; }
    IRepository<AgentWebsite> AgentWebsites { get; }
    IRepository<WebsiteTemplate> WebsiteTemplates { get; }
    IRepository<Client> Clients { get; }
    IRepository<ClientCategory> ClientCategories { get; }
    IRepository<ClientComment> ClientComments { get; }
    IRepository<Billing> Billings { get; }
    IRepository<BillingRule> BillingRules { get; }
    IRepository<Invoice> Invoices { get; }
    IRepository<NewsLetter> NewsLetters { get; }
    IRepository<NewsLetterArticle> NewsLetterArticles { get; }
    IRepository<DripCampaign> DripCampaigns { get; }
    IRepository<DripCampaignStep> DripCampaignSteps { get; }
    IRepository<Scheduler> Schedulers { get; }
    IRepository<Article> Articles { get; }
    IRepository<Coupon> Coupons { get; }
    IRepository<CalendarEvent> CalendarEvents { get; }
    IRepository<Testimonial> Testimonials { get; }
    IRepository<OperateLog> OperateLogs { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}