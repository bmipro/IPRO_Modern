using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// Fabricates a full PublicWebsiteViewModel for a prospect who has not created an account --
// AgentUser/AgentWebsite/WebsitePage/WebsiteContentBlock/Article are all real entity shapes but
// exist only in memory for the duration of one request. No Add/SaveChangesAsync anywhere in this
// file. Modeled on TemplatePreviewBuilder.cs (proves an in-memory AgentWebsite/AgentUser renders
// correctly through the real public-site pipeline) and WebsiteStarterPagesHelper/
// WebsiteStarterResourcesHelper (the real starter-content selection logic this reuses, minus
// persistence and minus the two-phase save Resources needs only because it writes real rows).
public static class ProspectWebsitePreviewBuilder
{
    public static async Task<PublicWebsiteViewModel?> BuildAsync(IPRODbContext db, ProspectPreviewInput input)
    {
        var prospect = input.Normalized();

        var templateKey = TemplateKeyForBusinessType(prospect.BusinessType);
        var template = await db.WebsiteTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TemplateKey == templateKey && t.IsActive)
            ?? await db.WebsiteTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.IsDefault && t.IsActive);
        if (template == null) return null;

        var nextId = -1;
        int NextId() => nextId--;

        var agent = new AgentUser
        {
            Id = NextId(),
            FirstName = prospect.FirstName,
            LastName = prospect.LastName,
            CompanyName = prospect.CompanyName,
            BusinessType = prospect.BusinessType,
            Designation = string.Empty,
            Email = string.Empty,
            Phone = string.Empty,
            City = string.Empty,
            Province = string.Empty,
            PostalCode = string.Empty,
            Country = string.Empty,
            CompanyAddress = string.Empty
        };

        var website = new AgentWebsite
        {
            Id = NextId(),
            AgentUserId = agent.Id,
            AgentUser = agent,
            TemplateId = template.Id,
            Template = template,
            ThemeColor = WebsiteTemplateDesign.FromTemplate(template).AccentColor,
            HeaderSettingsJson = "{}",
            FooterSettingsJson = "{}",
            IsPublished = true
        };

        var pages = new List<WebsitePage>();

        // Starter pages -- same selection rule EnsureStarterPagesAsync uses (exact BusinessType
        // beats "All"), just never persisted. Naturally yields the current default set (Home/About/
        // Testimonials/Contact/Free Newsletter/Request Meeting) since Services is IsActive=false.
        var starterPages = await db.WebsiteStarterPages
            .AsNoTracking()
            .Include(p => p.Blocks)
            .Where(p => p.IsActive && (p.BusinessType == prospect.BusinessType || p.BusinessType == "All"))
            .ToListAsync();
        var selectedPages = starterPages
            .GroupBy(p => p.Slug, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(p => p.BusinessType == prospect.BusinessType).First())
            .OrderBy(p => p.SortOrder)
            .ToList();

        foreach (var starter in selectedPages)
        {
            pages.Add(new WebsitePage
            {
                Id = NextId(),
                AgentWebsiteId = website.Id,
                Title = starter.Title,
                Slug = starter.Slug,
                NavigationLabel = starter.NavigationLabel,
                MetaTitle = starter.MetaTitle,
                MetaDescription = starter.MetaDescription,
                IsHomePage = starter.IsHomePage,
                ShowInNavigation = starter.ShowInNavigation,
                IsPublished = true,
                SortOrder = starter.SortOrder,
                Blocks = starter.Blocks.OrderBy(b => b.SortOrder).Select(b => new WebsiteContentBlock
                {
                    Id = NextId(),
                    BlockType = b.BlockType,
                    Heading = b.Heading,
                    Subheading = b.Subheading,
                    Body = b.Body,
                    ImageUrl = b.ImageUrl,
                    ButtonText = b.ButtonText,
                    ButtonUrl = b.ButtonUrl,
                    SettingsJson = b.SettingsJson,
                    SortOrder = b.SortOrder,
                    IsVisible = b.IsVisible
                }).ToList()
            });
        }

        // Resources -- same selection rule EnsureResourcesAsync uses. The real helper needs a
        // two-phase save (create Article, save, THEN reference its real Id in the block's
        // SettingsJson) because ArticleContent needs a real database-assigned Article.Id; here a
        // fake Id serves exactly the same purpose, so it collapses to one pass with no save at all.
        var articleContentByBlockId = new Dictionary<int, Article>();
        var starterArticles = await db.WebsiteStarterArticles
            .AsNoTracking()
            .Where(a => a.IsActive && (a.BusinessType == prospect.BusinessType || a.BusinessType == "All"))
            .ToListAsync();
        var selectedArticles = starterArticles
            .GroupBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(a => a.BusinessType == prospect.BusinessType).First())
            .OrderBy(a => a.SortOrder)
            .ToList();

        var previewCalculators = VerticalCalculatorCatalog.ForBusinessType(prospect.BusinessType);
        if (selectedArticles.Count > 0 || previewCalculators.Count > 0)
        {
            var resourcesPage = new WebsitePage
            {
                Id = NextId(),
                AgentWebsiteId = website.Id,
                Title = "Resources",
                Slug = "resources",
                NavigationLabel = "Resources",
                MetaTitle = "Resources",
                MetaDescription = "Helpful articles and resources.",
                ShowInNavigation = true,
                IsPublished = true,
                SortOrder = pages.Count,
                Blocks = new List<WebsiteContentBlock>
                {
                    new()
                    {
                        Id = NextId(),
                        BlockType = WebsiteBlockTypes.Text,
                        Heading = "Resources",
                        Body = "Helpful articles and updates, added from time to time.",
                        SortOrder = 0,
                        IsVisible = true
                    },
                    new()
                    {
                        Id = NextId(),
                        BlockType = WebsiteBlockTypes.SectionIndex,
                        SortOrder = 1,
                        IsVisible = true
                    }
                }
            };
            pages.Add(resourcesPage);

            // Mirrors WebsiteStarterResourcesHelper: uncategorized starters each get their own child
            // page directly under Resources; categorized starters get a Category page in between,
            // with one page per article beneath it. Kept in sync by hand -- this builder writes
            // nothing, so it can't call the real helper.
            var existingSlugs = pages.Select(p => p.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var childOrder = 0;
            foreach (var group in selectedArticles.GroupBy(a => a.Category))
            {
                if (string.IsNullOrWhiteSpace(group.Key))
                {
                    foreach (var starter in group)
                    {
                        var article = new Article
                        {
                            Id = NextId(),
                            AgentUserId = agent.Id,
                            Title = starter.Title,
                            Summary = starter.Summary,
                            Content = starter.Content,
                            ImageUrl = starter.ImageUrl,
                            IsPublished = true,
                            PublishedAt = DateTime.UtcNow
                        };

                        var slug = UniqueSlug(Slugify(starter.Title), existingSlugs);
                        existingSlugs.Add(slug);

                        var block = new WebsiteContentBlock
                        {
                            Id = NextId(),
                            BlockType = WebsiteBlockTypes.ArticleContent,
                            SortOrder = 0,
                            IsVisible = true
                        };

                        pages.Add(new WebsitePage
                        {
                            Id = NextId(),
                            AgentWebsiteId = website.Id,
                            ParentPageId = resourcesPage.Id,
                            Title = starter.Title,
                            Slug = slug,
                            NavigationLabel = starter.Title,
                            MetaTitle = starter.Title,
                            MetaDescription = starter.Summary,
                            ShowInNavigation = true,
                            IsPublished = true,
                            SortOrder = childOrder++,
                            Blocks = new List<WebsiteContentBlock> { block }
                        });

                        articleContentByBlockId[block.Id] = article;
                    }
                    continue;
                }

                var categorySlug = UniqueSlug(Slugify(group.Key), existingSlugs);
                existingSlugs.Add(categorySlug);
                var categoryPage = new WebsitePage
                {
                    Id = NextId(),
                    AgentWebsiteId = website.Id,
                    ParentPageId = resourcesPage.Id,
                    Title = group.Key,
                    Slug = categorySlug,
                    NavigationLabel = group.Key,
                    MetaTitle = group.Key,
                    MetaDescription = $"{group.Key} articles and resources.",
                    ShowInNavigation = true,
                    IsPublished = true,
                    SortOrder = childOrder++,
                    Blocks = new List<WebsiteContentBlock>
                    {
                        new()
                        {
                            Id = NextId(),
                            BlockType = WebsiteBlockTypes.SectionIndex,
                            Heading = group.Key,
                            SortOrder = 0,
                            IsVisible = true
                        }
                    }
                };
                pages.Add(categoryPage);

                var articleOrder = 0;
                foreach (var starter in group.OrderBy(a => a.SortOrder))
                {
                    var article = new Article
                    {
                        Id = NextId(),
                        AgentUserId = agent.Id,
                        Title = starter.Title,
                        Summary = starter.Summary,
                        Content = starter.Content,
                        ImageUrl = starter.ImageUrl,
                        IsPublished = true,
                        PublishedAt = DateTime.UtcNow
                    };
                    var block = new WebsiteContentBlock
                    {
                        Id = NextId(),
                        BlockType = WebsiteBlockTypes.ArticleContent,
                        SortOrder = 0,
                        IsVisible = true
                    };
                    articleContentByBlockId[block.Id] = article;

                    var articleSlug = UniqueSlug(Slugify(starter.Title), existingSlugs);
                    existingSlugs.Add(articleSlug);
                    pages.Add(new WebsitePage
                    {
                        Id = NextId(),
                        AgentWebsiteId = website.Id,
                        ParentPageId = categoryPage.Id,
                        Title = starter.Title,
                        Slug = articleSlug,
                        NavigationLabel = starter.Title,
                        MetaTitle = starter.Title,
                        MetaDescription = starter.Summary,
                        ShowInNavigation = true,
                        IsPublished = true,
                        SortOrder = articleOrder++,
                        Blocks = new List<WebsiteContentBlock> { block }
                    });
                }
            }

            // Calculators category -- mirrors WebsiteStarterResourcesHelper's cal1-derived category.
            // Calculator blocks are self-contained (settings only, no database row), so the mirror
            // needs nothing beyond fake page ids.
            if (previewCalculators.Count > 0)
            {
                var calcCategorySlug = UniqueSlug("calculators", existingSlugs);
                existingSlugs.Add(calcCategorySlug);
                var calcCategoryPage = new WebsitePage
                {
                    Id = NextId(),
                    AgentWebsiteId = website.Id,
                    ParentPageId = resourcesPage.Id,
                    Title = "Calculators",
                    Slug = calcCategorySlug,
                    NavigationLabel = "Calculators",
                    MetaTitle = "Calculators",
                    MetaDescription = "Free financial calculators you can use any time.",
                    ShowInNavigation = true,
                    IsPublished = true,
                    SortOrder = childOrder++,
                    Blocks = new List<WebsiteContentBlock>
                    {
                        new()
                        {
                            Id = NextId(),
                            BlockType = WebsiteBlockTypes.SectionIndex,
                            Heading = "Calculators",
                            SortOrder = 0,
                            IsVisible = true
                        }
                    }
                };
                pages.Add(calcCategoryPage);

                var calcOrder = 0;
                foreach (var entry in previewCalculators)
                {
                    var title = CalculatorKinds.DisplayName(entry.Kind);
                    var calcSlug = UniqueSlug(Slugify(title), existingSlugs);
                    existingSlugs.Add(calcSlug);
                    pages.Add(new WebsitePage
                    {
                        Id = NextId(),
                        AgentWebsiteId = website.Id,
                        ParentPageId = calcCategoryPage.Id,
                        Title = title,
                        Slug = calcSlug,
                        NavigationLabel = title,
                        MetaTitle = title,
                        MetaDescription = entry.Blurb,
                        ShowInNavigation = true,
                        IsPublished = true,
                        SortOrder = calcOrder++,
                        Blocks = new List<WebsiteContentBlock>
                        {
                            new()
                            {
                                Id = NextId(),
                                BlockType = WebsiteBlockTypes.Calculator,
                                Heading = title,
                                Subheading = entry.Blurb,
                                SettingsJson = new WebsiteCalculatorSettings { CalculatorKind = entry.Kind }.ToJson(),
                                SortOrder = 0,
                                IsVisible = true
                            }
                        }
                    });
                }
            }
        }

        // Request Meeting form -- mirrors WebsiteStarterPagesHelper, which swaps the seeded contact
        // block for the vertical's "Request a Meeting" form at signup. Real provisioning copies the
        // template into a saved WebsiteForm and references its database id; here fake negative ids
        // serve the same purpose (the form is render-only: _WebsiteCustomForm disables submission
        // whenever IsProspectPreview is set, same gate as the testimonial form).
        var formsByBlockId = new Dictionary<int, PublicFormBlockData>();
        var meetingPage = pages.FirstOrDefault(p => string.Equals(p.Slug, "request-meeting", StringComparison.OrdinalIgnoreCase));
        var meetingBlock = meetingPage?.Blocks.FirstOrDefault(b => b.BlockType == WebsiteBlockTypes.ContactForm);
        if (meetingBlock != null)
        {
            var meetingTemplate = (await db.WebsiteStarterForms
                    .AsNoTracking()
                    .Where(f => f.IsActive && f.Title == WebsiteStarterFormSeeder.MeetingFormTitle &&
                                (f.BusinessType == prospect.BusinessType || f.BusinessType == "All"))
                    .ToListAsync())
                .OrderByDescending(f => f.BusinessType == prospect.BusinessType)
                .FirstOrDefault();
            if (meetingTemplate != null)
            {
                var templateFields = await db.WebsiteStarterFormFields.AsNoTracking()
                    .Where(f => f.WebsiteStarterFormId == meetingTemplate.Id).OrderBy(f => f.SortOrder).ToListAsync();
                var templateFieldIds = templateFields.Select(f => f.Id).ToList();
                var templateOptions = await db.WebsiteStarterFormFieldOptions.AsNoTracking()
                    .Where(o => templateFieldIds.Contains(o.WebsiteStarterFormFieldId)).OrderBy(o => o.SortOrder).ToListAsync();

                meetingBlock.BlockType = WebsiteBlockTypes.Form;
                formsByBlockId[meetingBlock.Id] = new PublicFormBlockData
                {
                    WebsiteFormId = NextId(),
                    Title = meetingTemplate.Title,
                    Description = meetingTemplate.Description,
                    SubmitButtonText = meetingTemplate.SubmitButtonText,
                    SuccessMessage = meetingTemplate.SuccessMessage,
                    Fields = templateFields.Select(f => new PublicFormField
                    {
                        FieldId = NextId(),
                        FieldType = f.FieldType,
                        Label = f.Label,
                        Placeholder = f.Placeholder,
                        HelpText = f.HelpText,
                        IsRequired = f.IsRequired,
                        Options = templateOptions.Where(o => o.WebsiteStarterFormFieldId == f.Id).Select(o => o.Text).ToList()
                    }).ToList()
                };
            }
        }

        var currentPage = string.IsNullOrWhiteSpace(prospect.Page)
            ? null
            : pages.FirstOrDefault(p => string.Equals(p.Slug, prospect.Page, StringComparison.OrdinalIgnoreCase));
        currentPage ??= pages.FirstOrDefault(p => p.IsHomePage) ?? pages.FirstOrDefault();

        return new PublicWebsiteViewModel
        {
            Website = website,
            Pages = pages,
            CurrentPage = currentPage,
            ApprovedTestimonials = BuildTestimonials(prospect.BusinessType),
            ArticleContentByBlockId = articleContentByBlockId,
            FormsByBlockId = formsByBlockId
        };
    }

    private static string TemplateKeyForBusinessType(string businessType) => businessType switch
    {
        "Accountants" => WebsiteTemplateSeeder.ClassicSidebarTemplateKey,
        "Mortgage" => WebsiteTemplateSeeder.EditorialVisualTemplateKey,
        _ => WebsiteTemplateSeeder.DefaultTemplateKey // "Insurance / Financial" and any fallback
    };

    // A handful of fabricated approved testimonials so the Testimonials starter page (which
    // otherwise shows only its own now-disabled submission form) is a real demo moment, not an
    // empty one. TestimonialSubmission.Id is never read by the rendering path -- left at default.
    private static List<TestimonialSubmission> BuildTestimonials(string businessType)
    {
        var (people, quotes) = businessType switch
        {
            "Accountants" => (
                new[] { ("Karen", "Whitfield"), ("Daniel", "Osei") },
                new[]
                {
                    "Organized, responsive, and always ahead of deadlines. Filing season has never been this stress-free.",
                    "Finally, an accountant who explains things in plain language and actually returns calls."
                }),
            "Mortgage" => (
                new[] { ("Ryan", "Beaudry"), ("Lisa", "Nakamura") },
                new[]
                {
                    "Walked us through every option and got us a better rate than we expected. Closing was smooth.",
                    "First-time buyers, completely lost -- made the whole process feel manageable."
                }),
            _ => (
                new[] { ("Emily", "Sato"), ("James", "Okafor") },
                new[]
                {
                    "Took the time to actually understand our goals before recommending anything. Highly recommend.",
                    "Responsive, knowledgeable, and never made me feel rushed through a decision."
                })
        };

        return people.Zip(quotes, (person, quote) => new TestimonialSubmission
        {
            FirstName = person.Item1,
            LastName = person.Item2,
            Body = quote,
            Status = TestimonialStatus.Approved,
            SubmittedAt = DateTime.UtcNow.AddDays(-30),
            ReviewedAt = DateTime.UtcNow.AddDays(-25)
        }).ToList();
    }

    private static string Slugify(string title) =>
        System.Text.RegularExpressions.Regex.Replace(title.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    private static string UniqueSlug(string baseSlug, HashSet<string> existingSlugs)
    {
        if (string.IsNullOrWhiteSpace(baseSlug)) baseSlug = "resource";
        if (!existingSlugs.Contains(baseSlug)) return baseSlug;
        var i = 2;
        while (existingSlugs.Contains($"{baseSlug}-{i}")) i++;
        return $"{baseSlug}-{i}";
    }
}
