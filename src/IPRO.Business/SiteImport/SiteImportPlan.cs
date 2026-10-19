using System.Security.Cryptography;
using System.Text;
using IPRO.Entities;

namespace IPRO.Business.SiteImport;

// 571: what bringing a package in would do to a site, worked out without touching anything. The
// review screen shows it, the look renders it and BuilderImportService writes exactly it, so the
// three cannot disagree.
public enum PageChange
{
    Added,      // in the package, not on the site
    Replaced,   // in the package and on the site: the page keeps its row and its search settings
    Kept,       // on the site, not in the package: left as it is
    Removed     // on the site, not in the package, and it goes
}

// 576: a section a replaced page has on the site today that the package does not bring back. The
// review names each one and offers to keep it; BlockId, the section's own row, is what the review
// posts back for that. RemovedInBuilder: it is a section the hand-over sent out, and its locked box
// is nowhere in the package. Otherwise the package never had it: for a site built from scratch, any
// section the builder cannot carry; for a return, one added in iPro since. Says: what the section
// says, as one value, so that the write can tell a section changed after the review was read.
public sealed record SectionGoing(int BlockId, string BlockType, string Title, bool RemovedInBuilder, bool Kept, string Says = "");

// 577: a page whose menu label went to the builder shortened (SiteHandoverBuilder.BuilderLabel). Slug:
// the page's address at the hand-over; RowId: its row today (0 when the site no longer has it); Label:
// its own label at the hand-over; SentLabel: what the builder was given. OnlyLabelChanged: the page was
// changed in iPro since, and its menu label is all that changed.
public sealed record ShortenedLabel(string Slug, int RowId, string Label, string SentLabel, bool OnlyLabelChanged = false);

// 581: how the plan knew which page of the site a package page at a new address is.
public enum MoveMatch
{
    None,
    ShortenedLabel,   // it carries the menu label the builder was given shortened for that page (577)
    SameTitle,        // it has that page's title, and no other such page on either side has it
    SameMenuLabel,    // it has that page's menu label, and no other such page on either side has it
    Chosen            // the operator said so on the review
}

// 581: a page of the site that went to the builder and did not come back at its address: a page of
// the package at a new address can be it. RowId is its row, Slug its address on the site today.
public sealed record MoveCandidate(int RowId, string Slug, string Title);

public sealed class PlannedPage
{
    // The page as it will be, sections included (for a removed page: as it is now). A page that does
    // not exist yet, and every new section, carries a negative Id until it is written.
    public WebsitePage Page { get; init; } = null!;
    public PageChange Change { get; init; }
    // The section types the page has on the site today (empty for an added page).
    public IReadOnlyList<string> SectionsBefore { get; init; } = Array.Empty<string>();
    // 572: the page was handed to the builder and did not come back: it was removed there. (A page
    // made in iPro after the hand-over was never in the builder, and is simply kept.)
    public bool RemovedInBuilder { get; init; }
    // When the page was last changed on the site (null for an added page). A returning package
    // replaces whatever was changed in iPro after its hand-over; the review says which pages.
    public DateTime? ChangedOnSite { get; init; }
    // 574: what the page says today is not what its hand-over recorded: it was changed in iPro after the
    // site went to the builder, and this return replaces (or removes) that change. Decided here, from
    // the content, and not in the view from a timestamp: the page editor stamps a saved section and not
    // its page, and adding, deleting or moving a section stamps nothing. 577: false when the one change
    // is a menu label that this return keeps (LabelKept), wherever the page lands.
    public bool ChangedSinceHandover { get; init; }

    // Sections a replaced or removed page has today that this package could not have carried (a map,
    // a gallery, a price list; for a site built from scratch also a form or an article section, which
    // only a hand-over can hold). They do not come back with the package.
    // 576: for a replaced page, the kinds of the sections that really go (SectionsGoing, less the
    // ones the operator keeps). A section whose locked box came back is not among them.
    public IReadOnlyList<string> SectionsLost { get; init; } = Array.Empty<string>();
    // 576: the sections a replaced page has today that the package does not bring back, one by one,
    // each with the operator's choice. A kept one stays as the site has it, after the package's sections.
    public IReadOnlyList<SectionGoing> SectionsGoing { get; init; } = Array.Empty<SectionGoing>();
    // 576: what a reviewer could not tell from the list of sections alone. A locked box that comes
    // back SHOWN though its section is hidden on the site, one that comes from another page, one that
    // is a copy, one whose section the site no longer has; and, on a page that is not replaced, a
    // section that comes back elsewhere in its box. In plain sentences.
    public IReadOnlyList<string> BoxNotes { get; init; } = Array.Empty<string>();
    // 577: the menu label the builder was given for this page, shortened ("" = it had the page's own).
    // LabelKept: it came back as it was given, so the page keeps its own label (Page.NavigationLabel).
    // Otherwise what came back is written: the designer's label, or, for a page with no label of its
    // own, the shortened one (Page.NavigationLabel equals LabelSent then).
    public string LabelSent { get; init; } = string.Empty;
    public bool LabelKept { get; init; }
    // 581: a page the designer gave a new address, on the row of the page it was. MovedFrom: that page's
    // address on the site ("" for every other page); MovedBy: how the plan knew (None: not moved). It
    // keeps what the builder never showed, as any replaced page does, and MovedFrom joins its old
    // addresses, so links to it keep arriving (553). OldAddressKept: they do, from the moment it is
    // brought in; when they do not, OldAddressNote says why in a plain sentence (the page is not
    // published, it has no room for another old address, another page has that address). MoveNotes:
    // what it keeps that a visitor would notice and the designer could not see (not published, not in
    // the menu, a language of its own).
    public string MovedFrom { get; init; } = string.Empty;
    public MoveMatch MovedBy { get; init; }
    public bool IsMoved => MovedBy != MoveMatch.None;
    public bool OldAddressKept { get; init; }
    public string OldAddressNote { get; init; } = string.Empty;
    public IReadOnlyList<string> MoveNotes { get; init; } = Array.Empty<string>();
    // 581: for a removed page, in plain sentences, what goes with it that its list of sections does not
    // show: its search title and description, its own language, the old addresses it answers to, that it
    // is hidden from visitors or from the menu, and where links to its address go afterwards.
    public IReadOnlyList<string> SettingsLost { get; init; } = Array.Empty<string>();
    // 585 (the owner's bench walk-through): for a replaced page of a return, what bringing it in changes on
    // it, in plain sentences (PageChanges), apart from the sections named one by one (SectionsGoing).
    // Empty: nothing a visitor sees changes. Null: not compared (a page that is not replaced, and every
    // page of a site built from scratch, whose sections all give way to the package's).
    public IReadOnlyList<string>? Changes { get; init; }
    // 585: a replaced page of a return on which nothing changes and nothing needs a word: the reviews
    // fold it away under "No change".
    public bool Unchanged => Change == PageChange.Replaced && Changes is { Count: 0 } && !IsMoved && SectionsGoing.Count == 0
                             && BoxNotes.Count == 0 && !ChangedSinceHandover && (LabelSent.Length == 0 || LabelKept);
}

