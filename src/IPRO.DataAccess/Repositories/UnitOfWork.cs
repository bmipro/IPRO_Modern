#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess.Repositories;

public class UnitOfWork : IUnitOfWork, IDisposable, IAsyncDisposable
{
    private readonly IPRODbContext _context; // <-- CHANGED
    private bool _disposed;

    private IRepository<AgentUser>? _agentUsers;
    private IRepository<AgentWebsite>? _agentWebsites;
    private IRepository<WebsiteTemplate>? _websiteTemplates;
    private IRepository<Client>? _clients;
    private IRepository<ClientCategory>? _clientCategories;
    private IRepository<ClientComment>? _clientComments;
    private IRepository<Billing>? _billings;
    private IRepository<BillingRule>? _billingRules;
    private IRepository<PackageFeature>? _packageFeatures;
    private IRepository<ProvinceTaxRate>? _provinceTaxRates;
    private IRepository<Invoice>? _invoices;
    private IRepository<InvoiceLineItem>? _invoiceLineItems;
    private IRepository<SubscriptionChange>? _subscriptionChanges;
    private IRepository<NewsLetter>? _newsLetters;
    private IRepository<NewsLetterArticle>? _newsLetterArticles;
    private IRepository<NewsLetterSend>? _newsLetterSends;
    private IRepository<NewsLetterRecipient>? _newsLetterRecipients;
    private IRepository<NewsLetterTemplate>? _newsLetterTemplates;
    private IRepository<DripCampaign>? _dripCampaigns;
    private IRepository<DripCampaignStep>? _dripCampaignSteps;
    private IRepository<DripCampaignStepSend>? _dripCampaignStepSends;
    private IRepository<Scheduler>? _schedulers;
    private IRepository<Article>? _articles;
    private IRepository<Coupon>? _coupons;
    private IRepository<CalendarEvent>? _calendarEvents;
    private IRepository<Testimonial>? _testimonials;
    private IRepository<OperateLog>? _operateLogs;
    private IRepository<AdminUser>? _adminUsers;
    private IRepository<AdminAuditLogEntry>? _adminAuditLogEntries;
    private IRepository<SupportTicket>? _supportTickets;
    private IRepository<SupportTicketMessage>? _supportTicketMessages;

    public UnitOfWork(IPRODbContext context) // <-- CHANGED
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public IRepository<AgentUser> AgentUsers => _agentUsers ??= new Repository<AgentUser>(_context);
    public IRepository<AgentWebsite> AgentWebsites => _agentWebsites ??= new Repository<AgentWebsite>(_context);
    public IRepository<WebsiteTemplate> WebsiteTemplates => _websiteTemplates ??= new Repository<WebsiteTemplate>(_context);
    public IRepository<Client> Clients => _clients ??= new Repository<Client>(_context);
    public IRepository<ClientCategory> ClientCategories => _clientCategories ??= new Repository<ClientCategory>(_context);
    public IRepository<ClientComment> ClientComments => _clientComments ??= new Repository<ClientComment>(_context);
    public IRepository<Billing> Billings => _billings ??= new Repository<Billing>(_context);
    public IRepository<BillingRule> BillingRules => _billingRules ??= new Repository<BillingRule>(_context);
    public IRepository<PackageFeature> PackageFeatures => _packageFeatures ??= new Repository<PackageFeature>(_context);
    public IRepository<ProvinceTaxRate> ProvinceTaxRates => _provinceTaxRates ??= new Repository<ProvinceTaxRate>(_context);
    public IRepository<Invoice> Invoices => _invoices ??= new Repository<Invoice>(_context);
    public IRepository<InvoiceLineItem> InvoiceLineItems => _invoiceLineItems ??= new Repository<InvoiceLineItem>(_context);
    public IRepository<SubscriptionChange> SubscriptionChanges => _subscriptionChanges ??= new Repository<SubscriptionChange>(_context);
    public IRepository<NewsLetter> NewsLetters => _newsLetters ??= new Repository<NewsLetter>(_context);
    public IRepository<NewsLetterArticle> NewsLetterArticles => _newsLetterArticles ??= new Repository<NewsLetterArticle>(_context);
    public IRepository<NewsLetterSend> NewsLetterSends => _newsLetterSends ??= new Repository<NewsLetterSend>(_context);
    public IRepository<NewsLetterRecipient> NewsLetterRecipients => _newsLetterRecipients ??= new Repository<NewsLetterRecipient>(_context);
    public IRepository<NewsLetterTemplate> NewsLetterTemplates => _newsLetterTemplates ??= new Repository<NewsLetterTemplate>(_context);
    public IRepository<DripCampaign> DripCampaigns => _dripCampaigns ??= new Repository<DripCampaign>(_context);
    public IRepository<DripCampaignStep> DripCampaignSteps => _dripCampaignSteps ??= new Repository<DripCampaignStep>(_context);
    public IRepository<DripCampaignStepSend> DripCampaignStepSends => _dripCampaignStepSends ??= new Repository<DripCampaignStepSend>(_context);
    public IRepository<Scheduler> Schedulers => _schedulers ??= new Repository<Scheduler>(_context);
    public IRepository<Article> Articles => _articles ??= new Repository<Article>(_context);
    public IRepository<Coupon> Coupons => _coupons ??= new Repository<Coupon>(_context);
    public IRepository<CalendarEvent> CalendarEvents => _calendarEvents ??= new Repository<CalendarEvent>(_context);
    public IRepository<Testimonial> Testimonials => _testimonials ??= new Repository<Testimonial>(_context);
    public IRepository<OperateLog> OperateLogs => _operateLogs ??= new Repository<OperateLog>(_context);
    public IRepository<AdminUser> AdminUsers => _adminUsers ??= new Repository<AdminUser>(_context);
    public IRepository<AdminAuditLogEntry> AdminAuditLogEntries => _adminAuditLogEntries ??= new Repository<AdminAuditLogEntry>(_context);
    public IRepository<SupportTicket> SupportTickets => _supportTickets ??= new Repository<SupportTicket>(_context);
    public IRepository<SupportTicketMessage> SupportTicketMessages => _supportTicketMessages ??= new Repository<SupportTicketMessage>(_context);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) 
        => _context.SaveChangesAsync(cancellationToken);

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _context.Dispose();
        }
        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();
}
