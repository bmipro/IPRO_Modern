using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.DataAccess;

// Guarded by SeedGuard: check-then-insert on BillingRules.PackageName, run by both apps against the
// same database from the same push. BillingRules has no unique index on PackageName, so the race
// duplicates package rows rather than throwing at insert time -- and the duplicate is what does the
// damage, because EnsurePackagesAsync then builds a dictionary keyed on PackageName and a duplicate
// key throws ArgumentException there instead.
//
// That is worse than the 2026-07-29 outage it resembles: the bad rows persist, so EVERY subsequent
// start of BOTH apps throws in the same place. A one-time race becomes a permanent boot crash-loop
// that no restart clears. Hence two defences here -- the lock to stop it happening, and a
// duplicate-tolerant read below so an already-poisoned database can still boot.
public static class PackageEntitlementSeeder
{
    private const int Unlimited = -1;

    public static async Task SeedAsync(IPRODbContext db, ILogger? logger = null)
    {
        await SeedGuard.RunAsync(db, "PackageEntitlements", logger, async () =>
        {
            var packages = await EnsurePackagesAsync(db, logger);
            await EnsureFeaturesAsync(db, packages);
            await RepairGoogleCalendarSyncEntitlementAsync(db, packages);
            await RepairSmsReminderEntitlementAsync(db);
            await RetireWithdrawnFeaturesAsync(db);
        });
    }

    private static async Task<Dictionary<string, BillingRule>> EnsurePackagesAsync(IPRODbContext db, ILogger? logger)
    {
        var packageDefinitions = new[]
        {
            // Annual is priced at 10x monthly -- pay for the year, get two months free. Previously
            // 12x, which offered no reason to pay annually and read as a bad deal on the most
            // closely-read page on the site. Changing these values only affects a fresh database;
            // existing rows are edited in Super Admin -> Packages, and the PayPal plan MUST be
            // re-synced there afterwards or PayPal keeps billing the old plan's price.
            new PackageDefinition("IPro Silver", "Entry package for individual advisors.", 40m, 120m, 400m, 150m, 500, 12),
            new PackageDefinition("IPro Gold", "Expanded package with marketing, banners, coupons, and mail tools.", 60m, 180m, 600m, 200m, Unlimited, Unlimited),
            new PackageDefinition("IPro Platinum", "Premium package with managed content, SEO, and PayPal tools.", 90m, 270m, 900m, 400m, Unlimited, Unlimited),
            new PackageDefinition("Broker Package", "Broker/team package. Pricing, setup, and monthly fees vary.", 0m, 0m, 0m, 0m, Unlimited, Unlimited)
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
                    SetupFee = definition.SetupFee,
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
                existing.SetupFee = existing.SetupFee == 0 ? definition.SetupFee : existing.SetupFee;
                existing.MaxClients = existing.MaxClients == 0 ? definition.MaxClients : existing.MaxClients;
                existing.MaxNewsletters = existing.MaxNewsletters == 0 ? definition.MaxNewsletters : existing.MaxNewsletters;
            }
        }

        await db.SaveChangesAsync();

        // Duplicate-tolerant on purpose. ToDictionaryAsync(p => p.PackageName) throws
        // ArgumentException the moment two rows share a name, and because the rows persist that
        // turns one race into a permanent boot crash-loop on both apps. Group and take the lowest
        // Id -- the original -- so a poisoned database still starts and stays deterministic about
        // which row wins.
        var names = packageDefinitions.Select(d => d.Name).ToArray();
        var rows = await db.BillingRules
            .Where(p => names.Contains(p.PackageName))
            .ToListAsync();

        var duplicated = rows.GroupBy(p => p.PackageName).Where(g => g.Count() > 1).ToList();
        if (duplicated.Count > 0)
        {
            logger?.LogWarning(
                "BillingRules has duplicate package rows for {Names}. Using the earliest of each; " +
                "clean up the extras, as agents may be pointed at the wrong row.",
                string.Join(", ", duplicated.Select(g => $"{g.Key} x{g.Count()}")));
        }

