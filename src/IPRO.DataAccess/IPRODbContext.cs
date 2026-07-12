using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

public class IPRODbContext : DbContext
{
    public IPRODbContext(DbContextOptions<IPRODbContext> options) : base(options) { }

    public DbSet<AgentUser> AgentUsers => Set<AgentUser>();
    public DbSet<AgentWebsite> AgentWebsites => Set<AgentWebsite>();
    public DbSet<AgentDomain> AgentDomains => Set<AgentDomain>();
    public DbSet<WebsiteTemplate> WebsiteTemplates => Set<WebsiteTemplate>();
    public DbSet<WebsitePage> WebsitePages => Set<WebsitePage>();
    public DbSet<WebsiteContentBlock> WebsiteContentBlocks => Set<WebsiteContentBlock>();
    public DbSet<WebsiteMediaAsset> WebsiteMediaAssets => Set<WebsiteMediaAsset>();
    public DbSet<WebsiteStarterPage> WebsiteStarterPages => Set<WebsiteStarterPage>();
    public DbSet<WebsiteStarterBlock> WebsiteStarterBlocks => Set<WebsiteStarterBlock>();
    public DbSet<WebsiteLead> WebsiteLeads => Set<WebsiteLead>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<ClientCategory> ClientCategories => Set<ClientCategory>();
    public DbSet<ClientComment> ClientComments => Set<ClientComment>();
    public DbSet<ClientFollowUp> ClientFollowUps => Set<ClientFollowUp>();
    public DbSet<Billing> Billings => Set<Billing>();
    public DbSet<BillingRule> BillingRules => Set<BillingRule>();
    public DbSet<PackageFeature> PackageFeatures => Set<PackageFeature>();
    public DbSet<ProvinceTaxRate> ProvinceTaxRates => Set<ProvinceTaxRate>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLineItem> InvoiceLineItems => Set<InvoiceLineItem>();
    public DbSet<SubscriptionChange> SubscriptionChanges => Set<SubscriptionChange>();
    public DbSet<NewsLetter> NewsLetters => Set<NewsLetter>();
    public DbSet<NewsLetterArticle> NewsLetterArticles => Set<NewsLetterArticle>();
    public DbSet<NewsLetterSend> NewsLetterSends => Set<NewsLetterSend>();
    public DbSet<NewsLetterRecipient> NewsLetterRecipients => Set<NewsLetterRecipient>();
    public DbSet<DripCampaign> DripCampaigns => Set<DripCampaign>();
    public DbSet<DripCampaignStep> DripCampaignSteps => Set<DripCampaignStep>();
    public DbSet<DripCampaignEnrollment> DripCampaignEnrollments => Set<DripCampaignEnrollment>();
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

