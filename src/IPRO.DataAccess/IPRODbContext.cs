using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

public class IPRODbContext : DbContext
{
    public IPRODbContext(DbContextOptions<IPRODbContext> options) : base(options) { }

    public DbSet<AgentUser> AgentUsers => Set<AgentUser>();
    public DbSet<AgentWebsite> AgentWebsites => Set<AgentWebsite>();
    public DbSet<WebsiteTemplate> WebsiteTemplates => Set<WebsiteTemplate>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<ClientCategory> ClientCategories => Set<ClientCategory>();
    public DbSet<ClientComment> ClientComments => Set<ClientComment>();
    public DbSet<ClientFollowUp> ClientFollowUps => Set<ClientFollowUp>();
    public DbSet<Billing> Billings => Set<Billing>();
    public DbSet<BillingRule> BillingRules => Set<BillingRule>();
    public DbSet<PackageFeature> PackageFeatures => Set<PackageFeature>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<SubscriptionChange> SubscriptionChanges => Set<SubscriptionChange>();
    public DbSet<NewsLetter> NewsLetters => Set<NewsLetter>();
    public DbSet<NewsLetterArticle> NewsLetterArticles => Set<NewsLetterArticle>();
    public DbSet<DripCampaign> DripCampaigns => Set<DripCampaign>();
    public DbSet<DripCampaignStep> DripCampaignSteps => Set<DripCampaignStep>();
    public DbSet<Scheduler> Schedulers => Set<Scheduler>();
    public DbSet<Article> Articles => Set<Article>();
    public DbSet<Coupon> Coupons => Set<Coupon>();
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();
    public DbSet<Testimonial> Testimonials => Set<Testimonial>();
    public DbSet<OperateLog> OperateLogs => Set<OperateLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // AgentUser
        modelBuilder.Entity<AgentUser>(e =>
        {
            e.HasIndex(u => u.UserName).IsUnique();
            e.HasIndex(u => u.Email).IsUnique();
            e.HasIndex(u => u.DomainName).IsUnique();
            e.Property(u => u.UserName).HasMaxLength(100).IsRequired();
            e.Property(u => u.Email).HasMaxLength(200).IsRequired();
            e.Property(u => u.FirstName).HasMaxLength(80).IsRequired();
            e.Property(u => u.LastName).HasMaxLength(80).IsRequired();
            e.Property(u => u.Designation).HasMaxLength(80);
            e.Property(u => u.CompanyName).HasMaxLength(150).IsRequired();
            e.Property(u => u.CompanyAddress).HasMaxLength(200);
            e.Property(u => u.City).HasMaxLength(80).IsRequired();
            e.Property(u => u.Province).HasMaxLength(80).IsRequired();
            e.Property(u => u.PostalCode).HasMaxLength(20).IsRequired();
            e.Property(u => u.Country).HasMaxLength(80).IsRequired();
            e.Property(u => u.TimeZone).HasMaxLength(120);
            e.Property(u => u.Phone).HasMaxLength(40).IsRequired();
            e.Property(u => u.BusinessFax).HasMaxLength(40);
            e.Property(u => u.CellPhone).HasMaxLength(40);
            e.Property(u => u.BusinessType).HasMaxLength(80);
            e.Property(u => u.DomainName).HasMaxLength(255);
            e.Property(u => u.PromotionCode).HasMaxLength(80);
            e.Property(u => u.RegistrationIpAddress).HasMaxLength(64);
        });