        return rows
            .GroupBy(p => p.PackageName)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Id).First());
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

    // One-time data repair, not a schema change: the GoogleCalendarSync row above was originally
    // seeded with every package denied (no,no,no,no) -- a configuration bug, not an intentional
    // launch state, since GoogleCalendarController actively gates on it and the sync job has run
    // on a recurring schedule since 2026-07-19. EnsureFeaturesAsync deliberately never re-syncs
    // IsIncluded on a PackageFeature row that already exists (SuperAdmin can hand-edit entitlements
    // per package via the Packages screen, and a blind re-sync on every startup would silently
    // clobber that), so correcting the definition above only fixes fresh installs -- any
    // already-seeded database keeps the wrong value until corrected here. Narrowly scoped to this
    // one feature/package combination so it can't touch a real SuperAdmin customization elsewhere;
    // becomes a permanent no-op the first time it runs against a database where this is fixed.
    private static async Task RepairGoogleCalendarSyncEntitlementAsync(IPRODbContext db, IReadOnlyDictionary<string, BillingRule> packages)
    {
        var changed = false;
        foreach (var packageName in new[] { "IPro Platinum", "Broker Package" })
        {
            if (!packages.TryGetValue(packageName, out var package)) continue;
            var feature = await db.PackageFeatures.FirstOrDefaultAsync(f =>
                f.BillingRuleId == package.Id && f.FeatureCode == PackageFeatureCodes.GoogleCalendarSync);
            if (feature != null && !feature.IsIncluded)
            {
                feature.IsIncluded = true;
                feature.LimitValue = null;
                feature.LimitLabel = "";
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync();
        }
    }

    // The mirror image of the repair above, for the opposite mistake: SMS reminders were seeded as
    // INCLUDED on all four packages and the feature does not exist. Nothing sends an SMS anywhere in
    // this codebase; it sits in DOCS/TODO.md as backlog. The consequence was public: the pricing
    // page's feature comparison table showed "Mobile SMS reminder" with a tick against every plan,
    // so the cheapest tier advertised a capability that cannot fire.
    //
    // As above, EnsureFeaturesAsync never re-syncs IsIncluded on an existing row, so fixing the
    // definition only helps fresh installs. This corrects databases seeded before 15 Aug 2026.
    // Scoped to this one feature code, and a permanent no-op once it has run.
    //
    // If SMS is ever built: flip the definition in BuildFeatureDefinitions, and DELETE this method
    // rather than leaving it to switch the feature back off on every startup.
    // Owner decision 2026-08-28: four features were sold in every package's comparison table with
    // no implementation anywhere in the codebase -- rotating banner, newsboard, mail merge and
    // printable label creator. They are WITHDRAWN, not deferred: their definitions are gone from
    // BuildFeatureDefinitions above, and this deletes the rows an existing database already has.
    //
    // Deleting rather than un-ticking is deliberate. The homepage comparison table renders one row
    // per PackageFeature that exists, so an un-ticked row would still show the feature name with a
    // dash against every plan -- advertising something we do not build. The row has to go.
    //
    // The fifth, MultilingualEditor, is KEPT and RENAMED. The capability is real but it is not an
    // editor we ship: an agent writes in whatever editor they like and pastes the content in, and
    // it renders correctly -- bahmanmotamed.247advisers.com/article is a live Farsi article created
    // exactly that way. "Multilingual editor support" implied a product feature; the new wording
    // describes what actually happens.
    //
    // Same reasoning as RepairSmsReminderEntitlementAsync below: EnsureFeaturesAsync only ever ADDS
    // missing rows and never re-syncs an existing row's name or IsIncluded, so changing the
    // definitions alone would fix fresh installs and leave production selling all five forever.
    //
    // A permanent no-op once it has run. If any of the four is ever built, re-add its definition
    // and DELETE its code from RetiredFeatureCodes rather than leaving this to delete the rows on
    // every startup.
    private static readonly string[] RetiredFeatureCodes =
    {
        "rotating_banner", "newsboard", "mail_merge", "printable_label_creator"
    };

    internal const string MultilingualFeatureName = "Supports multilingual content (paste from any editor)";

    private static async Task RetireWithdrawnFeaturesAsync(IPRODbContext db)
    {
        var withdrawn = await db.PackageFeatures
            .Where(f => RetiredFeatureCodes.Contains(f.FeatureCode))
            .ToListAsync();

        var changed = false;
        if (withdrawn.Count > 0)
        {
            db.PackageFeatures.RemoveRange(withdrawn);
            changed = true;
        }

        var multilingual = await db.PackageFeatures
            .Where(f => f.FeatureCode == PackageFeatureCodes.MultilingualEditor)
            .ToListAsync();
        foreach (var feature in multilingual)
        {
            if (feature.FeatureName != MultilingualFeatureName)
            {
                feature.FeatureName = MultilingualFeatureName;
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync();
        }
    }

    private static async Task RepairSmsReminderEntitlementAsync(IPRODbContext db)
    {
        var rows = await db.PackageFeatures
            .Where(f => f.FeatureCode == PackageFeatureCodes.SmsReminder)
            .ToListAsync();

        var changed = false;
        foreach (var feature in rows)
        {
            if (feature.IsIncluded)
            {
                feature.IsIncluded = false;
                changed = true;
            }
            if (feature.FeatureName == "Mobile SMS reminder")
            {
                feature.FeatureName = "Mobile SMS reminder (not yet available)";
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync();
        }
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
            // SMS IS NOT BUILT. Seeded as excluded on every package so a fresh database never
            // advertises it on the public pricing comparison, and no agent is entitled to a
            // feature that cannot fire. It stays in the catalogue rather than being deleted so the
            // feature code keeps its identity if SMS is ever implemented -- flip the values then.
            // NOTE: this seeder only fills blank names and never rewrites IsIncluded on existing
            // rows, so databases seeded before 15 Aug 2026 still have this ticked on all four
            // packages and must be corrected in Super Admin -> Packages. See DOCS/TODO.md.
            Feature(50, PackageFeatureCodes.SmsReminder, "Mobile SMS reminder (not yet available)", no, no, no, no),
            Feature(60, PackageFeatureCodes.PreDesignedECard, "Pre-designed e-card", no, all, all, all),
            Feature(70, PackageFeatureCodes.PreDesignedELetters, "Pre-designed e-letters", no, all, all, all),
            Feature(80, PackageFeatureCodes.MarketingCampaign, "Automated marketing campaign", all, all, all, all),
            Feature(90, PackageFeatureCodes.Contacts, "Contacts", new FeatureValue(true, 500, "500"), unlimited, unlimited, unlimited),
            Feature(100, PackageFeatureCodes.WebsiteDesign, "Pre-formatted website design", all, all, all, all),
            Feature(110, PackageFeatureCodes.Newsletters, "Create and send newsletters", all, all, all, all),
            Feature(120, PackageFeatureCodes.SupportTraining, "Support and training", limited, unlimited, unlimited, unlimited),
            Feature(150, PackageFeatureCodes.FileUploadCapacity, "File upload capacity", new FeatureValue(true, 50, "50 MB"), new FeatureValue(true, 500, "500 MB"), new FeatureValue(true, 1000, "1000 MB"), new FeatureValue(true, 1000, "1000 MB/per user")),
            Feature(160, PackageFeatureCodes.CouponManager, "Coupon manager", no, all, all, all),
            Feature(170, PackageFeatureCodes.MultiDomainSupport, "Multi domain support", new FeatureValue(true, 2, "2"), unlimited, unlimited, unlimited),
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
            Feature(340, PackageFeatureCodes.MultilingualEditor, "Supports multilingual content (paste from any editor)", all, all, all, all),
            Feature(350, PackageFeatureCodes.ProspectManager, "Prospect manager", all, all, all, all),
            Feature(360, PackageFeatureCodes.ManagedBlog, "One unique blog per month written and managed", no, no, all, all),
            Feature(370, PackageFeatureCodes.ManagedSeo, "Managed SEO for all pages", no, no, all, all),
            Feature(380, PackageFeatureCodes.PayPalIntegration, "PayPal integration", no, no, all, all),
            Feature(390, PackageFeatureCodes.DesignatedSupport, "Designated support", no, no, no, all),
            Feature(400, PackageFeatureCodes.ClientInvoicing, "Client invoicing and estimates", no, no, all, all),
            Feature(410, PackageFeatureCodes.ClientPortal, "Client portal (login, messages, documents, appointments)", no, no, all, all),
            Feature(420, PackageFeatureCodes.GoogleCalendarSync, "Google Calendar two-way sync", no, no, all, all),
            Feature(430, PackageFeatureCodes.LifeEventReminders, "Client life-event reminders (birthdays, renewals, anniversaries)", no, no, all, all),
            Feature(440, PackageFeatureCodes.PollSurveys, "Poll and survey builder", all, all, all, all),
            Feature(450, PackageFeatureCodes.LeadMagnet, "Lead magnet download block", all, all, all, all),
            Feature(460, PackageFeatureCodes.AiDailyAssistant, "AI Assistant features", no, no, all, all),
            Feature(470, PackageFeatureCodes.CustomForms, "Custom form builder", all, all, all, all),
            Feature(480, PackageFeatureCodes.TeamMembers, "Team member logins",
                new FeatureValue(true, 1, "1"), new FeatureValue(true, 2, "2"),
                new FeatureValue(true, 5, "5"), new FeatureValue(true, 10, "10"))
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

    private sealed record PackageDefinition(string Name, string Description, decimal MonthlyPrice, decimal QuarterlyPrice, decimal AnnualPrice, decimal SetupFee, int MaxClients, int MaxNewsletters);
    private sealed record FeatureDefinition(int SortOrder, string Code, string Name, IReadOnlyDictionary<string, FeatureValue> Values);
    private sealed record FeatureValue(bool IsIncluded, int? LimitValue = null, string LimitLabel = "");
}
