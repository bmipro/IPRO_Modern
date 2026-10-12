using IPRO.Business.SiteImport;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// 571: the look at a Template Builder package before it is brought in. The site as it WOULD be --
// the plan's pages (SiteImportPlan), the package's logo, the chosen design -- handed to the real
// public-site views, so what the SuperAdmin sees is what a visitor would get. Nothing is written:
// the pages exist only in memory for this request, as in TemplatePreviewBuilder and
// ProspectWebsitePreviewBuilder.
public static class BuilderImportPreviewBuilder
{
    public static async Task<PublicWebsiteViewModel?> BuildAsync(
        IPRODbContext db, WebsiteBuilderImport import, string? pageSlug, int? design, string? without, string? keep = null)
    {
        static HashSet<int> Ids(string? list) => (list ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToHashSet();
        var planned = await BuilderImportService.PlanAsync(db, import, Ids(without), Ids(keep));
        if (planned?.Plan == null || planned.Plan.Problems.Count > 0) return null;
        var plan = planned.Plan;

        // PlanAsync loads the site untracked, so changing it here changes nothing but this look.
        var site = planned.Site;
        site.LogoUrl = plan.LogoUrlAfter;
        if (design.HasValue && design.Value != site.TemplateId)
        {
            var chosen = await db.WebsiteTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == design.Value && t.IsActive);
            if (chosen != null)
            {
                site.Template = chosen;
                site.TemplateId = chosen.Id;
            }
        }

        var slug = (pageSlug ?? string.Empty).Trim().Trim('/').ToLowerInvariant();
        var all = plan.PagesAfter;
        var current = (slug.Length == 0 ? null : all.FirstOrDefault(p => string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase)))
                      ?? all.FirstOrDefault(p => p.IsHomePage)
                      ?? all.FirstOrDefault();
        if (current == null) return null;

        // A visitor sees published pages; the page being looked at is shown whether or not it is.
        var pages = all
            .Where(p => p.IsPublished || p.Id == current.Id)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Title)
            .ToList();

        var agentId = site.AgentUserId;
        var testimonials = current.Blocks.Any(b => b.BlockType == WebsiteBlockTypes.TestimonialForm)
            ? await db.TestimonialSubmissions.AsNoTracking()
                .Where(t => t.AgentUserId == agentId && t.Status == TestimonialStatus.Approved)
                .OrderByDescending(t => t.ReviewedAt)
                .ToListAsync()
            : new List<TestimonialSubmission>();

        return new PublicWebsiteViewModel
        {
            Website = site,
            Pages = pages,
            CurrentPage = current,
            ApprovedTestimonials = testimonials,
            PollResultsByBlockId = await PollResultsBuilder.BuildAsync(db, agentId, current, isOwnerPreview: true),
            PollVoteByBlockId = await PollVoteBuilder.BuildAsync(db, agentId, current, new HashSet<int>(), isOwnerPreview: true),
            FormsByBlockId = await PublicFormBuilder.BuildAsync(db, agentId, current),
            DidYouKnowByBlockId = await DidYouKnowBuilder.BuildAsync(db, agentId, current),
            ArticleContentByBlockId = await ArticleContentBuilder.BuildAsync(db, agentId, current),
            BlogByBlockId = await BlogBuilder.BuildAsync(db, agentId, current, 0)
        };
    }
}
