using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.Business.SiteImport;
using IPRO.Admin.Infrastructure;
using IPRO.Admin.Models;
using IPRO.Billing;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Admin.Controllers;

[Authorize(Policy = "AdminAccess")]
public class AgentsController : Controller
{
    private readonly IAgentService _agents;
    private readonly IWebsiteService _websites;
    private readonly IUnitOfWork _uow;
    private readonly IBillingService _billing;
    private readonly IPasswordHasher<AgentUser> _hasher;
    private readonly ILogger<AgentsController> _logger;
    private readonly IAdminAuditLogService _auditLog;
    private readonly IPRO.DataAccess.IPRODbContext _db;
    // Resolved lazily, never in the constructor: AzureBlobStorageService is a singleton whose
    // constructor reads Azure:StorageConnectionString, which the admin app does not configure. Taking
    // it as a constructor dependency made EVERY action on this controller throw
    // ArgumentNullException('connectionString') -- it took down the whole agent list. Same reason
    // ECardDesignsController resolves it through the service provider inside the action.
    private readonly IServiceProvider _services;

    public AgentsController(IAgentService agents, IWebsiteService websites,
        IUnitOfWork uow, IBillingService billing, IPasswordHasher<AgentUser> hasher, ILogger<AgentsController> logger, IAdminAuditLogService auditLog, IPRO.DataAccess.IPRODbContext db, IServiceProvider services)
    { _agents = agents; _websites = websites; _uow = uow; _billing = billing; _hasher = hasher; _logger = logger; _auditLog = auditLog; _db = db; _services = services; }

    private int CurrentAdminId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    private string CurrentAdminUsername => User.Identity?.Name ?? "unknown";

    // Matches the "SuperAdmin" policy in Program.cs exactly (RequireClaim("Role", "SuperAdmin")).
    // Used where an action is available to all admins but individual FIELDS are not -- see Edit.
    private bool IsSuperAdmin => User.HasClaim("Role", "SuperAdmin");

    public async Task<IActionResult> Index(string? search, string? status, int page = 1)
    {
        var all = await _agents.GetAllAsync();
        if (!string.IsNullOrWhiteSpace(search))
        {
            all = int.TryParse(search, out var searchId)
                ? all.Where(a => a.Id == searchId)
                : all.Where(a => a.UserName.Contains(search, StringComparison.OrdinalIgnoreCase)
                              || a.Email.Contains(search, StringComparison.OrdinalIgnoreCase)
                              || a.FirstName.Contains(search, StringComparison.OrdinalIgnoreCase)
                              || a.LastName.Contains(search, StringComparison.OrdinalIgnoreCase));
        }
        if (status == "active")   all = all.Where(a => a.IsActive);
        if (status == "inactive") all = all.Where(a => !a.IsActive);

        ViewBag.PackageNames = (await _uow.BillingRules.GetAllAsync()).ToDictionary(p => p.Id, p => p.PackageName);   // 518: the name, not "Package 3"
        ViewBag.Search     = search;
        ViewBag.Status     = status;
        ViewBag.TotalCount = all.Count();
        return View(PaginationHelper.Paginate(all.OrderByDescending(a => a.CreatedAt), page, 20));
    }

    public async Task<IActionResult> Details(int id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();
        // 518: times on this page in the platform's zone, as the support screens since 502.
        ViewBag.Zone = AdminClock.Zone(HttpContext.RequestServices.GetRequiredService<IConfiguration>());

        var warnings = new List<string>();
        var website = await LoadDetailsPanelAsync(
            () => _websites.GetByAgentIdAsync(id),
            "Website details",
            warnings);
        ViewBag.Website = website;
        // 584: a start-again that "Put the previous pages back" undoes, while it is the site's newest
        // change of pages, and how many pages that brings back.
        var startedAgain = website == null
            ? null
            : await LoadDetailsPanelAsync(() => SiteStartAgainService.PutBackableAsync(_db, website.Id), "Start again", warnings);
        ViewBag.StartedAgain = startedAgain;
        ViewBag.StartedAgainPages = startedAgain == null
            ? 0
            : await LoadDetailsPanelAsync(() => SiteStartAgainService.PagesKeptAsync(_db, startedAgain.Id), "Start again", warnings);
        var activeSubscription = await LoadDetailsPanelAsync(
            () => _uow.Billings.FirstOrDefaultAsync(b => b.AgentUserId == id && b.Status == BillingStatus.Active),
            "Subscription details",
            warnings);
        ViewBag.Subscription = activeSubscription;
        // 503: the same next charge the agent's own Billing page shows (see NextCharge), so the
        // owner answering "what will I be billed?" reads the number the customer is reading.
        ViewBag.NextCharge = activeSubscription == null
            ? null
            : await LoadDetailsPanelAsync(
                async () => (NextCharge?)await NextCharge.ForAsync(_db, activeSubscription, DateTime.UtcNow),
                "Next charge",
                warnings);
        ViewBag.Billings = await LoadDetailsPanelAsync(
            async () => (await _uow.Billings.FindAsync(b => b.AgentUserId == id)).OrderByDescending(b => b.CreatedAt).ToList(),
            "Billing history",
            warnings) ?? new List<IPRO.Entities.Billing>();
        ViewBag.SubscriptionChanges = await LoadDetailsPanelAsync(
            async () => (await _uow.SubscriptionChanges.FindAsync(c => c.AgentUserId == id)).OrderByDescending(c => c.CreatedAt).ToList(),
            "Subscription changes",
            warnings) ?? new List<SubscriptionChange>();
        ViewBag.Invoices = await LoadDetailsPanelAsync(
            async () => (await _uow.Invoices.FindAsync(i => i.AgentUserId == id)).OrderByDescending(i => i.IssuedAt).Take(10),
            "Invoices",
            warnings) ?? Enumerable.Empty<Invoice>();
        ViewBag.OpenInvoices = await LoadDetailsPanelAsync(
            async () => (await _uow.Invoices.FindAsync(i => i.AgentUserId == id && !i.IsPaid)).OrderByDescending(i => i.IssuedAt).ToList(),
            "Open invoices",
            warnings) ?? new List<Invoice>();
        ViewBag.FailedPaymentInvoices = await LoadDetailsPanelAsync(
            async () => (await _uow.Invoices.FindAsync(i =>
                    i.AgentUserId == id &&
                    !i.IsPaid &&
                    i.PayPalTransactionId.StartsWith("PAYPAL_FAILED:")))
                .OrderByDescending(i => i.IssuedAt)
                .ToList(),
            "Failed payment invoices",
            warnings) ?? new List<Invoice>();
        ViewBag.PackageLookup = (await LoadDetailsPanelAsync(
            async () => (await _uow.BillingRules.GetAllAsync()).ToDictionary(p => p.Id),
            "Packages",
            warnings)) ?? new Dictionary<int, BillingRule>();
        ViewBag.ClientCount = await LoadDetailsPanelAsync(
            () => _uow.Clients.CountAsync(c => c.AgentUserId == id),
            "Client count",
            warnings);
        ViewBag.Logs = await LoadDetailsPanelAsync(
            async () => (await _uow.OperateLogs.FindAsync(l => l.AgentUserId == id)).OrderByDescending(l => l.CreatedAt).Take(20),
            "Activity log",
            warnings) ?? Enumerable.Empty<OperateLog>();
        ViewBag.BillingLogs = await LoadDetailsPanelAsync(
            async () => (await _uow.OperateLogs.FindAsync(l =>
                    l.AgentUserId == id &&
                    (l.Module == "Billing" || l.Action.Contains("Invoice") || l.Action.Contains("Billing"))))
                .OrderByDescending(l => l.CreatedAt)
                .Take(25)
                .ToList(),
            "Billing activity log",
            warnings) ?? new List<OperateLog>();
        // 583: sign-ins locked right now under this agent -- their team members' and their clients' (the
        // agent's own lock is on the agent row the page already has).
        var lockNow = DateTime.UtcNow;
        ViewBag.LockedTeamMembers = await LoadDetailsPanelAsync(
            async () => await _db.TeamMembers.AsNoTracking()
                .Where(t => t.AgentUserId == id && t.SignInLockedUntil != null && t.SignInLockedUntil > lockNow)
                .OrderBy(t => t.FullName).ToListAsync(),
            "Locked team member sign-ins",
            warnings) ?? new List<TeamMember>();
        ViewBag.LockedClients = await LoadDetailsPanelAsync(
            async () => await _db.Clients.AsNoTracking()
                .Where(c => c.AgentUserId == id && c.SignInLockedUntil != null && c.SignInLockedUntil > lockNow)
                .OrderBy(c => c.LastName).ThenBy(c => c.FirstName).ToListAsync(),
            "Locked client portal sign-ins",
            warnings) ?? new List<Client>();
        // 593: two-step sign-in -- the agent's own, whether it is required, and their team members' that are on.
        ViewBag.TwoStep = await LoadDetailsPanelAsync(() => TwoStepStore.FindAsync(_db, TwoStepAccount.Adviser(id)), "Two-step sign-in", warnings);
        ViewBag.TwoStepRequired = await LoadDetailsPanelAsync(() => TwoStepStore.IsRequiredAsync(_db, id), "Two-step sign-in", warnings);
        ViewBag.TwoStepTeam = await LoadDetailsPanelAsync(
            async () => await (from t in _db.TwoStepSignIns.AsNoTracking()
                               join m in _db.TeamMembers.AsNoTracking() on t.TeamMemberId equals m.Id
                               where t.AgentUserId == id && t.TeamMemberId != 0 && m.AgentUserId == id
                               orderby m.FullName
                               select new TwoStepTeamRow(m.Id, m.FullName, m.Email, t.Method, t.CodeLockedUntil, t.WrongCodes)).ToListAsync(),
            "Two-step sign-in", warnings) ?? new List<TwoStepTeamRow>();
        ViewBag.DetailsWarnings = warnings;
        return View(agent);
    }

