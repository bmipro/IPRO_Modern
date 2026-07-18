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
    IRepository<PackageFeature> PackageFeatures { get; }
    IRepository<ProvinceTaxRate> ProvinceTaxRates { get; }
    IRepository<Invoice> Invoices { get; }
    IRepository<InvoiceLineItem> InvoiceLineItems { get; }
    IRepository<SubscriptionChange> SubscriptionChanges { get; }
    IRepository<NewsLetter> NewsLetters { get; }
    IRepository<NewsLetterArticle> NewsLetterArticles { get; }
    IRepository<NewsLetterSend> NewsLetterSends { get; }
    IRepository<NewsLetterRecipient> NewsLetterRecipients { get; }
    IRepository<NewsLetterTemplate> NewsLetterTemplates { get; }
    IRepository<DripCampaign> DripCampaigns { get; }
    IRepository<DripCampaignStep> DripCampaignSteps { get; }
    IRepository<DripCampaignStepSend> DripCampaignStepSends { get; }
    IRepository<Scheduler> Schedulers { get; }
    IRepository<Article> Articles { get; }
    IRepository<Coupon> Coupons { get; }
    IRepository<CalendarEvent> CalendarEvents { get; }
    IRepository<Testimonial> Testimonials { get; }
    IRepository<OperateLog> OperateLogs { get; }
    IRepository<AdminUser> AdminUsers { get; }
    IRepository<AdminAuditLogEntry> AdminAuditLogEntries { get; }
    IRepository<SupportTicket> SupportTickets { get; }
    IRepository<SupportTicketMessage> SupportTicketMessages { get; }
    IRepository<PromotionCode> PromotionCodes { get; }
    IRepository<PromotionCodeRedemption> PromotionCodeRedemptions { get; }
    IRepository<ClientInvoice> ClientInvoices { get; }
    IRepository<ClientInvoiceLineItem> ClientInvoiceLineItems { get; }
    IRepository<RecurringInvoiceSchedule> RecurringInvoiceSchedules { get; }
    IRepository<RecurringInvoiceLineItem> RecurringInvoiceLineItems { get; }
    IRepository<PortalMessage> PortalMessages { get; }
    IRepository<PortalDocument> PortalDocuments { get; }
    IRepository<PortalAppointmentRequest> PortalAppointmentRequests { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
