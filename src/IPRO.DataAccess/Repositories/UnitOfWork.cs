using IPRO.Entities;

namespace IPRO.DataAccess.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly IPRODbContext _context;

    public UnitOfWork(IPRODbContext context)
    {
        _context = context;
        AgentUsers       = new Repository<AgentUser>(_context);
        AgentWebsites    = new Repository<AgentWebsite>(_context);
        WebsiteTemplates = new Repository<WebsiteTemplate>(_context);
        Clients          = new Repository<Client>(_context);
        ClientCategories = new Repository<ClientCategory>(_context);
        ClientComments   = new Repository<ClientComment>(_context);
        Billings         = new Repository<Billing>(_context);
        BillingRules     = new Repository<BillingRule>(_context);
        Invoices         = new Repository<Invoice>(_context);
        NewsLetters      = new Repository<NewsLetter>(_context);
        NewsLetterArticles = new Repository<NewsLetterArticle>(_context);
        DripCampaigns    = new Repository<DripCampaign>(_context);
        DripCampaignSteps = new Repository<DripCampaignStep>(_context);
        Schedulers       = new Repository<Scheduler>(_context);
        Articles         = new Repository<Article>(_context);
        Coupons          = new Repository<Coupon>(_context);
        CalendarEvents   = new Repository<CalendarEvent>(_context);
        Testimonials     = new Repository<Testimonial>(_context);
        OperateLogs      = new Repository<OperateLog>(_context);
    }

    public IRepository<AgentUser>        AgentUsers        { get; }
    public IRepository<AgentWebsite>     AgentWebsites     { get; }
    public IRepository<WebsiteTemplate>  WebsiteTemplates  { get; }
    public IRepository<Client>           Clients           { get; }
    public IRepository<ClientCategory>   ClientCategories  { get; }
    public IRepository<ClientComment>    ClientComments    { get; }
    public IRepository<Billing>          Billings          { get; }
    public IRepository<BillingRule>      BillingRules      { get; }
    public IRepository<Invoice>          Invoices          { get; }
    public IRepository<NewsLetter>       NewsLetters       { get; }
    public IRepository<NewsLetterArticle> NewsLetterArticles { get; }
    public IRepository<DripCampaign>     DripCampaigns     { get; }
    public IRepository<DripCampaignStep> DripCampaignSteps { get; }
    public IRepository<Scheduler>        Schedulers        { get; }
    public IRepository<Article>          Articles          { get; }
    public IRepository<Coupon>           Coupons           { get; }
    public IRepository<CalendarEvent>    CalendarEvents    { get; }
    public IRepository<Testimonial>      Testimonials      { get; }
    public IRepository<OperateLog>       OperateLogs       { get; }

    public async Task<int> SaveChangesAsync() =>
        await _context.SaveChangesAsync();

    public void Dispose() => _context.Dispose();
}