    // 583: 5 wrong passwords in a row lock a sign-in for 15 minutes. SuperAdmin clears the agent's own,
    // or one of their team members' or clients' (the account must be this agent's). Recorded in the
    // audit log, and at Warning in the container log as the lock was.
    [Authorize(Policy = "SuperAdmin")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UnlockSignIn(int id, SignInAccountKind kind = SignInAccountKind.Adviser, int accountId = 0)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();

        string who;
        SignInAccount account;
        switch (kind)
        {
            case SignInAccountKind.Adviser:
                account = new SignInAccount(SignInAccountKind.Adviser, agent.Id);
                who = $"agent '{agent.UserName}'";
                break;
            case SignInAccountKind.TeamMember:
                var member = await _db.TeamMembers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == accountId && t.AgentUserId == id);
                if (member == null) return NotFound();
                account = new SignInAccount(SignInAccountKind.TeamMember, member.Id);
                who = $"team member {member.Email} of agent '{agent.UserName}'";
                break;
            case SignInAccountKind.Client:
                var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == accountId && c.AgentUserId == id);
                if (client == null) return NotFound();
                account = new SignInAccount(SignInAccountKind.Client, client.Id);
                who = $"client {client.Email} (client portal) of agent '{agent.UserName}'";
                break;
            default:
                return NotFound();
        }

        var wasLocked = await SignInLockout.ClearAsync(_db, account, DateTime.UtcNow);
        // 593: and a lock after 5 wrong two-step codes (its own lock: a new password does not end it).
        if (account.Kind != SignInAccountKind.Client
            && await TwoStepStore.ClearCodeLockAsync(_db, account.Kind == SignInAccountKind.Adviser ? TwoStepAccount.Adviser(agent.Id) : TwoStepAccount.Member(agent.Id, account.Id), DateTime.UtcNow))
            wasLocked = true;
        if (wasLocked) SignInLockout.LogCleared(_logger, account, $"unlocked by {CurrentAdminUsername}");
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "SignInUnlocked", wasLocked
            ? $"Unlocked sign-in for {who}."
            : $"Cleared the wrong-password count for {who} (sign-in was not locked).");
        TempData["Success"] = wasLocked
            ? "Sign-in unlocked. They can sign in again now."
            : "That sign-in was not locked; the count of wrong passwords is back to 0.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // 593: two-step sign-in is optional, and SuperAdmin can require it for one adviser's account: the
    // adviser and each of their team members then set it up before using the portal (the portal sends
    // them to its set-up page). Recorded in the audit log.
    [Authorize(Policy = "SuperAdmin")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RequireTwoStep(int id, bool required)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();
        var changed = await TwoStepStore.SetRequiredAsync(_db, id, required, CurrentAdminUsername, DateTime.UtcNow);
        if (changed)
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, required ? "TwoStepRequired" : "TwoStepNotRequired",
                required ? $"Required two-step sign-in for agent '{agent.UserName}' and their team members."
                         : $"Two-step sign-in is optional again for agent '{agent.UserName}' and their team members.");
        TempData["Success"] = required
            ? "Two-step sign-in is required for this account: the adviser and each team member set it up the next time they use the portal."
            : "Two-step sign-in is optional again for this account. Anyone who has it on keeps it.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // 593: someone lost their phone and their recovery codes. Their two-step sign-in is turned off (every
    // session of theirs ends at the next page), the reset is in the audit log, and they are emailed. The
    // account must be this agent's, as for UnlockSignIn.
    [Authorize(Policy = "SuperAdmin")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetTwoStep(int id, SignInAccountKind kind = SignInAccountKind.Adviser, int accountId = 0)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();

        TwoStepAccount account;
        TwoStepLetters.Person person;
        TeamMember? member = null;
        string who;
        switch (kind)
        {
            case SignInAccountKind.Adviser:
                account = TwoStepAccount.Adviser(agent.Id);
                person = TwoStepLetters.ForAdviser(agent);
                who = $"agent '{agent.UserName}'";
                break;
            case SignInAccountKind.TeamMember:
                member = await _db.TeamMembers.FirstOrDefaultAsync(t => t.Id == accountId && t.AgentUserId == id);
                if (member == null) return NotFound();
                account = TwoStepAccount.Member(agent.Id, member.Id);
                person = TwoStepLetters.ForTeamMember(member, agent);
                who = $"team member {member.Email} of agent '{agent.UserName}'";
                break;
            default:
                return NotFound();
        }

        var row = await TwoStepStore.FindAsync(_db, account);
        if (row == null)
        {
            TempData["Success"] = "Two-step sign-in was not on for that sign-in; nothing to reset.";
            return RedirectToAction(nameof(Details), new { id });
        }
        // Held after 20 wrong codes: whoever was guessing has the password. The reset gives a temporary
        // password in the same action (as My Team does), and FIRST: a reset that left the password would
        // hand the login to the guesser between two clicks (the second re-check of 593).
        string? temporaryPassword = null;
        if (TwoStepStore.IsCodeHeld(row))
        {
            temporaryPassword = BuildTemporaryPassword();
            if (member != null)
            {
                member.PasswordHash = new PasswordHasher<TeamMember>().HashPassword(member, temporaryPassword);
                member.MustChangePassword = true;
                await _db.SaveChangesAsync();   // tracked: these columns only
            }
            else
            {
                agent.PasswordHash = _hasher.HashPassword(agent, temporaryPassword);
                agent.MustChangePassword = true;
                agent.PasswordChangedAt = null;
                await _agents.UpdateAsync(agent);
            }
            await SignInLockout.ClearAsync(_db, account.SignIn, DateTime.UtcNow);
        }
        await TwoStepStore.DisableAsync(_db, account);
        _logger.LogWarning("Two-step sign-in reset by {Admin}: {Kind} {Id}", CurrentAdminUsername, kind, kind == SignInAccountKind.Adviser ? agent.Id : accountId);
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "TwoStepReset", temporaryPassword == null
            ? $"Reset two-step sign-in for {who}."
            : $"Reset two-step sign-in for {who}; it was locked after 20 wrong codes, so a temporary password was issued too.");

        var sent = false;
        if (!string.IsNullOrWhiteSpace(person.Email) && _services.GetService<IPRO.Email.IEmailService>() is { } email)
        {
            var letter = TwoStepLetters.Reset(person, byOwner: false);
            try
            {
                sent = (await email.SendDetailedAsync(letter.To, letter.ToName, letter.Subject, letter.Html)).Success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The two-step reset email for {Kind} {Id} failed", kind, accountId);
            }
        }
        TempData["Success"] = (sent
            ? "Two-step sign-in reset. Their password alone signs them in until they set it up again; they have been emailed."
            : "Two-step sign-in reset. Their password alone signs them in until they set it up again. The email to them could not be sent: tell them yourself.")
            + (temporaryPassword == null ? "" :
                $" It was locked after 20 wrong codes, so someone has their password: their temporary password is now {temporaryPassword} (give it to them securely; they choose a new one when they sign in).");
        return RedirectToAction(nameof(Details), new { id });
    }

    // Resources provisioning only runs when the agent has no page with the "resources" slug, so an
    // agent provisioned before three-tier navigation keeps the old flat shape forever. Deleting the
    // subtree lets WebsiteStarterResourcesHelper rebuild it the next time they open Website Pages;
    // their Article rows are reused by title rather than duplicated, so nothing they wrote is lost.
    // ADMIN-10 / A5-M-REBUILDRES (2026-08-20): this deletes the agent's Resources pages INCLUDING
    // any content blocks they customised, so it is SuperAdmin-only -- a support-role admin could
    // previously destroy agent content with a button whose confirm text did not say anything would
    // be lost. The button stays VISIBLE but disabled for support admins (the owner's standing rule:
    // disable, don't hide -- people should see what the role gates).
    [Authorize(Policy = "SuperAdmin")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RebuildResources(int id)
    {
        var website = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(_db.AgentWebsites, w => w.AgentUserId == id);
        if (website == null)
        {
            TempData["Error"] = "This agent has no website yet, so there is nothing to rebuild.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var pages = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(_db.WebsitePages.Where(p => p.AgentWebsiteId == website.Id));
        var resources = pages.FirstOrDefault(p => p.Slug == "resources");
        if (resources == null)
        {
            // Not a failure: there is simply nothing to delete. Resources is built by the agent portal
            // (WebsitePagesController calls EnsureResourcesAsync on both Index and Navigation), so this
            // agent will get the current three-level structure automatically. Reported as a neutral
            // message rather than a red error, which read as "the button is broken".
            TempData["Warning"] = "This agent has no Resources section yet, so there was nothing to rebuild. " +
                                  "They will get the current three-level structure automatically the next time they " +
                                  "open Website Pages or Menu & Header Settings in their portal.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var byParent = pages.ToLookup(p => p.ParentPageId);
        var doomed = new List<WebsitePage> { resources };
        var pending = new Queue<int>();
        pending.Enqueue(resources.Id);
        while (pending.Count > 0)
        {
            foreach (var child in byParent[pending.Dequeue()])
            {
                doomed.Add(child);
                pending.Enqueue(child.Id);
            }
        }

        var doomedIds = doomed.Select(p => p.Id).ToList();
        var blocks = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(_db.WebsiteContentBlocks.Where(b => doomedIds.Contains(b.WebsitePageId)));
        _db.WebsiteContentBlocks.RemoveRange(blocks);
        _db.WebsitePages.RemoveRange(doomed);
        await _db.SaveChangesAsync();

        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentRebuildResources",
            $"Removed {doomed.Count} Resources pages for agent #{id} so they rebuild at three levels");
        TempData["Success"] = $"Removed {doomed.Count} Resources page{(doomed.Count == 1 ? "" : "s")}. They rebuild with the three-level structure the next time this agent opens Website Pages.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // Unlike Resources, Request Meeting has no lazy rebuild path to lean on -- starter-page
    // provisioning only runs for agents with ZERO pages -- so this does the work directly, the same
    // way signup provisioning does since 2026-08-15: copy the vertical's "Request a Meeting" starter
    // form into a real form the agent owns, then point the page's block at it. If the agent already
    // owns a form with that title (a prior rebuild, or self-adopted from the template gallery), it is
    // REUSED so their edits survive; only the page's blocks are replaced.
    // M2 (launch runway Phase 2, 2026-08-27): same RemoveRange destruction as RebuildResources
    // sixty lines up -- every block the agent customised on that page is deleted -- so it takes
    // the same SuperAdmin gate. A support-role admin could destroy an agent's page content with a
    // confirm that (until the same fix) claimed their edits survived.
    [Authorize(Policy = "SuperAdmin")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RebuildRequestMeeting(int id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();
        var website = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(_db.AgentWebsites, w => w.AgentUserId == id);
        if (website == null)
        {
            TempData["Error"] = "This agent has no website yet, so there is nothing to rebuild.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var meetingTitle = IPRO.DataAccess.WebsiteStarterFormSeeder.MeetingFormTitle;
        var template = (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .ToListAsync(_db.WebsiteStarterForms.Where(f => f.IsActive && f.Title == meetingTitle &&
                    (f.BusinessType == agent.BusinessType || f.BusinessType == "All"))))
            .OrderByDescending(f => f.BusinessType == agent.BusinessType)
            .FirstOrDefault();
        if (template == null)
        {
            TempData["Error"] = $"No active \"{meetingTitle}\" starter form exists for this agent's business type (or \"All\") -- check Starter Forms.";
            return RedirectToAction(nameof(Details), new { id });
        }

        // 584: the form the page's Form section already shows comes first. After "Start the website
        // again" an adviser can own two forms of that title (the old business type's and the new one's);
        // the oldest would be the old business type's.
        var currentPage = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(_db.WebsitePages, p => p.AgentWebsiteId == website.Id && p.Slug == "request-meeting");
        var shownFormIds = currentPage == null
            ? new List<int>()
            : (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                    .ToListAsync(_db.WebsiteContentBlocks.Where(b => b.WebsitePageId == currentPage.Id && b.BlockType == WebsiteBlockTypes.Form).Select(b => b.SettingsJson)))
                .Select(json => WebsiteFormSettings.FromJson(json).WebsiteFormId)
                .ToList();
        var form = (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .ToListAsync(_db.WebsiteForms.Where(f => f.AgentUserId == id && f.IsActive && f.Title == meetingTitle)))
            .OrderByDescending(f => shownFormIds.Contains(f.Id))
            .ThenBy(f => f.Id)
            .FirstOrDefault();
        var reusedExistingForm = form != null;
        form ??= await IPRO.DataAccess.WebsiteFormTemplateCopier.CopyToAgentAsync(_db, template, id);

        // The starter page definition supplies the page/block wording so a rebuilt page matches what
        // a fresh signup gets; fall back to plain defaults if that seed row is ever deactivated.
        var starterPage = (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .ToListAsync(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                    .Include(_db.WebsiteStarterPages.Where(p => p.IsActive && p.Slug == "request-meeting" &&
                        (p.BusinessType == agent.BusinessType || p.BusinessType == "All")), p => p.Blocks)))
            .OrderByDescending(p => p.BusinessType == agent.BusinessType)
            .FirstOrDefault();
        var starterBlock = starterPage?.Blocks.OrderBy(b => b.SortOrder).FirstOrDefault();

        var page = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(_db.WebsitePages, p => p.AgentWebsiteId == website.Id && p.Slug == "request-meeting");
        var createdPage = page == null;
        if (page == null)
        {
            page = new WebsitePage
            {
                AgentWebsiteId = website.Id,
                Title = starterPage?.Title ?? "Request Meeting",
                Slug = "request-meeting",
                NavigationLabel = starterPage?.NavigationLabel ?? "Request Meeting",
                MetaTitle = starterPage?.MetaTitle ?? "Request Meeting",
                MetaDescription = starterPage?.MetaDescription ?? "Request a meeting - professional service and support.",
                IsHomePage = false,
                ShowInNavigation = true,
                IsPublished = true,
                SortOrder = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                    .CountAsync(_db.WebsitePages.Where(p => p.AgentWebsiteId == website.Id))
            };
            _db.WebsitePages.Add(page);
            await _db.SaveChangesAsync();
        }
        else
        {
            var oldBlocks = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .ToListAsync(_db.WebsiteContentBlocks.Where(b => b.WebsitePageId == page.Id));
            _db.WebsiteContentBlocks.RemoveRange(oldBlocks);
        }

        _db.WebsiteContentBlocks.Add(new WebsiteContentBlock
        {
            WebsitePageId = page.Id,
            BlockType = WebsiteBlockTypes.Form,
            Heading = starterBlock?.Heading ?? "Request a meeting",
            Subheading = starterBlock?.Subheading ?? string.Empty,
            Body = starterBlock?.Body ?? string.Empty,
            SettingsJson = new WebsiteFormSettings { WebsiteFormId = form.Id }.ToJson(),
            SortOrder = 0,
            IsVisible = true
        });
        await _db.SaveChangesAsync();

        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentRebuildRequestMeeting",
            $"Agent #{id}: request-meeting page {(createdPage ? "created" : "blocks replaced")}, wired to form #{form.Id} ({(reusedExistingForm ? "existing form reused" : $"copied from starter template #{template.Id}")})");
        TempData["Success"] = $"Request Meeting page now carries the \"{meetingTitle}\" form " +
            $"({(reusedExistingForm ? "reused the form this agent already had, edits intact" : $"created their own copy of the {template.BusinessType} template")}). Live immediately.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // 584 (2026-10-16): start the adviser's website again from the starter pack of their CURRENT
    // business type (SiteStartAgainService). Changing the business type under Edit changes no page --
    // starter pages are made only for a site with none -- so a demo account built from one pack could
    // not show another. Every page goes and the new pack's pages are written in the same transaction,
    // so the public site is never empty; the site as it was is kept, and "Put the previous pages back"
    // on Details undoes it. SuperAdmin only like the two Rebuild buttons (a support admin sees the
    // button disabled); the screen says what goes and what comes, and the username must be typed.
    [Authorize(Policy = "SuperAdmin")]
    public async Task<IActionResult> StartAgain(int id)
    {
        if (!IsSuperAdmin) return Forbid();
        var plan = await SiteStartAgainService.PlanAsync(_db, id);
        if (plan == null) return await NoWebsiteToStartAgainAsync(id);
        return View(new AgentStartAgainViewModel { Plan = plan, Zone = AdminZone() });
    }

    // plan: what the screen showed, posted back with the button; the site is written only when it is
    // still so. unpublishOtherPackArticles: the tick box (an unticked box posts nothing).
    [Authorize(Policy = "SuperAdmin")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> StartAgain(int id, string? confirmUserName, bool unpublishOtherPackArticles = false, string? plan = null)
    {
        if (!IsSuperAdmin) return Forbid();
        SiteStartAgainService.Result result;
        try
        {
            result = await SiteStartAgainService.ApplyAsync(_db, id, confirmUserName, unpublishOtherPackArticles, plan, CurrentAdminUsername);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
        {
            // The database refused a row (the site changed at the same moment). The transaction went
            // with it: nothing was written.
            _logger.LogWarning(ex, "Agent {AgentId}'s website was not started again", id);
            _db.ChangeTracker.Clear();
            result = SiteStartAgainService.Result.Failed("The website was being changed at the same moment, so nothing was changed. Read the screen again, then confirm.");
        }

        if (result.AlreadyDone)
        {
            TempData["Success"] = result.Problems.FirstOrDefault();
            return RedirectToAction(nameof(Details), new { id });
        }
        if (!result.Ok)
        {
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentWebsiteNotStartedAgain",
                Short($"Agent #{id}'s website was NOT started again: {result.Problems.FirstOrDefault()}"));
            var again = await SiteStartAgainService.PlanAsync(_db, id);
            if (again == null) return await NoWebsiteToStartAgainAsync(id);
            return View(new AgentStartAgainViewModel
            {
                Plan = again,
                Problems = result.Problems,
                TypedUserName = (confirmUserName ?? string.Empty).Trim(),
                UnpublishOtherPackArticles = unpublishOtherPackArticles,
                Zone = AdminZone()
            });
        }

        _logger.LogWarning(
            "Admin {Admin} started agent {AgentId}'s website again from the {BusinessType} starter pack: {PagesRemoved} pages ({SectionsRemoved} sections) removed, " +
            "{PagesAdded} pages ({SectionsAdded} sections) written, {ArticlesUnpublished} articles unpublished, {ArticlesPublished} published, {ArticlesAdded} articles and {FormsAdded} forms added; the previous pages are kept as import {ImportId}",
            CurrentAdminUsername, id, result.BusinessType, result.PagesRemoved, result.SectionsRemoved,
            result.PagesAdded, result.SectionsAdded, result.ArticlesUnpublished, result.ArticlesPublished, result.ArticlesAdded, result.FormsAdded, result.ImportId);
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentWebsiteStartedAgain",
            Short($"Started agent #{id}'s website again from the {result.BusinessType} starter pack: removed {result.PagesRemoved} page(s) with {result.SectionsRemoved} section(s), " +
                 $"wrote {result.PagesAdded} page(s) with {result.SectionsAdded} section(s), unpublished {result.ArticlesUnpublished} article(s) of another pack, " +
                 $"published {result.ArticlesPublished} article(s) the new pages show, added {result.ArticlesAdded} article(s) and {result.FormsAdded} form(s). " +
                 (result.ImportId > 0 ? $"The previous pages are kept (import id {result.ImportId}) and can be put back." : "The website had no pages before, so there is nothing to put back.")));
        TempData["Success"] = $"The website now has the {result.BusinessType} starter pages ({result.PagesAdded} pages)" +
            (result.SitePublished ? ", live now. " : ". The website is not published, so visitors see them once it is. ") +
            (result.ImportId > 0
                ? $"{result.PagesRemoved} old page{(result.PagesRemoved == 1 ? " was" : "s were")} removed and kept: \"Put the previous pages back\" below undoes this."
                : "It had no pages before, so there is nothing to put back.") +
            (result.ArticlesUnpublished > 0 ? $" {result.ArticlesUnpublished} article{(result.ArticlesUnpublished == 1 ? " of another pack was" : "s of another pack were")} taken off the website (still in the adviser's Articles)." : string.Empty);
        return RedirectToAction(nameof(Details), new { id });
    }

    // 584: undoes a start-again while it is the site's newest change of pages
    // (BuilderImportService.RestoreAsync: the pages as they were; the logo and design stay as they are).
    [Authorize(Policy = "SuperAdmin")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PutBackStartAgain(int id, int importId)
    {
        if (!IsSuperAdmin) return Forbid();
        var import = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(_db.WebsiteBuilderImports, i => i.Id == importId && i.AgentUserId == id);
        if (import == null || !SiteStartAgainService.IsStartAgain(import)) return NotFound();

        BuilderImportService.ApplyResult result;
        try
        {
            result = await BuilderImportService.RestoreAsync(_db, import);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Agent {AgentId}'s previous pages (import {ImportId}) were not put back", id, importId);
            _db.ChangeTracker.Clear();
            result = BuilderImportService.ApplyResult.Failed("The website was being changed at the same moment, so nothing was put back. Try again.");
        }

        if (!result.Ok)
        {
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentWebsiteStartAgainNotPutBack",
                Short($"Agent #{id}'s previous pages (import id {importId}) were NOT put back: {result.Problems.FirstOrDefault()}"));
            // RestoreAsync speaks of packages; on this screen it is a start-again.
            var problem = result.Problems.FirstOrDefault() ?? string.Empty;
            TempData["Error"] = problem.StartsWith("This package is not on the site", StringComparison.Ordinal)
                ? "The previous pages are already back (the button was pressed twice, or they were put back already). Nothing more was changed."
                : "The previous pages were not put back. " + (problem.StartsWith("A newer package has been brought in since", StringComparison.Ordinal)
                    ? "A Template Builder package was brought in after the website was started again: put that back first, on the Template Builder screen."
                    : string.Join(" ", result.Problems));
            return RedirectToAction(nameof(Details), new { id });
        }

        var published = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .AnyAsync(_db.AgentWebsites, w => w.Id == import.AgentWebsiteId && w.IsPublished);
        _logger.LogWarning("Admin {Admin} put agent {AgentId}'s previous pages back, undoing the start-again kept as import {ImportId}",
            CurrentAdminUsername, id, importId);
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentWebsiteStartAgainPutBack",
            Short($"Put agent #{id}'s website back to the pages it had before it was started again from the {import.TemplateName} (import id {importId}); the starter pages written then were removed."));
        TempData["Success"] = "The website has its previous pages back" + (published ? ", live now." : " (the website is not published, so visitors see them once it is).") +
            " Its logo, design and business type are as they are now.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // The platform's time zone, as the details screen reads it; Eastern when nothing says otherwise.
    private string? AdminZone()
    {
        var configuration = _services.GetService(typeof(IConfiguration)) as IConfiguration;
        return configuration == null ? null : AdminClock.Zone(configuration);
    }

    private async Task<IActionResult> NoWebsiteToStartAgainAsync(int id)
    {
        if (await _agents.GetByIdAsync(id) == null) return NotFound();
        TempData["Error"] = "This agent has no website yet, so there is nothing to start again. A website is made when they sign up or publish.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // The audit row's length, as BuilderImportsController keeps it.
    private static string Short(string details) => details.Length <= 480 ? details : details[..480] + "...";

    public async Task<IActionResult> Edit(int id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();
        await LoadActivePackagesAsync();
        return View(ToEditModel(agent));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, AgentEditViewModel model)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();

        NormalizeAgent(model);

        // Support-role admins may edit an agent's profile, but not the fields that ARE the account.
        //
        // This action is only gated by AdminAccess, which is RequireAuthenticatedUser() -- every
        // signed-in admin. ApplyEditModel writes Email and UserName, so a Support admin could change
        // an agent's email to one they control and then use the public password-reset flow to take
        // the account over. That routes straight around the SuperAdmin gate on ResetPassword (M-2)
        // and makes it decorative. PackageId is here too because it grants paid entitlements,
        // IsActive/MustChangePassword because they control whether the agent can get in at all, and
        // DomainName because public host resolution reads it (review H-6) -- a colliding value
        // makes another agent's website ambiguous.
        //
        // Restored BEFORE validation, deliberately (review H-5): the protected inputs are disabled
        // in the view and disabled controls never post, so the model arrives with them empty --
        // validating first meant "Email is required" made the form unsaveable for every
        // non-SuperAdmin. An empty value means "not posted", not an attempted change, so only
        // non-empty differences are reported as blocked.
        var blockedFields = new List<string>();
        if (!IsSuperAdmin)
        {
            if (!string.IsNullOrWhiteSpace(model.Email) && !string.Equals(model.Email, agent.Email, StringComparison.OrdinalIgnoreCase)) blockedFields.Add("email address");
            if (!string.IsNullOrWhiteSpace(model.UserName) && !string.Equals(model.UserName, agent.UserName, StringComparison.OrdinalIgnoreCase)) blockedFields.Add("username");
            if (model.PackageId != 0 && model.PackageId != agent.PackageId) blockedFields.Add("package");
            if (model.IsActive && !agent.IsActive) blockedFields.Add("active status");
            if (model.MustChangePassword && !agent.MustChangePassword) blockedFields.Add("must-change-password flag");
            if (!string.IsNullOrWhiteSpace(model.DomainName) && !string.Equals(model.DomainName, agent.DomainName, StringComparison.OrdinalIgnoreCase)) blockedFields.Add("setup domain");

            // Put the stored values back so ApplyEditModel cannot write the attempted ones. Reverting
            // the model rather than skipping the save keeps every other edit on the form working.
            model.Email = agent.Email;
            model.UserName = agent.UserName;
            model.PackageId = agent.PackageId;
            model.IsActive = agent.IsActive;
            model.MustChangePassword = agent.MustChangePassword;
            model.DomainName = agent.DomainName;

            if (blockedFields.Count > 0)
            {
                await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentEditBlocked",
                    $"Non-SuperAdmin attempted to change {string.Join(", ", blockedFields)} on agent '{agent.UserName}'. Change was not applied.");
            }
        }

        ValidateAgentEdit(model);
        await ValidateUniqueAgentFieldsAsync(id, model);
        if (!ModelState.IsValid)
        {
            await LoadActivePackagesAsync();
            return View(model);
        }

        ApplyEditModel(agent, model);

        await _agents.UpdateAsync(agent);
        await LogAsync(id, "Edit", "Agent profile updated");
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentEdit", $"Agent '{agent.UserName}' profile updated");

        // Say what was refused. Silently discarding an edit is its own bug: the admin believes the
        // change took, and finds out later from a confused agent.
        TempData["Success"] = $"Agent {agent.UserName} updated.";
        if (blockedFields.Count > 0)
        {
            TempData["Warning"] = $"Your other changes were saved, but the {string.Join(", ", blockedFields)} " +
                                  "can only be changed by a Super Admin. Please ask one to make that change.";
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    // Read-only "what would be deleted" view. Deliberately the same predicates the erasure runs, so the
    // preview can never claim something different from what deletion actually does.
    [Authorize(Policy = "SuperAdmin")]
    public async Task<IActionResult> ErasurePreview(int id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();
        ViewBag.Agent = agent;
        return View(await IPRO.DataAccess.AgentDataEraser.PreviewAsync(_db, id));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Policy = "SuperAdmin")]
    public async Task<IActionResult> Delete(int id, bool eraseFinancialRecords = false)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();
        var userName = agent.UserName;

        // M15 (erasure wave 2026-08-26): a person the business OWES money cannot be deleted.
        // The refund queue is worked manually; deleting the agent either buried the Pending row
        // among retained records nobody revisits, or (eraseFinancialRecords) destroyed it
        // outright -- and the billing waves WIDENED this by minting refund rows from more doors.
        // Refused before anything is attempted: no lockout, no PayPal call, nothing.
        var unresolvedRefunds = (await _uow.SubscriptionChanges.FindAsync(c =>
            c.AgentUserId == id && c.RefundStatus == RefundStatus.Pending)).ToList();
        if (unresolvedRefunds.Count > 0)
        {
            var owedGross = unresolvedRefunds.Sum(c => c.RefundGrossAmount);
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentDeleteRefused",
                $"Agent '{userName}' (id {id}) NOT deleted: {unresolvedRefunds.Count} unresolved refund(s) " +
                $"totalling {owedGross:0.00} sit in the Refunds queue. Resolve them (Refunded or Waived) first.");
            TempData["Error"] = $"{userName} is owed {owedGross:0.00} across {unresolvedRefunds.Count} unresolved " +
                                "refund(s). Resolve them in the Refunds queue (mark Refunded or Waived) before " +
                                "deleting — deleting now would bury money the business owes.";
            return RedirectToAction(nameof(Details), new { id });
        }

        // Blob storage is resolved up front and treated as required, for the same reason billing is:
        // once the rows are deleted, the URLs of the agent's uploaded files are gone with them, so
        // proceeding without working storage would strand every file permanently and unrecoverably.
        IBlobStorageService blobs;
        try
        {
            blobs = _services.GetRequiredService<IBlobStorageService>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Blob storage unavailable; refusing to delete agent {AgentId}", id);
            TempData["Error"] = $"File storage isn't configured for the admin app, so {userName}'s uploaded files " +
                                "(logo, photo, documents, gallery) could not be deleted. The account was NOT deleted -- " +
                                "deleting the rows first would strand those files permanently. Set Azure__StorageConnectionString " +
                                "and Azure__StorageAccountName on the admin app, then retry.";
            return RedirectToAction(nameof(Details), new { id });
        }

        // Billing next, and fatal if it fails. Deleting the account while the PayPal subscription is
        // still live would keep charging a customer who no longer exists in the system, with no record
        // left to trace the charge back to -- strictly worse than refusing to delete.
        //
        // The active-subscription check has to happen here rather than relying on the return value:
        // CancelSubscriptionAsync returns false both when cancellation fails AND when there is simply
        // nothing to cancel. Treating those the same makes free/promo agents, and any agent whose
        // billing rows are already gone, permanently undeletable.
        var hasActiveSubscription = (await _uow.Billings.FindAsync(
            b => b.AgentUserId == id && b.Status == BillingStatus.Active)).Any();
        try
        {
            if (hasActiveSubscription && !await _billing.CancelSubscriptionAsync(id))
            {
                TempData["Error"] = $"Could not cancel {userName}'s PayPal subscription, so the account was NOT deleted. " +
                                    "Cancel the subscription in PayPal first, then retry -- otherwise billing would continue with no account attached.";
                return RedirectToAction(nameof(Details), new { id });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PayPal cancellation failed for agent {AgentId}; deletion aborted", id);
            TempData["Error"] = $"PayPal cancellation errored ({ex.Message}). Agent {userName} was NOT deleted.";
            return RedirectToAction(nameof(Details), new { id });
        }

        // Values needed after the rows are gone must be read while they still exist.
        var domainName = agent.DomainName;
        _db.ChangeTracker.Clear();

        // ROWS BEFORE FILES, since 2026-08-24 (audit finding C1). This ordering was the OPPOSITE
        // until today, and the reversal is deliberate:
        //
        //   The shared-file check can only tell "another agent still uses this" from "this was
        //   only ever mine" once this agent's own rows are gone. Deleting files first therefore
        //   made that check structurally impossible -- it ran inside EraseAsync, produced a
        //   correct filtered list, and the caller had already deleted the unfiltered one. A5-H12
        //   was shipped, tested and green on 2026-08-18 and never protected production once.
        //
        //   The old ordering existed for a real reason: rows are the only record of where an
        //   agent's files live, so a crash between the two once stranded 10 files permanently
        //   (2026-08-04). That risk is now carried by the log line below -- written before
        //   anything is destroyed, listing every candidate URL -- which makes stranding
        //   recoverable by hand. A wrongly deleted file that another agent still displays is not
        //   recoverable at all, so the trade goes this way.
        var plan = await IPRO.DataAccess.AgentDataEraser.PreviewAsync(_db, id);

        // The recovery record: even an unexpected crash mid-erasure leaves exactly which files
        // belonged to this agent, in the log, before a single byte is deleted.
        if (plan.Blobs.Count > 0)
        {
            _logger.LogInformation("Deleting agent {AgentId} ({UserName}); candidate files: {BlobUrls}",
                id, userName, string.Join(" | ", plan.Blobs));
        }

        // ADMIN-6, reordered by the erasure wave (H11, 2026-08-26): the domain LIST is read here
        // -- it dies with the rows -- but the Azure unbind itself now runs AFTER the shred
        // commits. The old order unbound first, so a shred failure rolled back the rows while the
        // customer's live domain and certificate were already gone: their site dark, their
        // account "intact", and rebinding a manual DNS-and-cert crawl. The reversed trade: a
        // post-shred unbind failure leaves only a dangling binding on the Azure app (the site
        // already serves the not-published page), logged for manual cleanup -- annoying,
        // recoverable, and invisible to the customer. Same reasoning as ROWS BEFORE FILES.
        var domainsToUnbind = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(_db.AgentDomains.Where(d => d.AgentUserId == id));
        // Lazily resolved for the same reason the blob service is (see the _services comment above).
        var _azureDomains = _services.GetService<IPRO.Utility.IAzureDomainAutomationService>();

        // M14 (erasure wave): an erase failure must never surface as a raw 500 with no audit
        // trail. The lockout inside EraseAsync precedes its transaction, so a failure here leaves
        // exactly the promised state -- locked out but intact, Activate restores -- and the admin
        // is told so, in the audit log and on screen.
        IPRO.DataAccess.AgentErasureReport report;
        try
        {
            report = await IPRO.DataAccess.AgentDataEraser.EraseAsync(_db, id, eraseFinancialRecords);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EraseAsync failed for agent {AgentId}; rows rolled back, nothing external touched", id);
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentDeleteFailed",
                $"Agent '{userName}' (id {id}) NOT deleted: the erasure failed ({ex.Message}). The transaction " +
                "rolled back — rows intact, no files deleted, domains still bound. The account is deactivated " +
                "only; use Activate to restore it, and investigate before retrying.");
            TempData["Error"] = $"Deletion FAILED for {userName}: {ex.Message}. Nothing was deleted — the account " +
                                "is deactivated only (Activate restores it). Their files and domains are untouched. " +
                                "Investigate before retrying.";
            return RedirectToAction(nameof(Details), new { id });
        }

        // H10: the eraser VETOES a shred that would lose retained financial rows -- it rolls back
        // and returns nothing to delete. Stop here: the agent is locked out but otherwise intact
        // (Admin -> Activate restores them), and no file may be touched.
        if (report.RetentionViolated)
        {
            _logger.LogError(
                "RETENTION VIOLATION deleting agent {AgentId}: {Shortfall} retained financial rows would have been lost; erasure rolled back, no files deleted.",
                id, report.RetentionShortfallRows);
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentDeleteAborted",
                $"Agent '{userName}' (id {id}) NOT deleted: {report.RetentionShortfallRows} retained financial row(s) " +
                "would have been destroyed by the shred. Transaction rolled back; no files deleted. The account is " +
                "deactivated -- use Activate to restore it. Investigate the FK cascade before retrying.");
            TempData["Error"] = $"Deletion ABORTED for {userName}: the shred would have destroyed " +
                                $"{report.RetentionShortfallRows} retained financial row(s), so nothing was deleted. " +
                                "The account has been deactivated only; use Activate to restore it. " +
                                "This needs investigation before any further deletions.";
            return RedirectToAction(nameof(Details), new { id });
        }

        // H11: the shred has committed -- NOW the Azure bindings may go. Per-host failures are
        // logged and never block the rest; the dangling-binding cleanup is manual by design.
        foreach (var domain in _azureDomains == null ? new List<IPRO.Entities.AgentDomain>() : domainsToUnbind)
        {
            // 553: a short address pointed straight at the platform has its own binding and
            // certificate in Azure. Only when one was ever made: a forwarded name has nothing there.
            var rootHost = domain.RootAzureBindingStatus != IPRO.Entities.AgentDomainStatus.NotConfigured ? domain.RootDomain : null;
            foreach (var host in new[] { domain.DomainName, domain.WwwDomain, rootHost }.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h!).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    // 591: the agent's rows are gone; a host another agent's row still names is that agent's site.
                    if (await AgentDomainHosts.StillNamedAsync(_db, host))
                    {
                        _logger.LogWarning("Azure unbind of {Host} skipped while deleting agent {AgentId}: another domain row still names it", IPRO.Utility.PlatformHostNames.Printable(host), id);
                        continue;
                    }

                    var unbind = await _azureDomains.RemoveDomainAsync(host);
                    if (!unbind.Success)
                    {
                        _logger.LogError("Azure unbind failed for {Host} while deleting agent {AgentId}: {Message}", host, id, unbind.Message);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Azure unbind threw for {Host} while deleting agent {AgentId}", host, id);
                }
            }
        }

        // ONLY the eraser's filtered list may be deleted -- report.Blobs excludes files another
        // agent still references (report.SharedBlobsKept). Never plan.Blobs.
        var blobsDeleted = 0;
        foreach (var url in report.Blobs)
        {
            try
            {
                if (await blobs.DeleteAsync(url)) blobsDeleted++;
            }
            catch (Exception ex)
            {
                // A single unreachable blob shouldn't block the erasure; the URL is in the log above.
                _logger.LogError(ex, "Failed deleting blob {BlobUrl} for agent {AgentId}", url, id);
            }
        }

        var financialNote = eraseFinancialRecords
            ? "Financial records erased too (test-agent shred)."
            : $"Financial records retained: {report.RetainedInvoices} invoices ({report.RetainedRows} rows).";
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentDelete",
            $"Agent '{userName}' (id {id}) permanently deleted. PayPal subscription cancelled. " +
            $"{report.TotalRows} rows across {report.TableCount} tables; {blobsDeleted}/{report.Blobs.Count} files removed; " +
            $"{report.SharedBlobsKept.Count} file(s) kept because another agent still references them. {financialNote}");

        TempData["Warning"] = $"Agent {userName} deleted: {report.TotalRows} rows across {report.TableCount} tables, " +
                              $"{blobsDeleted} uploaded files, PayPal subscription cancelled. {financialNote}";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Policy = "SuperAdmin")]
    public async Task<IActionResult> ResetPassword(int id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();

        var temporaryPassword = BuildTemporaryPassword();
        agent.PasswordHash = _hasher.HashPassword(agent, temporaryPassword);
        agent.MustChangePassword = true;
        agent.PasswordChangedAt = null;

        await _agents.UpdateAsync(agent);
        // 583: a new password unlocks sign-in at once.
        var signIn = new SignInAccount(SignInAccountKind.Adviser, agent.Id);
        if (await SignInLockout.ClearAsync(_db, signIn, DateTime.UtcNow))
            SignInLockout.LogCleared(_logger, signIn, $"temporary password by {CurrentAdminUsername}");
        await LogAsync(id, "ResetPassword", "Temporary password reset by Super Admin");
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentResetPassword", $"Temporary password reset for agent '{agent.UserName}'");

        TempData["Success"] = $"Temporary password for {agent.UserName} reset to: {temporaryPassword}";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EmailInvoice(int id, int invoiceId)
    {
        var invoice = await _uow.Invoices.GetByIdAsync(invoiceId);
        if (invoice == null || invoice.AgentUserId != id)
        {
            return NotFound();
        }

        var result = await _billing.EmailPaidInvoiceAsync(invoiceId, force: true);
        if (result.Success)
        {
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentEmailInvoice", $"Invoice {invoice.InvoiceNumber} resent to agent id {id}");
        }
        TempData[result.Success ? "Success" : "Error"] = result.Success
            ? $"Invoice {invoice.InvoiceNumber} emailed to the agent's current email address."
            : result.Message;

        return RedirectToAction(nameof(Details), new { id });
    }

    // Pre-launch audit (2026-08-30): these carried no policy while every sensitive sibling
    // (Delete, ResetPassword, ErasurePreview, both Rebuilds) is SuperAdmin-only, and the
    // class-level AdminAccess policy is literally "is signed in". A Support admin could sign an
    // agent AND their whole team out instantly -- and Edit already blocks this same IsActive field
    // for non-SuperAdmins, so the buttons contradicted the documented intent.
    [Authorize(Policy = "SuperAdmin")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Activate(int id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();
        agent.IsActive = true;
        await _agents.UpdateAsync(agent);
        await LogAsync(id, "Activate", "Agent activated");
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentActivate", $"Agent '{agent.UserName}' activated");
        TempData["Success"] = $"Agent {agent.UserName} activated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // Pre-launch audit (2026-08-30): these carried no policy while every sensitive sibling
    // (Delete, ResetPassword, ErasurePreview, both Rebuilds) is SuperAdmin-only, and the
    // class-level AdminAccess policy is literally "is signed in". A Support admin could sign an
    // agent AND their whole team out instantly -- and Edit already blocks this same IsActive field
    // for non-SuperAdmins, so the buttons contradicted the documented intent.
    [Authorize(Policy = "SuperAdmin")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Deactivate(int id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();
        await _agents.DeactivateAsync(id);
        await LogAsync(id, "Deactivate", "Agent deactivated");
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentDeactivate", $"Agent '{agent.UserName}' deactivated");
        TempData["Warning"] = $"Agent {agent.UserName} deactivated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // ProvisionHosting/PleskLogin removed 2026-08-04 along with the rest of the Plesk integration:
    // agent sites are hosted on Azure now, so there is no control panel to provision against or log
    // into. Agent domains are managed through AgentDomain + the domain automation job instead.

    private async Task LogAsync(int agentId, string action, string desc)
    {
        await _uow.OperateLogs.AddAsync(new OperateLog { AgentUserId = agentId, Action = action, Module = "Agents", Description = desc, CreatedAt = DateTime.UtcNow });
        await _uow.SaveChangesAsync();
    }

    private async Task<T?> LoadDetailsPanelAsync<T>(Func<Task<T>> load, string panelName, List<string> warnings)
    {
        try
        {
            return await load();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load {PanelName} on agent details page.", panelName);
            warnings.Add($"{panelName} could not be loaded because some legacy data needs cleanup.");
            return default;
        }
    }

    // DeleteAgentOwnedDataAsync/RemoveEach removed 2026-08-04: they covered 15 entity types and had
    // fallen ~25 behind the schema, silently orphaning everything added since. Replaced by
    // IPRO.DataAccess.AgentDataEraser, which drives deletion from one declarative table map that also
    // powers the preview screen. Add new agent-owned tables there, not here.

    private static string BuildTemporaryPassword() => EncryptionService.GenerateToken(12);

    // 593: a team member with two-step sign-in on, for the agent record's Two-step card.
    public sealed record TwoStepTeamRow(int Id, string FullName, string Email, string Method, DateTime? CodeLockedUntil, int WrongCodes)
    {
        public bool Held => WrongCodes >= TwoStepStore.WrongCodesBeforeHold;
    }

    private static AgentEditViewModel ToEditModel(AgentUser agent) => new()
    {
        Id = agent.Id,
        UserName = agent.UserName ?? "",
        Email = agent.Email ?? "",
        FirstName = agent.FirstName ?? "",
        LastName = agent.LastName ?? "",
        Designation = agent.Designation ?? "",
        CompanyName = agent.CompanyName ?? "",
        CompanyAddress = agent.CompanyAddress ?? "",
        City = agent.City ?? "",
        Province = agent.Province ?? "",
        PostalCode = agent.PostalCode ?? "",
        Country = agent.Country ?? "",
        TimeZone = agent.TimeZone ?? "",
        Phone = agent.Phone ?? "",
        BusinessFax = agent.BusinessFax ?? "",
        CellPhone = agent.CellPhone ?? "",
        BusinessType = agent.BusinessType ?? "",
        DomainName = agent.DomainName ?? "",
        PackageId = agent.PackageId,
        PromotionCode = agent.PromotionCode ?? "",
        IsActive = agent.IsActive,
        MustChangePassword = agent.MustChangePassword
    };

    private static void ApplyEditModel(AgentUser agent, AgentEditViewModel model)
    {
        agent.UserName = model.UserName;
        agent.Email = model.Email;
        agent.FirstName = model.FirstName;
        agent.LastName = model.LastName;
        agent.Designation = model.Designation ?? "";
        agent.CompanyName = model.CompanyName;
        agent.CompanyAddress = model.CompanyAddress ?? "";
        agent.City = model.City;
        agent.Province = model.Province;
        agent.PostalCode = model.PostalCode;
        agent.Country = model.Country;
        agent.TimeZone = model.TimeZone ?? "";
        agent.Phone = model.Phone;
        agent.BusinessFax = model.BusinessFax ?? "";
        agent.CellPhone = model.CellPhone ?? "";
        agent.BusinessType = model.BusinessType;
        agent.DomainName = model.DomainName ?? "";
        agent.PackageId = model.PackageId;
        agent.PromotionCode = model.PromotionCode ?? "";
        agent.IsActive = model.IsActive;
        agent.MustChangePassword = model.MustChangePassword;
    }

    private static void NormalizeAgent(AgentEditViewModel agent)
    {
        agent.UserName = agent.UserName?.Trim() ?? "";
        agent.Email = (agent.Email?.Trim() ?? "").ToLowerInvariant();
        agent.FirstName = agent.FirstName?.Trim() ?? "";
        agent.LastName = agent.LastName?.Trim() ?? "";
        agent.Designation = agent.Designation?.Trim() ?? "";
        agent.CompanyName = agent.CompanyName?.Trim() ?? "";
        agent.CompanyAddress = agent.CompanyAddress?.Trim() ?? "";
        agent.City = agent.City?.Trim() ?? "";
        agent.Province = agent.Province?.Trim() ?? "";
        agent.PostalCode = agent.PostalCode?.Trim() ?? "";
        agent.Country = agent.Country?.Trim() ?? "";
        agent.TimeZone = agent.TimeZone?.Trim() ?? "";
        agent.Phone = agent.Phone?.Trim() ?? "";
        agent.BusinessFax = agent.BusinessFax?.Trim() ?? "";
        agent.CellPhone = agent.CellPhone?.Trim() ?? "";
        agent.BusinessType = agent.BusinessType?.Trim() ?? "";
        agent.DomainName = CanonicalizeDomain(agent.DomainName);
        agent.PromotionCode = agent.PromotionCode?.Trim() ?? "";
    }

    // Canonical hostname form (audit #2, A2-H7): trimmed, lowercase, no trailing dot, IDN mapped
    // to punycode so visually-identical Unicode hostnames cannot dodge the ownership checks.
    // Malformed input is passed through unchanged; validation rejects it with a clear message.
    private static string CanonicalizeDomain(string? raw)
    {
        var value = (raw ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (value.Length == 0) return "";
        try
        {
            return new System.Globalization.IdnMapping().GetAscii(value);
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    private void ValidateAgentEdit(AgentEditViewModel agent)
    {
        if (string.IsNullOrWhiteSpace(agent.UserName)) ModelState.AddModelError("", "Username is required.");
        if (string.IsNullOrWhiteSpace(agent.Email)) ModelState.AddModelError("", "Email is required.");
        if (string.IsNullOrWhiteSpace(agent.FirstName)) ModelState.AddModelError("", "First name is required.");
        if (string.IsNullOrWhiteSpace(agent.LastName)) ModelState.AddModelError("", "Last name is required.");
        if (string.IsNullOrWhiteSpace(agent.CompanyName)) ModelState.AddModelError("", "Company name is required.");
        if (string.IsNullOrWhiteSpace(agent.City)) ModelState.AddModelError("", "City is required.");
        if (string.IsNullOrWhiteSpace(agent.Province)) ModelState.AddModelError("", "Province is required.");
        if (string.IsNullOrWhiteSpace(agent.PostalCode)) ModelState.AddModelError("", "Postal code is required.");
        if (string.IsNullOrWhiteSpace(agent.Country)) ModelState.AddModelError("", "Country is required.");
        if (string.IsNullOrWhiteSpace(agent.Phone)) ModelState.AddModelError("", "Business phone is required.");
        if (string.IsNullOrWhiteSpace(agent.BusinessType)) ModelState.AddModelError("", "Business type is required.");
        if (agent.PackageId <= 0) ModelState.AddModelError("", "Package is required.");
    }

    private async Task ValidateUniqueAgentFieldsAsync(int id, AgentEditViewModel agent)
    {
        var existingUserName = await _uow.AgentUsers.FirstOrDefaultAsync(a => a.UserName == agent.UserName && a.Id != id);
        if (existingUserName != null) ModelState.AddModelError("", "Username is already used by another agent.");

        var existingEmail = await _uow.AgentUsers.FirstOrDefaultAsync(a => a.Email == agent.Email && a.Id != id);
        if (existingEmail != null) ModelState.AddModelError("", "Email is already used by another agent.");

        if (!string.IsNullOrWhiteSpace(agent.DomainName))
        {
            var domain = agent.DomainName;

            // Audit #2 (A2-H7): the value must be a bare canonical hostname before any comparison
            // means anything. CanonicalizeDomain already lowercased, stripped a trailing dot and
            // punycoded it; anything still carrying URL machinery is rejected outright.
            if (domain.IndexOfAny(new[] { '/', ':', '?', '#', '@', ' ' }) >= 0)
            {
                ModelState.AddModelError("", "Setup domain must be a bare domain name (like agentname.247advisers.com) - no http://, paths, ports or spaces.");
                return;
            }
            if (domain is "247advisers.com" or "www.247advisers.com" or "iproadvisers.com"
                || domain.EndsWith(".iproadvisers.com", StringComparison.Ordinal)
                || domain.EndsWith(".azurewebsites.net", StringComparison.Ordinal))
            {
                ModelState.AddModelError("", "That hostname is reserved by the platform.");
                return;
            }

            // Public resolution treats root and www forms of a hostname as the same site, so the
            // ownership check must too -- an exact-string comparison let www.victim.example be
            // assigned while another agent owned victim.example (A2-H7).
            var root = domain.StartsWith("www.", StringComparison.Ordinal) ? domain[4..] : domain;
            var www = "www." + root;

            var existingDomain = await _uow.AgentUsers.FirstOrDefaultAsync(a =>
                (a.DomainName == domain || a.DomainName == root || a.DomainName == www) && a.Id != id);
            if (existingDomain != null) ModelState.AddModelError("", "Domain is already used by another agent.");

            var websiteClash = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .AnyAsync(_db.AgentWebsites, w => w.AgentUserId != id &&
                    (w.CustomDomain == domain || w.CustomDomain == root || w.CustomDomain == www));
            var domainClash = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .AnyAsync(_db.AgentDomains, d => d.AgentUserId != id &&
                    (d.DomainName == domain || d.DomainName == root || d.DomainName == www
                     || d.RootDomain == root || d.WwwDomain == www));
            if (websiteClash || domainClash)
            {
                ModelState.AddModelError("", "Domain is already claimed by another agent's website or domain binding.");
            }
        }
    }

    private async Task LoadActivePackagesAsync()
    {
        var packages = await _uow.BillingRules.FindAsync(p => p.IsActive);
        ViewBag.Packages = packages
            .OrderBy(GetPackageRank)
            .ThenBy(p => p.MonthlyPrice <= 0 ? decimal.MaxValue : p.MonthlyPrice)
            .ThenBy(p => p.PackageName)
            .ToList();
    }

    private static int GetPackageRank(BillingRule package) => package.PackageName switch
    {
        "IPro Silver" => 1,
        "IPro Gold" => 2,
        "IPro Platinum" => 3,
        "Broker Package" => 4,
        _ => 50
    };
}
