using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

public static class PackageEntitlementSeeder
{
    private const int Unlimited = -1;

    public static async Task SeedAsync(IPRODbContext db)
    {
        var packages = await EnsurePackagesAsync(db);
        await EnsureFeaturesAsync(db, packages);
    }

    private static async Task<Dictionary<string, BillingRule>> EnsurePackagesAsync(IPRODbContext db)
    {
        var packageDefinitions = new[]
        {
            new PackageDefinition("IPro Silver", "Entry package for individual advisors.", 40m, 120m, 480m, 500, 12),
            new PackageDefinition("IPro Gold", "Expanded package with marketing, banners, coupons, and mail tools.", 60m, 180m, 720m, Unlimited, Unlimited),
            new PackageDefinition("IPro Platinum", "Premium package with managed content, SEO, and PayPal tools.", 90m, 270m, 1080m, Unlimited, Unlimited),
            new PackageDefinition("Broker Package", "Broker/team package. Pricing, setup, and monthly fees vary.", 0m, 0m, 0m, Unlimited, Unlimited)
        };

        foreach (var definition in packageDefinitions)
        {
            var existing = await db.BillingRules.FirstOrDefaultAsync(p => p.PackageName == definition.Name);
            if (existing == null)
            {
                existing = new BillingRule
                {
                    PackageName = definition.Name,
                    Description = definition.Description,
                    MonthlyPrice = definition.MonthlyPrice,
                    QuarterlyPrice = definition.QuarterlyPrice,
                    AnnualPrice = definition.AnnualPrice,
                    MaxClients = definition.MaxClients,
                    MaxNewsletters = definition.MaxNewsletters,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };
                await db.BillingRules.AddAsync(existing);
            }
            else
            {
                existing.Description = string.IsNullOrWhiteSpace(existing.Description) ? definition.Description : existing.Description;
                existing.MaxClients = existing.MaxClients == 0 ? definition.MaxClients : existing.MaxClients;
                existing.MaxNewsletters = existing.MaxNewsletters == 0 ? definition.MaxNewsletters : existing.MaxNewsletters;
            }
        }

        await db.SaveChangesAsync();
        return await db.BillingRules
            .Where(p => packageDefinitions.Select(d => d.Name).Contains(p.PackageName))
            .ToDictionaryAsync(p => p.PackageName);
    }

    private static async Task EnsureFeaturesAsync(IPRODbContext db, IReadOnlyDictionary<string, BillingRule> packages)
    {
        var definitions = BuildFeatureDefinitions();
        var existingFeatures = await db.PackageFeatures.ToListAsync();

        foreach (var definition in definitions)
        {
            foreach (var packageName in packages.Keys)
            {
                var value = definition.Values[packageName];
                var billingRuleId = packages[packageName].Id;
                var existing = existingFeatures.FirstOrDefault(f =>
                    f.BillingRuleId == billingRuleId && f.FeatureCode == definition.Code);

                if (existing == null)
                {
                    await db.PackageFeatures.AddAsync(new PackageFeature
                    {
                        BillingRuleId = billingRuleId,
                        FeatureCode = definition.Code,
                        FeatureName = definition.Name,
                        IsIncluded = value.IsIncluded,
                        LimitValue = value.LimitValue,
                        LimitLabel = value.LimitLabel,
                        SortOrder = definition.SortOrder,
                        CreatedAt = DateTime.UtcNow
                    });
                }
                else
                {
                    existing.FeatureName = string.IsNullOrWhiteSpace(existing.FeatureName) ? definition.Name : existing.FeatureName;
                    existing.SortOrder = existing.SortOrder == 0 ? definition.SortOrder : existing.SortOrder;
                }
            }
        }

        await db.SaveChangesAsync();
    }