public enum LogoChange { Unchanged, Set, Replaced, Removed }

public sealed class SiteImportPlan
{
    public IReadOnlyList<PlannedPage> Pages { get; init; } = Array.Empty<PlannedPage>();
    // What stops the package from being brought in as chosen. Empty = it can go in.
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    public string LogoUrlAfter { get; init; } = string.Empty;
    public LogoChange Logo { get; init; }
    // 574: the site's logo is not the one that was handed over, and this return changes it.
    public bool LogoChangedSinceHandover { get; init; }
    // 574: what this plan would do, as one value. The review screen posts it back with "bring it in";
    // if the site has changed in between so that the plan is no longer the one that was read, the
    // write refuses and the review is shown again.
    public string Fingerprint { get; init; } = string.Empty;
    // 581: the pages of the site a package page at a new address can be said to be (empty for a site
    // built from scratch, and when every page that went to the builder came back at its address).
    public IReadOnlyList<MoveCandidate> MoveCandidates { get; init; } = Array.Empty<MoveCandidate>();

    // 581: how many old addresses a page answers to. The public site reads no more
    // (IPRO.Web.Infrastructure.OldAddresses.MaxEntries; a test holds the two together).
    public const int OldAddressLimit = 12;

    // The site's pages afterwards, in menu order, as the public site would load them.
    public IReadOnlyList<WebsitePage> PagesAfter =>
        Pages.Where(p => p.Change != PageChange.Removed).Select(p => p.Page).ToList();