        // AgentWebsite → AgentUser
        modelBuilder.Entity<AgentWebsite>(e =>
        {
            e.HasOne(w => w.AgentUser)
             .WithMany(u => u.Websites)
             .HasForeignKey(w => w.AgentUserId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(w => w.Template)
             .WithMany(t => t.Websites)
             .HasForeignKey(w => w.TemplateId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // Client → AgentUser
        modelBuilder.Entity<Client>(e =>
        {
            e.Property(c => c.FirstName).HasMaxLength(80).IsRequired();
            e.Property(c => c.LastName).HasMaxLength(80).IsRequired();
            e.Property(c => c.CompanyName).HasMaxLength(150);
            e.Property(c => c.Email).HasMaxLength(200).IsRequired();
            e.Property(c => c.Email2).HasMaxLength(200);
            e.Property(c => c.Phone).HasMaxLength(40);
            e.Property(c => c.HomePhone2).HasMaxLength(40);
            e.Property(c => c.BusinessPhone).HasMaxLength(40);
            e.Property(c => c.BusinessPhone2).HasMaxLength(40);
            e.Property(c => c.CellPhone).HasMaxLength(40);
            e.Property(c => c.CellPhone2).HasMaxLength(40);
            e.Property(c => c.Fax).HasMaxLength(40);
            e.Property(c => c.Fax2).HasMaxLength(40);
            e.Property(c => c.Address).HasMaxLength(200);
            e.Property(c => c.UnitNumber).HasMaxLength(40);
            e.Property(c => c.City).HasMaxLength(80);
            e.Property(c => c.Province).HasMaxLength(80);
            e.Property(c => c.PostalCode).HasMaxLength(20);
            e.Property(c => c.Country).HasMaxLength(80);

            e.HasOne(c => c.AgentUser)
             .WithMany(u => u.Clients)
             .HasForeignKey(c => c.AgentUserId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(c => c.Categories)
             .WithMany(cat => cat.Clients);
        });

        // ClientComment → Client
        modelBuilder.Entity<ClientComment>(e =>
        {
            e.HasOne(cc => cc.Client)
             .WithMany(c => c.Comments)
             .HasForeignKey(cc => cc.ClientId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ClientFollowUp>(e =>
        {
            e.Property(f => f.Title).HasMaxLength(160).IsRequired();
            e.Property(f => f.Notes).HasMaxLength(1000);

            e.HasOne(f => f.Client)
             .WithMany(c => c.FollowUps)
             .HasForeignKey(f => f.ClientId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // Billing → AgentUser, BillingRule
        modelBuilder.Entity<Billing>(e =>
        {
            e.HasOne(b => b.AgentUser)
             .WithMany(u => u.Billings)
             .HasForeignKey(b => b.AgentUserId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(b => b.BillingRule)
             .WithMany()
             .HasForeignKey(b => b.BillingRuleId)
             .OnDelete(DeleteBehavior.Restrict);

            e.Property(b => b.Amount).HasPrecision(10, 2);
        });

        // Invoice → Billing
        modelBuilder.Entity<Invoice>(e =>
        {
            e.HasOne(i => i.Billing)
             .WithMany(b => b.Invoices)
             .HasForeignKey(i => i.BillingId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(i => i.InvoiceNumber).IsUnique();
            e.Property(i => i.SubTotal).HasPrecision(10, 2);
            e.Property(i => i.TaxAmount).HasPrecision(10, 2);
            e.Property(i => i.Total).HasPrecision(10, 2);
        });

        modelBuilder.Entity<SubscriptionChange>(e =>
        {
            e.HasOne(c => c.AgentUser)
             .WithMany()
             .HasForeignKey(c => c.AgentUserId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(c => c.CurrentBillingRule)
             .WithMany()
             .HasForeignKey(c => c.CurrentBillingRuleId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(c => c.RequestedBillingRule)
             .WithMany()
             .HasForeignKey(c => c.RequestedBillingRuleId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(c => c.Billing)
             .WithMany()
             .HasForeignKey(c => c.BillingId)
             .OnDelete(DeleteBehavior.SetNull);

            e.Property(c => c.ProratedCredit).HasPrecision(10, 2);
            e.Property(c => c.ProratedCharge).HasPrecision(10, 2);
            e.Property(c => c.AmountDue).HasPrecision(10, 2);
            e.Property(c => c.Currency).HasMaxLength(3).IsRequired();
        });

        // NewsLetter → AgentUser
        modelBuilder.Entity<NewsLetter>(e =>
        {
            e.HasOne(n => n.AgentUser)
             .WithMany(u => u.NewsLetters)
             .HasForeignKey(n => n.AgentUserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // NewsLetterArticle → NewsLetter
        modelBuilder.Entity<NewsLetterArticle>(e =>
        {
            e.HasOne(a => a.NewsLetter)
             .WithMany(n => n.Articles)
             .HasForeignKey(a => a.NewsLetterId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // DripCampaign → Steps
        modelBuilder.Entity<DripCampaign>(e =>
        {
            e.HasMany(d => d.Steps)
             .WithOne(s => s.DripCampaign)
             .HasForeignKey(s => s.DripCampaignId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // BillingRule
        modelBuilder.Entity<BillingRule>(e =>
        {
            e.Property(r => r.MonthlyPrice).HasPrecision(10, 2);
            e.Property(r => r.QuarterlyPrice).HasPrecision(10, 2);
            e.Property(r => r.AnnualPrice).HasPrecision(10, 2);
        });

        modelBuilder.Entity<PackageFeature>(e =>
        {
            e.HasIndex(f => new { f.BillingRuleId, f.FeatureCode }).IsUnique();
            e.Property(f => f.FeatureCode).HasMaxLength(120).IsRequired();
            e.Property(f => f.FeatureName).HasMaxLength(180).IsRequired();
            e.Property(f => f.LimitLabel).HasMaxLength(120);
            e.HasOne(f => f.BillingRule)
             .WithMany(r => r.Features)
             .HasForeignKey(f => f.BillingRuleId)
             .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