    private static IReadOnlyList<FeatureDefinition> BuildFeatureDefinitions()
    {
        var all = new FeatureValue(true);
        var no = new FeatureValue(false);
        var limited = new FeatureValue(true, null, "Limited");
        var unlimited = new FeatureValue(true, Unlimited, "Unlimited");

        return new List<FeatureDefinition>
        {
            Feature(10, PackageFeatureCodes.InstantWebsite, "Self managed instant website with full content", all, all, all, all),
            Feature(20, PackageFeatureCodes.LeadGenerator, "Automated lead generator", all, all, all, all),
            Feature(30, PackageFeatureCodes.CalendarScheduler, "Calendar scheduler", all, all, all, all),
            Feature(40, PackageFeatureCodes.EmailReminder, "Email reminder", all, all, all, all),
            Feature(50, PackageFeatureCodes.SmsReminder, "Mobile SMS reminder", all, all, all, all),
            Feature(60, PackageFeatureCodes.PreDesignedECard, "Pre-designed e-card", no, all, all, all),
            Feature(70, PackageFeatureCodes.PreDesignedELetters, "Pre-designed e-letters", no, all, all, all),
            Feature(80, PackageFeatureCodes.MarketingCampaign, "Automated marketing campaign", all, all, all, all),
            Feature(90, PackageFeatureCodes.Contacts, "Contacts", new FeatureValue(true, 500, "500"), unlimited, unlimited, unlimited),
            Feature(100, PackageFeatureCodes.WebsiteDesign, "Pre-formatted website design", all, all, all, all),
            Feature(110, PackageFeatureCodes.Newsletters, "Create and send newsletters", all, all, all, all),
            Feature(120, PackageFeatureCodes.SupportTraining, "Support and training", limited, unlimited, unlimited, unlimited),
            Feature(130, PackageFeatureCodes.RotatingBanner, "Rotating banner", no, all, all, all),
            Feature(140, PackageFeatureCodes.Newsboard, "Newsboard", all, all, all, all),
            Feature(150, PackageFeatureCodes.FileUploadCapacity, "File upload capacity", new FeatureValue(true, 50, "50 MB"), new FeatureValue(true, 500, "500 MB"), new FeatureValue(true, 1000, "1000 MB"), new FeatureValue(true, 1000, "1000 MB/per user")),
            Feature(160, PackageFeatureCodes.CouponManager, "Coupon manager", no, all, all, all),
            Feature(170, PackageFeatureCodes.MultiDomainSupport, "Multi domain support", new FeatureValue(true, 2, "2"), unlimited, unlimited, unlimited),
            Feature(180, PackageFeatureCodes.MailMerge, "Mail merge function", no, all, all, all),
            Feature(190, PackageFeatureCodes.PrintableLabelCreator, "Printable label creator", no, all, all, all),
            Feature(200, PackageFeatureCodes.CustomHomeButtons, "Create custom buttons on home page", all, all, all, all),
            Feature(210, PackageFeatureCodes.NeedsAnalysisCalculator, "Need analysis calculator", all, all, all, all),
            Feature(220, PackageFeatureCodes.SeoTool, "Built-in SEO tool", all, all, all, all),
            Feature(230, PackageFeatureCodes.DidYouKnowManager, "Did you know manager", all, all, all, all),
            Feature(240, PackageFeatureCodes.QuoteForm, "Get a quote form with email function", all, all, all, all),
            Feature(250, PackageFeatureCodes.MeetingRequestForm, "Request meeting form with email function", all, all, all, all),
            Feature(260, PackageFeatureCodes.OutlookImport, "Import contact list from Outlook", all, all, all, all),
            Feature(270, PackageFeatureCodes.EmailTracking, "Email report and tracking system", all, all, all, all),
            Feature(280, PackageFeatureCodes.VisitorTracking, "Detailed visitor/hits tracking system", all, all, all, all),
            Feature(290, PackageFeatureCodes.CustomWebPages, "Custom web pages", all, all, all, all),
            Feature(300, PackageFeatureCodes.SocialMediaIntegration, "Social media integration", all, all, all, all),
            Feature(310, PackageFeatureCodes.FramedLinkManager, "Framed link manager", all, all, all, all),
            Feature(320, PackageFeatureCodes.MenuCreator, "Menu and sub-menu creator", all, all, all, all),
            Feature(330, PackageFeatureCodes.TestimonialManager, "Testimonial manager", all, all, all, all),
            Feature(340, PackageFeatureCodes.MultilingualEditor, "Multilingual editor support", all, all, all, all),
            Feature(350, PackageFeatureCodes.ProspectManager, "Prospect manager", all, all, all, all),
            Feature(360, PackageFeatureCodes.ManagedBlog, "One unique blog per month written and managed", no, no, all, all),
            Feature(370, PackageFeatureCodes.ManagedSeo, "Managed SEO for all pages", no, no, all, all),
            Feature(380, PackageFeatureCodes.PayPalIntegration, "PayPal integration", no, no, all, all),
            Feature(390, PackageFeatureCodes.DesignatedSupport, "Designated support", no, no, no, all)
        };
    }

    private static FeatureDefinition Feature(int sortOrder, string code, string name, FeatureValue silver, FeatureValue gold, FeatureValue platinum, FeatureValue broker) =>
        new(sortOrder, code, name, new Dictionary<string, FeatureValue>
        {
            ["IPro Silver"] = silver,
            ["IPro Gold"] = gold,
            ["IPro Platinum"] = platinum,
            ["Broker Package"] = broker
        });

    private sealed record PackageDefinition(string Name, string Description, decimal MonthlyPrice, decimal QuarterlyPrice, decimal AnnualPrice, int MaxClients, int MaxNewsletters);
    private sealed record FeatureDefinition(int SortOrder, string Code, string Name, IReadOnlyDictionary<string, FeatureValue> Values);
    private sealed record FeatureValue(bool IsIncluded, int? LimitValue = null, string LimitLabel = "");
}