        modelBuilder.Entity<AgentDomain>(e =>
        {
            e.HasIndex(d => d.DomainName).IsUnique();
            e.HasIndex(d => new { d.AgentUserId, d.IsPrimary });
            e.Property(d => d.DomainName).HasMaxLength(255).IsRequired();
            e.Property(d => d.RootDomain).HasMaxLength(255);
            e.Property(d => d.WwwDomain).HasMaxLength(255);
            e.Property(d => d.DnsTarget).HasMaxLength(255);
            e.Property(d => d.DnsStatus).HasMaxLength(40).IsRequired();
            e.Property(d => d.AzureBindingStatus).HasMaxLength(40).IsRequired();
            e.Property(d => d.SslStatus).HasMaxLength(40).IsRequired();
            e.Property(d => d.LastError).HasMaxLength(1000);

            e.HasOne(d => d.AgentUser)
             .WithMany()
             .HasForeignKey(d => d.AgentUserId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(d => d.AgentWebsite)
             .WithMany(w => w.Domains)
             .HasForeignKey(d => d.AgentWebsiteId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // Client → AgentUser
        modelBuilder.Entity<WebsitePage>(e =>
        {
            e.HasIndex(p => new { p.AgentWebsiteId, p.Slug }).IsUnique();
            e.Property(p => p.Title).HasMaxLength(160).IsRequired();
            e.Property(p => p.Slug).HasMaxLength(120).IsRequired();
            e.Property(p => p.NavigationLabel).HasMaxLength(100).IsRequired();
            e.Property(p => p.MetaTitle).HasMaxLength(180);
            e.Property(p => p.MetaDescription).HasMaxLength(320);
            e.HasOne(p => p.AgentWebsite)
             .WithMany(w => w.Pages)
             .HasForeignKey(p => p.AgentWebsiteId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(p => p.ParentPage)
             .WithMany(p => p.ChildPages)
             .HasForeignKey(p => p.ParentPageId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WebsiteContentBlock>(e =>
        {
            e.Property(b => b.BlockType).HasMaxLength(40).IsRequired();
            e.Property(b => b.Heading).HasMaxLength(220);
            e.Property(b => b.Subheading).HasMaxLength(500);
            e.Property(b => b.ImageUrl).HasMaxLength(1000);
            e.Property(b => b.ButtonText).HasMaxLength(100);
            e.Property(b => b.ButtonUrl).HasMaxLength(1000);
            e.HasOne(b => b.WebsitePage)
             .WithMany(p => p.Blocks)
             .HasForeignKey(b => b.WebsitePageId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WebsiteMediaAsset>(e =>
        {
            e.Property(a => a.OriginalFileName).HasMaxLength(255).IsRequired();
            e.Property(a => a.BlobUrl).HasMaxLength(1000).IsRequired();
            e.Property(a => a.ContentType).HasMaxLength(100).IsRequired();
            e.HasOne(a => a.AgentWebsite)
             .WithMany(w => w.MediaAssets)
             .HasForeignKey(a => a.AgentWebsiteId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WebsiteLead>(e =>
        {
            e.HasIndex(l => new { l.AgentUserId, l.IsRead, l.Status });
            e.Property(l => l.SubmissionType).HasMaxLength(30).IsRequired();
            e.Property(l => l.FirstName).HasMaxLength(80).IsRequired();
            e.Property(l => l.LastName).HasMaxLength(80);
            e.Property(l => l.Email).HasMaxLength(200).IsRequired();
            e.Property(l => l.Phone).HasMaxLength(40);
            e.Property(l => l.SourceDomain).HasMaxLength(255);
            e.Property(l => l.SourcePage).HasMaxLength(500);
            e.Property(l => l.Referrer).HasMaxLength(1000);
            e.Property(l => l.IpAddress).HasMaxLength(64);
            e.Property(l => l.Status).HasMaxLength(30).IsRequired();
            e.Property(l => l.ProcessingNote).HasMaxLength(500);
            e.HasOne(l => l.AgentUser).WithMany().HasForeignKey(l => l.AgentUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.AgentWebsite).WithMany().HasForeignKey(l => l.AgentWebsiteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.WebsitePage).WithMany().HasForeignKey(l => l.WebsitePageId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(l => l.Client).WithMany().HasForeignKey(l => l.ClientId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WebsiteStarterPage>(e =>
        {
            e.HasIndex(p => new { p.BusinessType, p.BillingRuleId, p.Slug }).IsUnique();
            e.Property(p => p.BusinessType).HasMaxLength(80).IsRequired();
            e.Property(p => p.Title).HasMaxLength(160).IsRequired();
            e.Property(p => p.Slug).HasMaxLength(120).IsRequired();
            e.Property(p => p.NavigationLabel).HasMaxLength(100).IsRequired();
            e.Property(p => p.MetaTitle).HasMaxLength(180);
            e.Property(p => p.MetaDescription).HasMaxLength(320);
            e.HasOne(p => p.BillingRule)
             .WithMany()
             .HasForeignKey(p => p.BillingRuleId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WebsiteStarterBlock>(e =>
        {
            e.Property(b => b.BlockType).HasMaxLength(40).IsRequired();
            e.Property(b => b.Heading).HasMaxLength(220);
            e.Property(b => b.Subheading).HasMaxLength(500);
            e.Property(b => b.ImageUrl).HasMaxLength(1000);
            e.Property(b => b.ButtonText).HasMaxLength(100);
            e.Property(b => b.ButtonUrl).HasMaxLength(1000);
            e.HasOne(b => b.WebsiteStarterPage)
             .WithMany(p => p.Blocks)
             .HasForeignKey(b => b.WebsiteStarterPageId)
             .OnDelete(DeleteBehavior.Cascade);
        });

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
            e.Property(i => i.TaxRate).HasPrecision(7, 4);
            e.Property(i => i.TaxRegion).HasMaxLength(80);
            e.Property(i => i.Total).HasPrecision(10, 2);
        });

        modelBuilder.Entity<InvoiceLineItem>(e =>
        {
            e.HasOne(i => i.Invoice)
             .WithMany(i => i.LineItems)
             .HasForeignKey(i => i.InvoiceId)
             .OnDelete(DeleteBehavior.Cascade);

            e.Property(i => i.Description).HasMaxLength(200).IsRequired();
            e.Property(i => i.Amount).HasPrecision(10, 2);
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

        modelBuilder.Entity<NewsLetterRecipient>(e =>
        {
            e.HasIndex(r => new { r.NewsLetterId, r.Email });
            e.HasIndex(r => r.NewsLetterSendId);
            e.HasIndex(r => r.SendGridMessageId);
            e.HasIndex(r => r.UnsubscribeToken);
            e.Property(r => r.Email).HasMaxLength(200).IsRequired();
            e.Property(r => r.RecipientName).HasMaxLength(160);
            e.Property(r => r.SendGridMessageId).HasMaxLength(200);
            e.Property(r => r.UnsubscribeToken).HasMaxLength(80);
            e.Property(r => r.LastEvent).HasMaxLength(80);
            e.Property(r => r.FailureReason).HasMaxLength(1000);

            e.HasOne(r => r.NewsLetter)
             .WithMany(n => n.Recipients)
             .HasForeignKey(r => r.NewsLetterId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(r => r.NewsLetterSend)
             .WithMany(s => s.Recipients)
             .HasForeignKey(r => r.NewsLetterSendId)
             .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(r => r.Client)
             .WithMany()
             .HasForeignKey(r => r.ClientId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<NewsLetterSend>(e =>
        {
            e.HasIndex(s => new { s.AgentUserId, s.ScheduledAt });
            e.HasIndex(s => s.ClientCategoryId);
            e.HasIndex(s => s.ClientId);
            e.Property(s => s.AudienceLabel).HasMaxLength(200).IsRequired();

            e.HasOne(s => s.NewsLetter)
             .WithMany(n => n.Sends)
             .HasForeignKey(s => s.NewsLetterId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(s => s.AgentUser)
             .WithMany()
             .HasForeignKey(s => s.AgentUserId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(s => s.ClientCategory)
             .WithMany()
             .HasForeignKey(s => s.ClientCategoryId)
             .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(s => s.Client)
             .WithMany()
             .HasForeignKey(s => s.ClientId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        // DripCampaign → Steps
        modelBuilder.Entity<DripCampaign>(e =>
        {
            e.HasMany(d => d.Steps)
             .WithOne(s => s.DripCampaign)
             .HasForeignKey(s => s.DripCampaignId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(d => d.Enrollments)
             .WithOne(s => s.DripCampaign)
             .HasForeignKey(s => s.DripCampaignId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DripCampaignEnrollment>(e =>
        {
            e.HasIndex(x => new { x.AgentUserId, x.Status, x.NextSendAt });
            e.HasIndex(x => new { x.DripCampaignId, x.ClientId, x.Status });
            e.Property(x => x.LastError).HasMaxLength(1000);

            e.HasOne(x => x.AgentUser)
             .WithMany()
             .HasForeignKey(x => x.AgentUserId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Client)
             .WithMany()
             .HasForeignKey(x => x.ClientId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.ClientCategory)
             .WithMany()
             .HasForeignKey(x => x.ClientCategoryId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        // BillingRule
        modelBuilder.Entity<BillingRule>(e =>
        {
            e.Property(r => r.MonthlyPrice).HasPrecision(10, 2);
            e.Property(r => r.QuarterlyPrice).HasPrecision(10, 2);
            e.Property(r => r.AnnualPrice).HasPrecision(10, 2);
            e.Property(r => r.SetupFee).HasPrecision(10, 2);
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

        modelBuilder.Entity<ProvinceTaxRate>(e =>
        {
            e.HasIndex(t => t.ProvinceCode).IsUnique();
            e.Property(t => t.ProvinceCode).HasMaxLength(10).IsRequired();
            e.Property(t => t.ProvinceName).HasMaxLength(80).IsRequired();
            e.Property(t => t.TaxLabel).HasMaxLength(80).IsRequired();
            e.Property(t => t.Rate).HasPrecision(7, 5);
        });

        modelBuilder.Entity<WebsiteTemplate>(e =>
        {
            e.HasIndex(t => t.TemplateKey).IsUnique();
            e.Property(t => t.TemplateKey).HasMaxLength(80).IsRequired();
            e.Property(t => t.Name).HasMaxLength(120).IsRequired();
            e.Property(t => t.Description).HasMaxLength(500);
            e.Property(t => t.BusinessType).HasMaxLength(80);
            e.Property(t => t.PreviewImageUrl).HasMaxLength(500);
        });
    }
}
