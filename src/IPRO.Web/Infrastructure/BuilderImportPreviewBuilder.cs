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
        IPRODbContext db, WebsiteBuilderImport import, string? pageSlug, int? design, string? without, string? keep = null, string? sections = null,
        string? moved = null)
    {
        // The same reading of "12,15" the review screen uses, so a look shows the choices it was opened with.
        var planned = await BuilderImportService.PlanAsync(db, import, BuilderImportService.ParseIds(without), BuilderImportService.ParseIds(keep),
            BuilderImportService.ParseIds(sections), BuilderImportService.ParseMoves(moved));
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
        var asked = slug.Length == 0 ? null : all.FirstOrDefault(p => string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase));
        var current = asked ?? all.FirstOrDefault(p => p.IsHomePage) ?? all.FirstOrDefault();
        if (current == null) return null;
        // 585: a link to a page the look does not have shows the home page, and the banner says so (only
        // an address of a page's shape is repeated back).
        var missing = slug.Length > 0 && asked == null && System.Text.RegularExpressions.Regex.IsMatch(slug, "\\A[a-z0-9][a-z0-9-]{0,59}\\z") ? slug : null;

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

        // 576: a replaced page drawn in the customer's own design can look like the page it replaces
        // (the same header, colours and kind of sections), so the look says which it is.
        var item = plan.Pages.First(p => ReferenceEquals(p.Page, current));
        return new PublicWebsiteViewModel
        {
            Website = site,
            Pages = pages,
            CurrentPage = current,
            PreviewNote = item.Change switch
            {
                // 581: a page at a new address, taken for the page it was.
                PageChange.Replaced when item.IsMoved =>
                    $"THIS PAGE COMES FROM THE PACKAGE, at a new address: it would replace the page at /{item.MovedFrom} and keep that page's search settings. Header, menu, colours and fonts are the site's own design, not the builder's.",
                PageChange.Replaced => "THIS PAGE COMES FROM THE PACKAGE and would replace the one the site has now. Header, menu, colours and fonts are the site's own design, not the builder's.",
                PageChange.Added => "THIS PAGE IS NEW, from the package. Header, menu, colours and fonts are the site's own design, not the builder's.",
                _ => "THIS PAGE IS NOT IN THE PACKAGE: it stays exactly as it is now."
            },
            PreviewMissingPage = missing,
            ApprovedTestimonials = testimonials,
            PollResultsByBlockId = await PollResultsBuilder.BuildAsync(db, agentId, current, isOwnerPreview: true),
            PollVoteByBlockId = await PollVoteBuilder.BuildAsync(db, agentId, current, new HashSet<int>(), isOwnerPreview: true),
            FormsByBlockId = await PublicFormBuilder.BuildAsync(db, agentId, current),
            DidYouKnowByBlockId = await DidYouKnowBuilder.BuildAsync(db, agentId, current),
            ArticleContentByBlockId = await ArticleContentBuilder.BuildAsync(db, agentId, current),
            BlogByBlockId = await BlogBuilder.BuildAsync(db, agentId, current, 0),
            PictureDescriptions = await PictureDescriptions.ForPageAsync(db, site.Id, current)
        };
    }
}