    // removePageIds: pages the operator chose to remove. handedOverPageIds: the pages a hand-over
    // carried (empty for a site built from scratch); one of those missing from the package was
    // removed in the builder and goes, unless the operator keeps it (keepPageIds).
    public static SiteImportPlan Build(
        BuilderTemplate template,
        IReadOnlyDictionary<string, string> imageUrls,
        AgentWebsite site,
        IReadOnlyList<WebsitePage> currentPages,
        IReadOnlySet<int> removePageIds,
        IReadOnlySet<int>? handedOverPageIds = null,
        IReadOnlySet<int>? keepPageIds = null,
        IEnumerable<string>? knownProblems = null,
        // 574, for a return. handedOverRowBySlug: the address a page had when it was handed over, to the
        // row it is today (a page renamed in iPro since is still that page). changedSinceHandover: the
        // rows whose content is not what the hand-over recorded. carriedTypes: the section types this
        // package could carry (default: the contract's eight).
        IReadOnlyDictionary<string, int>? handedOverRowBySlug = null,
        IReadOnlySet<int>? changedSinceHandover = null,
        bool logoChangedSinceHandover = false,
        IReadOnlySet<string>? carriedTypes = null,
        // 576. hostSections: the sections the hand-over sent out as locked boxes, each as it was.
        // keepSectionIds: the rows of the sections the operator keeps (SectionGoing.BlockId).
        IReadOnlyList<HandoverHostSection>? hostSections = null,
        IReadOnlySet<int>? keepSectionIds = null,
        // 577. shortenedLabels: the pages whose menu label the builder was given shortened.
        IReadOnlyList<ShortenedLabel>? shortenedLabels = null,
        // 581. movedFrom: the operator's word on a package page at a new address, by that address: the
        // row of the page it is (MoveCandidate.RowId), or 0 for a new page. A page not named is
        // matched as the plan can tell.
        IReadOnlyDictionary<string, int>? movedFrom = null,
        // 585. sectionsAtHandover: for a page that is the row it was at the hand-over, the rows of the
        // sections it had then. A section of a kind the builder carries that is not among them was added
        // in iPro after the hand-over: the package cannot have it, so it is named like a host section
        // the package does not bring back, and can be kept.
        IReadOnlyDictionary<int, IReadOnlySet<int>>? sectionsAtHandover = null)
    {
        bool Carried(string type) => carriedTypes == null ? BuilderContract.BlockTypes.Contains(type, StringComparer.Ordinal) : carriedTypes.Contains(type);
        IReadOnlyList<string> Lost(IReadOnlyList<string> before) => before.Where(t => !Carried(t)).ToList();
        var hosts = hostSections ?? Array.Empty<HandoverHostSection>();
        var hostById = hosts.GroupBy(h => h.Id).ToDictionary(g => g.Key, g => g.First());
        // The boxes that came back, wherever in the site the designer put them, and how many times each.
        var timesBack = template.Pages.SelectMany(p => p.Blocks).Where(b => b.Type == BuilderContract.HostSection)
            .GroupBy(b => b.HostSectionId).ToDictionary(g => g.Key, g => g.Count());
        var unknownBoxes = new SortedSet<int>();
        var problems = new List<string>(knownProblems ?? Array.Empty<string>());
        var nextId = -1;
        int TempId() => nextId--;
        string Image(string imageRef) => imageRef.Length > 0 && imageUrls.TryGetValue(imageRef, out var url) ? url : string.Empty;
        foreach (var missing in template.UsedImageRefs.Where(r => !imageUrls.ContainsKey(r)).Take(1))
        {
            // Without this a page would be written with its pictures quietly gone.
            problems.Add($"The record of where this package's pictures were stored is incomplete ({missing}). Drop the package and upload it again.");
        }

        // 1. Which row of the site each package page lands on. The home page is the home page,
        //    whatever either side calls it; every other page is matched by its address.
        var rowFor = new Dictionary<BuilderPage, WebsitePage>();
        var taken = new HashSet<int>();
        var packageHome = template.Pages.FirstOrDefault(p => p.IsHomePage);
        var siteHome = currentPages.FirstOrDefault(p => p.IsHomePage);
        if (packageHome != null && siteHome != null)
        {
            rowFor[packageHome] = siteHome;
            taken.Add(siteHome.Id);
        }
        foreach (var page in template.Pages.Where(p => !rowFor.ContainsKey(p)))
        {
            var row = currentPages.FirstOrDefault(r => !taken.Contains(r.Id) && string.Equals(r.Slug, page.Slug, StringComparison.OrdinalIgnoreCase));
            if (row == null) continue;
            rowFor[page] = row;
            taken.Add(row.Id);
        }
        // 574: a page given another address in iPro after the hand-over is still the page that went to
        // the builder. Matched by the address it had then, it is replaced on its own row (and keeps
        // its search settings); before, it was removed and the package's page added beside the gap.
        if (handedOverRowBySlug != null)
        {
            foreach (var page in template.Pages.Where(p => !rowFor.ContainsKey(p)))
            {
                if (!handedOverRowBySlug.TryGetValue(page.Slug, out var rowId) || taken.Contains(rowId)) continue;
                var row = currentPages.FirstOrDefault(r => r.Id == rowId);
                if (row == null) continue;
                rowFor[page] = row;
                taken.Add(row.Id);
            }
        }

        // 1a (581). A page the designer gave a new address. A package carries no page's identity, only
        // its address, so such a page matched nothing above. It used to come back as a new page with
        // iPro's defaults while the page it was went as "removed in the builder", and with it everything
        // the builder never showed: its search title and description, whether it is published and in
        // the menu, its language, its old addresses; and its address stopped arriving. What the designer
        // could not have meant to change is not changed: it lands on that page's row when the plan can
        // tell which page it is. First by the shortened menu label the builder was given for it (577),
        // which iPro wrote and nothing else carries; then by its title, then by its menu label, each only
        // when it points one way (one such page of the site, one package page at a new address). The
        // operator's word on the review decides over all three. Only a return has such pages: one that
        // went to the builder and whose row nothing above took.
        var candidates = handedOverPageIds == null ? new List<WebsitePage>()
            : currentPages.Where(r => handedOverPageIds.Contains(r.Id) && !taken.Contains(r.Id)).OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToList();
        var movedTo = new Dictionary<BuilderPage, (WebsitePage Row, MoveMatch By)>(ReferenceEqualityComparer.Instance);
        if (candidates.Count > 0)
        {
            var movable = template.Pages.Where(p => !rowFor.ContainsKey(p) && !p.IsHomePage).ToList();
            var guessed = GuessMoves(movable, template.Pages, candidates, shortenedLabels ?? Array.Empty<ShortenedLabel>());
            var said = movedFrom ?? new Dictionary<string, int>();
            bool Repeats(BuilderPage page) => guessed.TryGetValue(page, out var guess) && said.GetValueOrDefault(page.Slug) == guess.Row.Id;
            // A choice the operator made comes before one that only repeats the plan's reading (the form
            // posts every page's choice), so the reading cannot take the row the operator gave another page.
            foreach (var page in movable.OrderBy(Repeats))
            {
                if (!said.TryGetValue(page.Slug, out var rowId) || rowId == 0) continue;
                // A row that is not one of these any more reads as "a new page": the review then shows that.
                var row = candidates.FirstOrDefault(r => r.Id == rowId);
                if (row == null) continue;
                var other = movedTo.FirstOrDefault(m => m.Value.Row.Id == row.Id).Key;
                if (other != null)
                {
                    problems.Add($"\"{other.Title}\" and \"{page.Title}\" are both said to be the page that was at \"/{row.Slug}\". Choose it for one of them only.");
                    continue;
                }
                // The operator's word that agrees with the plan's own reading is that reading.
                movedTo[page] = (row, Repeats(page) ? guessed[page].By : MoveMatch.Chosen);
            }
            foreach (var (page, guess) in guessed)
            {
                if (said.ContainsKey(page.Slug) || movedTo.Values.Any(m => m.Row.Id == guess.Row.Id)) continue;
                movedTo[page] = guess;
            }
            foreach (var (page, move) in movedTo)
            {
                rowFor[page] = move.Row;
                taken.Add(move.Row.Id);
            }
        }

        // 1b (576). The sections of the site the builder cannot carry, and which of them each locked box is.
        //
        // A box that comes back brings the section AS THE SITE HAS IT NOW, wherever a section of the
        // site can be told to be the one that went out. Nothing in a box could be changed in the
        // builder, so the hand-over's copy is never better than the site's own: using the site's means
        // an edit the customer made meanwhile is not undone, and a section that was only saved again
        // cannot be taken for a new one and come back twice. The hand-over's record is what is written
        // only when the site no longer has the section.
        //
        // Which section a box is, in this order: the one on the row the hand-over recorded for it;
        // one that still says what was recorded (rows do not survive a put-back or an earlier return
        // of the same hand-over; what a section says does); one of its kind with its heading (the
        // section, changed since, when its row is not the recorded one any more); and last, for a box
        // whose section no box has found, one of its kind on the page the box stands on, which the
        // review then says in a note. Each is tried on the box's own page first, then anywhere. A
        // section is one box's at most.
        var foreign = currentPages
            .SelectMany(r => r.Blocks.Where(b => !Carried(b.BlockType)).Select(b => (Row: r, Block: b)))
            .OrderBy(x => x.Row.SortOrder).ThenBy(x => x.Row.Id).ThenBy(x => x.Block.SortOrder).ThenBy(x => x.Block.Id)
            .ToList();
        var boxes = template.Pages
            .SelectMany(p => p.Blocks.Where(b => b.Type == BuilderContract.HostSection && hostById.ContainsKey(b.HostSectionId))
                .OrderBy(b => b.SortOrder).Select(b => (Page: p, Box: b)))
            .ToList();
        // By the box itself, not by what it says: two boxes of one section read the same.
        var sectionOf = new Dictionary<BuilderBlock, (WebsitePage Row, WebsiteContentBlock Block)>(ReferenceEqualityComparer.Instance);
        var accounted = new HashSet<int>();
        var byKindAlone = new HashSet<BuilderBlock>(ReferenceEqualityComparer.Instance);
        bool Found(int hostId) => boxes.Any(x => x.Box.HostSectionId == hostId && sectionOf.ContainsKey(x.Box));
        void Match(bool samePage, Func<HandoverHostSection, WebsiteContentBlock, bool> isIt, bool lastResort = false)
        {
            foreach (var (boxPage, box) in boxes)
            {
                if (sectionOf.ContainsKey(box)) continue;
                var host = hostById[box.HostSectionId];
                // The same box a second time is a copy: it is not another section of that kind.
                if (lastResort && Found(host.Id)) continue;
                rowFor.TryGetValue(boxPage, out var boxRow);
                foreach (var candidate in foreign)
                {
                    if (accounted.Contains(candidate.Block.Id) || (samePage && candidate.Row.Id != boxRow?.Id) || !isIt(host, candidate.Block)) continue;
                    sectionOf[box] = candidate;
                    accounted.Add(candidate.Block.Id);
                    if (lastResort) byKindAlone.Add(box);
                    break;
                }
            }
        }
        bool RecordedRow(HandoverHostSection host, WebsiteContentBlock block) => host.BlockId > 0 && host.BlockId == block.Id && host.BlockType == block.BlockType;
        bool KindAndHeading(HandoverHostSection host, WebsiteContentBlock block) =>
            host.BlockType == block.BlockType && string.Equals((host.Heading ?? string.Empty).Trim(), (block.Heading ?? string.Empty).Trim(), StringComparison.Ordinal);
        Match(samePage: true, RecordedRow);
        Match(samePage: false, RecordedRow);
        Match(samePage: true, (host, block) => host.Says(block));
        Match(samePage: false, (host, block) => host.Says(block));
        Match(samePage: true, KindAndHeading);
        Match(samePage: false, KindAndHeading);
        Match(samePage: true, (host, block) => host.BlockType == block.BlockType, lastResort: true);
        // The same box a second time has no section of its own: it is a copy of the section as it is now.
        var liveOf = boxes.Where(x => sectionOf.ContainsKey(x.Box)).GroupBy(x => x.Box.HostSectionId)
            .ToDictionary(g => g.Key, g => sectionOf[g.First().Box].Block);
        // A section of a page that is not replaced, when a box somewhere in the package is that section.
        IReadOnlyList<string> ElsewhereNotes(WebsitePage row, bool stays) => foreign
            .Where(x => x.Row.Id == row.Id && accounted.Contains(x.Block.Id))
            .Select(x => Named(x.Block.BlockType, x.Block.Heading) +
                (stays ? " also comes back on another page, in its box: it will stand on both." : " comes back on another page, in its box."))
            .ToList();

        // 1c (577). The pages whose menu label the builder was given shortened, and which package page
        // each one is. First by the shortened label itself, which iPro wrote and nothing else looks like:
        // the one page that carries it word for word, when it is one record's alone and that page lands
        // on that record's row or on none (it was given another address, or the designer made another
        // page the home page, which then lands on the home row whatever either side calls it). A page
        // that lands on another page's row is that page, whatever label it carries, unless it is there
        // only as a page at a new address (1a): then the shortened label is still that record's, and
        // comes back whole rather than cut (581: the operator may say a page is another than the one whose
        // label it carries). Then, for a label the designer changed or one shared, the page on its row at
        // its address, or still carrying the label; and last the page at its address.
        var labels = shortenedLabels ?? Array.Empty<ShortenedLabel>();
        var labelOf = new Dictionary<BuilderPage, ShortenedLabel>(ReferenceEqualityComparer.Instance);
        var labelTaken = new HashSet<ShortenedLabel>(ReferenceEqualityComparer.Instance);
        void Claim(BuilderPage page, ShortenedLabel? found)
        {
            if (found != null && !labelOf.ContainsKey(page) && labelTaken.Add(found)) labelOf[page] = found;
        }
        foreach (var page in template.Pages)
        {
            var records = labels.Where(l => string.Equals(l.SentLabel, page.NavigationLabel, StringComparison.Ordinal)).ToList();
            var carriers = template.Pages.Count(p => string.Equals(p.NavigationLabel, page.NavigationLabel, StringComparison.Ordinal));
            if (records.Count != 1 || carriers != 1) continue;
            if (!rowFor.TryGetValue(page, out var row) || row.Id == records[0].RowId || movedTo.ContainsKey(page)) Claim(page, records[0]);
        }
        foreach (var page in template.Pages)
        {
            if (!rowFor.TryGetValue(page, out var row)) continue;
            Claim(page, labels.FirstOrDefault(l => l.RowId == row.Id && !labelTaken.Contains(l)
                && (string.Equals(l.Slug, page.Slug, StringComparison.OrdinalIgnoreCase) || string.Equals(l.SentLabel, page.NavigationLabel, StringComparison.Ordinal))));
        }
        foreach (var page in template.Pages)
        {
            Claim(page, labels.FirstOrDefault(l => !labelTaken.Contains(l) && string.Equals(l.Slug, page.Slug, StringComparison.OrdinalIgnoreCase)));
        }

        // What each of those pages' menu label becomes. One that came back exactly as the builder was
        // given it is the page's own label: as the site has it now (a label the customer changed in
        // iPro meanwhile stays changed), or as it was, when the site no longer has the page. What the
        // designer could not have meant to change is not changed; a label they changed is written. (A
        // page with no label of its own, which the page editor never saves, keeps what came back, and
        // the review says so.) labelCarried: the rows whose one change in iPro since the hand-over, their
        // label, goes on this way, on whichever page it lands: nothing of that change is lost.
        var labelAfter = new Dictionary<BuilderPage, (string Label, bool Kept)>(ReferenceEqualityComparer.Instance);
        var labelCarried = new HashSet<int>();
        foreach (var (page, label) in labelOf)
        {
            var own = currentPages.FirstOrDefault(r => r.Id == label.RowId)?.NavigationLabel ?? label.Label;
            var kept = string.Equals(page.NavigationLabel, label.SentLabel, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(own);
            labelAfter[page] = (kept ? own : page.NavigationLabel, kept);
            if (kept && label.OnlyLabelChanged) labelCarried.Add(label.RowId);
        }

        // The site's other pages, and which of them go (3, below): known here already, so that where a
        // link goes afterwards can be said (581).
        var others = currentPages.Where(r => !taken.Contains(r.Id)).ToList();
        var removedInBuilder = others
            .Where(r => handedOverPageIds != null && handedOverPageIds.Contains(r.Id) && !(keepPageIds?.Contains(r.Id) ?? false))
            .Select(r => r.Id).ToHashSet();
        var removed = others.Where(r => removePageIds.Contains(r.Id) || removedInBuilder.Contains(r.Id)).Select(r => r.Id).ToHashSet();
        // 581: where a link to an address of the site goes afterwards. A page that lives at the address
        // comes first (the package's pages and the pages that stay); else a page that stays and lists it
        // among its old addresses (553), as the public site reads them; else nothing.
        var livesAt = template.Pages.GroupBy(p => p.Slug, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Title, StringComparer.OrdinalIgnoreCase);
        foreach (var row in others.Where(r => !removed.Contains(r.Id))) livesAt.TryAdd(row.Slug, row.Title);
        var titleAfter = rowFor.ToDictionary(m => m.Value.Id, m => m.Key.Title);
        string? ListedBy(string address, int exceptRowId) => currentPages
            .Where(r => r.Id != exceptRowId && !removed.Contains(r.Id) && Addresses(r.OldAddresses).Take(OldAddressLimit).Contains(address, StringComparer.OrdinalIgnoreCase))
            .Select(r => titleAfter.GetValueOrDefault(r.Id) ?? r.Title).FirstOrDefault();

        // 2. The package's pages as they will be.
        var planned = new List<PlannedPage>();
        var bySlug = new Dictionary<string, WebsitePage>(StringComparer.Ordinal);
        foreach (var source in template.Pages)
        {
            rowFor.TryGetValue(source, out var row);
            // 577: the menu label, as 1c worked it out for a page whose label went shortened.
            var shortLabel = labelOf.GetValueOrDefault(source);
            var (navigationLabel, labelKept) = labelAfter.TryGetValue(source, out var labelled) ? labelled : (source.NavigationLabel, false);
            // 581 (re-check): the address a page leaves joins its old addresses only when no other page
            // has it afterwards or already lists it: the page editor would never store such an address,
            // and the customer's next save would warn about it or drop it from the other page.
            var oldAddresses = row != null && !string.Equals(row.Slug, source.Slug, StringComparison.OrdinalIgnoreCase)
                    && (livesAt.ContainsKey(row.Slug) || ListedBy(row.Slug, row.Id) != null)
                ? row.OldAddresses ?? string.Empty
                : OldAddressesAfter(row, source.Slug);
            // 581: a page at a new address on the row of the page it was (1a): whether links to the
            // address it had arrive, and what it keeps that a visitor would notice.
            var moved = movedTo.TryGetValue(source, out var move);
            var oldAddressNote = string.Empty;
            var moveNotes = new List<string>();
            if (moved)
            {
                var from = move.Row.Slug;
                var listed = ListedBy(from, move.Row.Id);
                if (livesAt.TryGetValue(from, out var livesThere))
                    oldAddressNote = $"Links to /{from} reach \"{livesThere}\", the page at that address afterwards, not this one.";
                else if (listed != null)
                    oldAddressNote = $"Links to /{from} may go to \"{listed}\" instead: it already lists that address among its old addresses. Remove it there in the page editor (Page Settings, Old addresses) if they should arrive here.";
                else if (!Addresses(oldAddresses).Take(OldAddressLimit).Contains(from, StringComparer.OrdinalIgnoreCase))
                    oldAddressNote = $"Links to /{from} will find nothing: the page already lists {OldAddressLimit} old addresses, the most a page answers to. " +
                        $"Afterwards, remove one in the page editor (Page Settings, Old addresses) and add /{from} there.";
                else if (!move.Row.IsPublished)
                    oldAddressNote = $"Links to /{from} will be sent on to /{source.Slug} once the page is published; until then they find nothing, as the page itself does.";
                if (!move.Row.IsPublished) moveNotes.Add("On the site it is not published, and it stays so: visitors see it only once the customer publishes it.");
                if (!move.Row.ShowInNavigation) moveNotes.Add("It is not in the menu on the site, and it stays out of it.");
                var language = WebsiteLanguages.NormalizePage(move.Row.Language);
                if (language.Length > 0) moveNotes.Add($"It keeps its own language ({LanguageName(language)}).");
            }
            var page = new WebsitePage
            {
                Id = row?.Id ?? TempId(),
                AgentWebsiteId = site.Id,
                Title = source.Title,
                Slug = source.Slug,
                NavigationLabel = navigationLabel,
                IsHomePage = source.IsHomePage,
                SortOrder = source.SortOrder,
                // What the contract does not carry stays the page's own (section 12); a new page
                // gets iPro's defaults.
                MetaTitle = row?.MetaTitle ?? string.Empty,
                MetaDescription = row?.MetaDescription ?? string.Empty,
                OldAddresses = oldAddresses,
                Language = row?.Language ?? string.Empty,
                ShowInNavigation = row?.ShowInNavigation ?? true,
                IsPublished = row?.IsPublished ?? true,
                CreatedAt = row?.CreatedAt ?? DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            page.Blocks = new List<WebsiteContentBlock>();
            var boxNotes = new List<string>();
            foreach (var b in source.Blocks.OrderBy(x => x.SortOrder))
            {
                WebsiteContentBlock block;
                if (b.Type == BuilderContract.HostSection)
                {
                    // 576: a locked box becomes the section it stands for (1b, above), at the place the
                    // box has. It is never stored as a box.
                    if (!hostById.TryGetValue(b.HostSectionId, out var host))
                    {
                        unknownBoxes.Add(b.HostSectionId);
                        continue;
                    }
                    var own = sectionOf.TryGetValue(b, out var its);
                    var onSite = own ? its.Block : liveOf.GetValueOrDefault(host.Id);
                    // Hidden or shown as the site has it, unless the designer changed that on the box.
                    var wasShown = onSite?.IsVisible ?? host.IsVisible;
                    var shown = b.IsVisible == host.IsVisible ? wasShown : b.IsVisible;
                    block = onSite == null ? host.ToBlock(b.SortOrder, shown) : HandoverHostSection.Copy(onSite, b.SortOrder);
                    block.IsVisible = shown;
                    // What a reviewer cannot tell from the list of sections: nobody could look at this
                    // section in the builder, and here it changes what visitors see.
                    var name = Named(block.BlockType, block.Heading);
                    if (onSite == null)
                    {
                        var otherwise = shown == host.IsVisible ? string.Empty : shown ? ", but SHOWN though it was hidden" : ", but HIDDEN though it was shown";
                        boxNotes.Add($"{name} is not on the site any more as it was handed over: it comes back with its box, as it was then{otherwise}.");
                    }
                    else
                    {
                        if (shown && !wasShown) boxNotes.Add($"{name} is HIDDEN on the site and comes back shown.");
                        if (!shown && wasShown) boxNotes.Add($"{name} is shown on the site and comes back HIDDEN.");
                        if (own && its.Row.Id != row?.Id) boxNotes.Add($"{name} comes from another page (/{its.Row.Slug}).");
                        if (!own) boxNotes.Add($"{name} comes back as a copy: the package has it {timesBack[host.Id]} times.");
                        if (byKindAlone.Contains(b))
                        {
                            boxNotes.Add($"{name} is taken for the section this box stood for ({Named(host.BlockType, host.Heading)}): that one is not on the site any more as it was handed over, and this is the one of its kind on this page.");
                        }
                    }
                }
                else
                {
                    block = new WebsiteContentBlock
                    {
                        BlockType = b.Type,
                        Heading = b.Heading,
                        Subheading = b.Subheading,
                        Body = b.Body,
                        ImageUrl = Image(b.ImageRef),
                        ButtonText = b.ButtonText,
                        ButtonUrl = b.ButtonUrl,
                        SettingsJson = b.SettingsJson,
                        LayoutVariant = b.LayoutVariant,
                        SortOrder = b.SortOrder,
                        IsVisible = b.IsVisible
                    };
                }
                block.Id = TempId();
                block.WebsitePageId = page.Id;
                page.Blocks.Add(block);
            }

            // 576: what this page has on the site today that the package does not bring back: every
            // section the builder cannot carry that is no box's (1b). For a site built from scratch
            // that is all of them. Each is named by its own row, and a kept one stays as the site has it.
            var going = new List<SectionGoing>();
            var fromPackage = page.Blocks.Count;
            if (row != null)
            {
                var next = page.Blocks.Count == 0 ? 0 : page.Blocks.Max(x => x.SortOrder) + 1;
                foreach (var (_, current) in foreign.Where(x => x.Row.Id == row.Id && !accounted.Contains(x.Block.Id)))
                {
                    // Surely removed in the builder: a section the hand-over sent out, whose box is nowhere in the package.
                    // (Of several records it could be, the one whose box is missing is the one that explains it.)
                    var was = hosts.FirstOrDefault(h => RecordedRow(h, current))
                        ?? hosts.Where(h => h.Says(current)).OrderBy(h => timesBack.ContainsKey(h.Id)).FirstOrDefault()
                        ?? hosts.Where(h => KindAndHeading(h, current)).OrderBy(h => timesBack.ContainsKey(h.Id)).FirstOrDefault();
                    var kept = keepSectionIds?.Contains(current.Id) ?? false;
                    going.Add(new SectionGoing(current.Id, current.BlockType, current.Heading ?? string.Empty,
                        RemovedInBuilder: was != null && !timesBack.ContainsKey(was.Id), Kept: kept, Says: SiteFingerprint.OfSection(current)));
                    if (!kept) continue;
                    var copy = HandoverHostSection.Copy(current, next++);
                    copy.Id = TempId();
                    copy.WebsitePageId = page.Id;
                    page.Blocks.Add(copy);
                }
                // 585: a section of a kind the builder carries, added in iPro after the hand-over.
                if (sectionsAtHandover != null && sectionsAtHandover.TryGetValue(row.Id, out var then))
                {
                    foreach (var added in row.Blocks.Where(x => Carried(x.BlockType) && !then.Contains(x.Id)).OrderBy(x => x.SortOrder).ThenBy(x => x.Id))
                    {
                        var kept = keepSectionIds?.Contains(added.Id) ?? false;
                        going.Add(new SectionGoing(added.Id, added.BlockType, added.Heading ?? string.Empty, RemovedInBuilder: false, Kept: kept,
                            Says: SiteFingerprint.OfSection(added)));
                        if (!kept) continue;
                        var copy = HandoverHostSection.Copy(added, next++);
                        copy.Id = TempId();
                        copy.WebsitePageId = page.Id;
                        page.Blocks.Add(copy);
                    }
                }
                if (going.Any(g => g.Kept) && page.Blocks.Count > BuilderContract.MaxBlocksPerPage)
                {
                    // The page editor has no limit of its own, but a page past the package's limit could
                    // never be handed to the builder again.
                    problems.Add($"\"{page.Title}\" would have {page.Blocks.Count} sections with the ones kept, and a page that goes to the builder holds at most " +
                        $"{BuilderContract.MaxBlocksPerPage}. Keep fewer sections on that page.");
                }
            }

            // 585: for a return, what this changes on the page, section by section, apart from the sections
            // named one by one with a choice (going, and the kept copies of them at the end of the page).
            IReadOnlyList<string>? changes = null;
            if (row != null && template.HandoverId.Length > 0)
            {
                var named = going.Select(g => g.BlockId).ToHashSet();
                changes = PageChanges.Between(row, row.Blocks.Where(x => !named.Contains(x.Id)), page, page.Blocks.Take(fromPackage));
            }

            bySlug[source.Slug] = page;
            planned.Add(new PlannedPage
            {
                Page = page,
                Change = row == null ? PageChange.Added : PageChange.Replaced,
                Changes = changes,
                SectionsBefore = row == null ? Array.Empty<string>() : SectionTypes(row),
                SectionsLost = going.Where(g => !g.Kept).Select(g => g.BlockType).ToList(),
                SectionsGoing = going,
                BoxNotes = boxNotes.Distinct().ToList(),
                ChangedOnSite = row?.UpdatedAt,
                // 577: a page whose only change in iPro is its menu label, which goes on (1c), loses nothing.
                ChangedSinceHandover = row != null && (changedSinceHandover?.Contains(row.Id) ?? false) && !labelCarried.Contains(row.Id),
                LabelSent = shortLabel?.SentLabel ?? string.Empty,
                LabelKept = labelKept,
                MovedFrom = moved ? move.Row.Slug : string.Empty,
                MovedBy = moved ? move.By : MoveMatch.None,
                OldAddressKept = moved && oldAddressNote.Length == 0,
                OldAddressNote = oldAddressNote,
                MoveNotes = moveNotes
            });
        }
        if (unknownBoxes.Count > 0)
        {
            // The door refuses such a package; this is for a record that changed underneath one already stored.
            problems.Add($"The package holds a locked box ({string.Join(", ", unknownBoxes)}) that this site's hand-over has no record of, so it cannot be brought in. " +
                "Drop it, hand the site over again and work from that package.");
        }
        foreach (var source in template.Pages.Where(p => p.ParentSlug != null))
        {
            bySlug[source.Slug].ParentPageId = bySlug.TryGetValue(source.ParentSlug!, out var parent) ? parent.Id : null;
        }

        // 3. The site's other pages: kept as they are, or removed -- because the operator said so,
        //    or because the page went to the builder and was removed there (others, removed: above).
        //    581: a removed page says where links to it go afterwards, and, when the package has new
        //    pages, that it was hidden (one of them may be it).
        var newPagesComeBack = planned.Any(p => p.Change == PageChange.Added);
        string LinksTo(WebsitePage row)
        {
            var slug = row.Slug.Trim('/');
            if (livesAt.TryGetValue(slug, out var title)) return $"Links to /{slug} reach \"{title}\", the page at that address afterwards.";
            var listed = ListedBy(slug, row.Id);
            return listed != null ? $"Links to /{slug} go to \"{listed}\", which lists that address among its old addresses." : $"Links to /{slug} will find nothing afterwards.";
        }
        var parentOf = currentPages.ToDictionary(r => r.Id, r => r.ParentPageId);
        foreach (var row in others)
        {
            if (removed.Contains(row.Id))
            {
                planned.Add(new PlannedPage
                {
                    Page = row, Change = PageChange.Removed, SectionsBefore = SectionTypes(row), SectionsLost = Lost(SectionTypes(row)),
                    RemovedInBuilder = removedInBuilder.Contains(row.Id), ChangedOnSite = row.UpdatedAt,
                    // 577: not when its one change, its menu label, goes on with the page at its new address.
                    ChangedSinceHandover = (changedSinceHandover?.Contains(row.Id) ?? false) && !labelCarried.Contains(row.Id),
                    BoxNotes = ElsewhereNotes(row, stays: false),
                    SettingsLost = SettingsOf(row, newPagesComeBack && removedInBuilder.Contains(row.Id), row.IsHomePage || string.IsNullOrWhiteSpace(row.Slug) ? null : LinksTo(row))
                });
                continue;
            }

            // A page whose parent goes moves up to that parent's parent, exactly as deleting a page in
            // the page editor does (WebsitePagesController.Delete).
            var parentId = row.ParentPageId;
            while (parentId.HasValue && removed.Contains(parentId.Value)) parentId = parentOf.GetValueOrDefault(parentId.Value);
            // A replaced parent may itself have moved: it keeps its row, so the link holds.
            var kept = new WebsitePage
            {
                Id = row.Id,
                AgentWebsiteId = row.AgentWebsiteId,
                ParentPageId = parentId,
                Title = row.Title,
                Slug = row.Slug,
                NavigationLabel = row.NavigationLabel,
                MetaTitle = row.MetaTitle,
                MetaDescription = row.MetaDescription,
                OldAddresses = row.OldAddresses,
                Language = row.Language,
                IsHomePage = false,   // the package's home page is the home page now
                ShowInNavigation = row.ShowInNavigation,
                IsPublished = row.IsPublished,
                SortOrder = row.SortOrder,
                CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt,
                Blocks = row.Blocks.OrderBy(b => b.SortOrder).ToList()
            };
            planned.Add(new PlannedPage
            {
                Page = kept, Change = PageChange.Kept, SectionsBefore = SectionTypes(row), ChangedOnSite = row.UpdatedAt, BoxNotes = ElsewhereNotes(row, stays: true)
            });

            if (bySlug.ContainsKey(row.Slug))
            {
                problems.Add($"The site already has a page at \"/{row.Slug}\" (\"{row.Title}\"), and the package's home page takes that address. " +
                    "Remove that page below, or give the home page another address in the builder.");
            }
        }

        // 4. Menu order: beside the package's pages, a kept page follows them in the order it had.
        var live = planned.Where(p => p.Change != PageChange.Removed).ToList();
        foreach (var group in live.GroupBy(p => p.Page.ParentPageId ?? 0))
        {
            var fromPackage = group.Where(p => p.Change != PageChange.Kept).ToList();
            if (fromPackage.Count == 0) continue;
            var next = fromPackage.Max(p => p.Page.SortOrder) + 1;
            foreach (var kept in group.Where(p => p.Change == PageChange.Kept).OrderBy(p => p.Page.SortOrder).ThenBy(p => p.Page.Title, StringComparer.OrdinalIgnoreCase))
            {
                kept.Page.SortOrder = next++;
            }
        }

        // 5. No page ends up deeper than the menu goes.
        var after = live.ToDictionary(p => p.Page.Id, p => p.Page);
        foreach (var item in live)
        {
            var depth = 1;
            var parentId = item.Page.ParentPageId;
            var seen = new HashSet<int> { item.Page.Id };
            while (parentId.HasValue && after.TryGetValue(parentId.Value, out var above) && seen.Add(above.Id))
            {
                depth++;
                parentId = above.ParentPageId;
            }
            if (depth > BuilderContract.MaxDepth)
            {
                problems.Add($"\"{item.Page.Title}\" would end up {depth} levels deep in the menu, and a menu goes {BuilderContract.MaxDepth}. " +
                    (item.Change == PageChange.Kept ? "Remove that page below, or move it in the page editor first." : "Move it in the builder."));
            }
        }

        // 6. The logo. A package built from scratch that has none says nothing about the site's logo;
        //    a site that came back from a hand-over without one had it removed in the builder.
        var logoAfter = template.LogoRef.Length > 0 ? Image(template.LogoRef)
            : template.HandoverId.Length > 0 ? string.Empty
            : site.LogoUrl ?? string.Empty;
        var logoBefore = site.LogoUrl ?? string.Empty;
        var logo = logoAfter == logoBefore ? LogoChange.Unchanged
            : logoAfter.Length == 0 ? LogoChange.Removed
            : logoBefore.Length == 0 ? LogoChange.Set
            : LogoChange.Replaced;

        var ordered = planned
            .OrderBy(p => p.Change == PageChange.Removed ? 1 : 0)
            .ThenBy(p => p.Page.SortOrder)
            .ThenBy(p => p.Page.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new SiteImportPlan
        {
            Pages = ordered, Problems = problems, LogoUrlAfter = logoAfter, Logo = logo,
            LogoChangedSinceHandover = logoChangedSinceHandover && logo != LogoChange.Unchanged,
            Fingerprint = FingerprintOf(ordered, problems, logo, logoAfter),
            MoveCandidates = candidates.Select(r => new MoveCandidate(r.Id, r.Slug, r.Title)).ToList()
        };
    }

    // 581: which package page at a new address (pages) is which page of the site (rows), where nothing
    // but the words tell (1a). Each reading pairs a page only when it points one way: one page of the
    // package and one of the site, of those still unpaired. allPages: every page of the package.
    private static Dictionary<BuilderPage, (WebsitePage Row, MoveMatch By)> GuessMoves(
        IReadOnlyList<BuilderPage> pages, IReadOnlyList<BuilderPage> allPages, IReadOnlyList<WebsitePage> rows, IReadOnlyList<ShortenedLabel> labels)
    {
        var found = new Dictionary<BuilderPage, (WebsitePage Row, MoveMatch By)>(ReferenceEqualityComparer.Instance);
        var used = new HashSet<int>();
        foreach (var page in pages)
        {
            // The shortened label, word for word: one record's alone, and no other page of the package
            // carries it (as 1c reads it).
            var records = labels.Where(l => string.Equals(l.SentLabel, page.NavigationLabel, StringComparison.Ordinal)).ToList();
            if (records.Count != 1 || allPages.Count(p => string.Equals(p.NavigationLabel, page.NavigationLabel, StringComparison.Ordinal)) != 1) continue;
            var row = rows.FirstOrDefault(r => r.Id == records[0].RowId);
            if (row == null || !used.Add(row.Id)) continue;
            found[page] = (row, MoveMatch.ShortenedLabel);
        }
        void BySame(Func<BuilderPage, string?> ofPage, Func<WebsitePage, string?> ofRow, MoveMatch by)
        {
            var open = pages.Where(p => !found.ContainsKey(p)).Select(p => (Page: p, Words: Words(ofPage(p)))).ToList();
            var free = rows.Where(r => !used.Contains(r.Id)).Select(r => (Row: r, Words: Words(ofRow(r)))).ToList();
            foreach (var (page, words) in open)
            {
                if (words.Length == 0 || open.Count(p => p.Words == words) != 1) continue;
                var same = free.Where(r => r.Words == words).ToList();
                if (same.Count != 1 || !used.Add(same[0].Row.Id)) continue;
                found[page] = (same[0].Row, by);
            }
        }
        BySame(p => p.Title, r => r.Title, MoveMatch.SameTitle);
        BySame(p => p.NavigationLabel, r => r.NavigationLabel, MoveMatch.SameMenuLabel);
        return found;
    }

    // Words as a reader takes them: the spaces between them and their capitals do not count.
    private static string Words(string? text) =>
        string.Join(" ", (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    // The old addresses a page lists (553), one per line, as the public site reads them: no slash at
    // either end, lower case, each once. (The page editor stores them already in that form, through
    // IPRO.Web's OldAddresses.Parse, which this project cannot call; this reads what is stored.)
    private static IEnumerable<string> Addresses(string? field) =>
        (field ?? string.Empty).Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => a.Trim('/').ToLowerInvariant()).Where(a => a.Length > 0).Distinct();

    private static string LanguageName(string language) => language == WebsiteLanguages.French ? "French" : "English";

    // 581: what a removed page has that its list of sections does not show, and that goes with it. A
    // page the designer only gave a new address, when the plan cannot tell it is this one, comes back as
    // a new page without any of it; the review names it before anything is written. hiddenMatters: it
    // was removed in the builder and the package has new pages, one of which may be it. linksTo: where
    // links to its address go afterwards (null for a page with none of its own, the home page).
    private static IReadOnlyList<string> SettingsOf(WebsitePage row, bool hiddenMatters, string? linksTo)
    {
        var lines = new List<string>();
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.MetaTitle)) parts.Add($"its search title \"{row.MetaTitle.Trim()}\"");
        if (!string.IsNullOrWhiteSpace(row.MetaDescription)) parts.Add("its search description");
        var language = WebsiteLanguages.NormalizePage(row.Language);
        if (language.Length > 0) parts.Add($"its own language ({LanguageName(language)})");
        var old = Addresses(row.OldAddresses).Take(OldAddressLimit).ToList();
        if (old.Count > 0) parts.Add($"the old address{(old.Count == 1 ? string.Empty : "es")} it answers to ({string.Join(", ", old.Select(a => "/" + a))})");
        if (parts.Count > 0)
        {
            lines.Add("Also goes with it: " + (parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1]) + ".");
        }
        if (hiddenMatters && (!row.IsPublished || !row.ShowInNavigation))
        {
            lines.Add("On the site it is " + (!row.IsPublished && !row.ShowInNavigation ? "neither published nor in the menu"
                : !row.IsPublished ? "not published" : "not in the menu") + "; a new page of the package comes back published and in the menu.");
        }
        if (linksTo != null) lines.Add(linksTo);
        return lines;
    }

    // Everything the review screen says about a plan: each page, what happens to it, what it loses and
    // whether it was changed in iPro; the logo; what stands in the way.
    private static string FingerprintOf(IReadOnlyList<PlannedPage> pages, IReadOnlyList<string> problems, LogoChange logo, string logoAfter)
    {
        const char field = (char)0x1F;
        var text = new StringBuilder();
        foreach (var item in pages)
        {
            text.Append(item.Change).Append(field)
                .Append(item.Page.Id > 0 ? item.Page.Id : 0).Append(field)
                .Append(item.Page.Slug).Append(field)
                .Append(item.Page.Title).Append(field)
                // 577: and its menu label, which a return can take from the site as it is at that moment.
                .Append(item.Page.NavigationLabel).Append(field)
                .Append(item.LabelSent).Append(item.LabelKept).Append(field)
                .Append(item.Page.Blocks?.Count ?? 0).Append(field)
                .Append(string.Join(",", item.SectionsBefore)).Append(field)
                .Append(string.Join(",", item.SectionsLost)).Append(field)
                .Append(string.Join(",", item.SectionsGoing.Select(g => g.BlockId + (g.Kept ? "+" : "-") + (g.RemovedInBuilder ? "r" : "n") + g.Says))).Append(field)
                .Append(string.Join("|", item.BoxNotes)).Append(field)
                // 576: and what a page of the package will SAY. A locked box brings the section as the
                // site has it at that moment, so what is written can change after the review was read.
                .Append(item.Change is PageChange.Added or PageChange.Replaced
                    ? string.Join(",", (item.Page.Blocks ?? new List<WebsiteContentBlock>()).OrderBy(b => b.SortOrder).Select(SiteFingerprint.OfSection)) : string.Empty).Append(field)
                .Append(item.RemovedInBuilder).Append(item.ChangedSinceHandover).Append(field)
                // 581: which page a page at a new address was, and what a removed page takes with it.
                .Append(item.MovedFrom).Append(item.MovedBy).Append(item.OldAddressKept).Append(field)
                .Append(item.OldAddressNote).Append(field).Append(string.Join("|", item.MoveNotes)).Append(field)
                .Append(string.Join("|", item.SettingsLost)).Append((char)0x1E);
        }
        text.Append(logo).Append(field).Append(logoAfter).Append(field).Append(string.Join(((char)0x1E).ToString(), problems));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant()[..32];
    }

    private static IReadOnlyList<string> SectionTypes(WebsitePage page) =>
        page.Blocks.OrderBy(b => b.SortOrder).Select(b => b.BlockType).ToList();

    // A section as the review names it: its kind as the page editor calls it, and its own heading.
    public static string Named(string blockType, string? heading) =>
        WebsiteBlockTypes.DisplayName(blockType) + (string.IsNullOrWhiteSpace(heading) ? string.Empty : $" \"{heading.Trim()}\"");

    // A page that keeps its row but changes its address (the home page, matched as the home page and
    // not by address; and, 581, a page the designer gave a new address, matched in 1a) remembers the
    // address it had, so links and search results that point at the old one still arrive (553:
    // OldAddresses, one per line).
    private static string OldAddressesAfter(WebsitePage? row, string newSlug)
    {
        var current = row?.OldAddresses ?? string.Empty;
        if (row == null || string.Equals(row.Slug, newSlug, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(row.Slug)) return current;
        var lines = current.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (lines.Contains(row.Slug, StringComparer.OrdinalIgnoreCase)) return current;
        // 581: a page that already lists as many as the public site reads gets no line it would never
        // read (the review says so, and what to do).
        if (Addresses(current).Count() >= OldAddressLimit) return current;
        lines.Add(row.Slug);
        var joined = string.Join("\n", lines);
        return joined.Length <= 2000 ? joined : current;   // the column's size; an address too many is not worth a failed import
    }
}
