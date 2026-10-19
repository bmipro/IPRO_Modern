using System.Security.Cryptography;
using System.Text;
using IPRO.Business.Interfaces;
using IPRO.Business.SiteImport;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

// 585: the doors iPro opens to the builder's SERVER, never to a browser (CONTRACT.md section 11). Each
// call carries the secret the two servers share (X-Ipro-Service-Key, Builder:ServiceKey): without it
// these addresses do not exist. Then, as a bearer token, the one-time pass the adviser's browser brought
// to the builder, or the session the builder got for it; nothing here reads a cookie or answers a
// signed-in adviser. A session stands while the Beta is on, the account is open, and the adviser's
// package still includes their website and their billing is in order.
//
//   POST builder-api/session    the pass, traded for a session (once; ten minutes)
//   GET  builder-api/session    whether the session still stands
//   GET  builder-api/handover   a new hand-over of the adviser's site as it is now: the package
//   POST builder-api/preview    "See it on my site": a look at the draft through the site's own design
//   POST builder-api/return     "Send to iPro": the draft, waiting for review; nothing on the site changes
//
// Only the builder's server calls here, from one address, so iPro's limit per address does not count
// these (IpRateLimiting's EndpointWhitelist); the builder limits each customer's calls itself.
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("builder-api")]
public class BuilderApiController : Controller
{
    public const string ServiceKeyHeader = "X-Ipro-Service-Key";
    // The contract's 40 MB and a little room.
    private const long BodyLimit = BuilderContract.MaxZipBytes + 1024 * 1024;
    private const string Ended = "Your session has ended. Open the builder again from My Website.";

    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _services;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<BuilderApiController> _logger;

    public BuilderApiController(IPRODbContext db, IPackageEntitlementService entitlements, IConfiguration configuration,
        IServiceProvider services, IWebHostEnvironment environment, ILogger<BuilderApiController> logger)
    {
        _db = db;
        _entitlements = entitlements;
        _configuration = configuration;
        _services = services;
        _environment = environment;
        _logger = logger;
    }

    private string PortalBase => WebAppUrlHelper.GetWebAppBaseUrl(_configuration);

    [HttpPost("session")]
    public async Task<IActionResult> Open()
    {
        if (!FromTheBuilder()) return NotFound();
        var now = DateTime.UtcNow;
        var (granted, refusal) = await BuilderBeta.RedeemPassAsync(_db, Bearer(), now);
        if (granted == null) return Problem401(refusal);
        var agent = granted.Agent;
        if (await NotIncludedAsync(agent.Id) is { } notIncluded)
        {
            granted.Session.EndedAt = now;
            await _db.SaveChangesAsync();
            return Problem401(notIncluded);
        }
        var adviser = !string.IsNullOrWhiteSpace(agent.CompanyName) ? agent.CompanyName.Trim()
            : !string.IsNullOrWhiteSpace(granted.Site.SiteTitle) ? granted.Site.SiteTitle.Trim()
            : $"{agent.FirstName} {agent.LastName}".Trim();
        _logger.LogInformation("Builder session opened for agent {AgentId} ({Kind})", agent.Id, granted.Session.StartFresh ? "start again" : "open");
        return Json(new
        {
            tenant = granted.Beta.Tenant,
            sessionToken = granted.Token,
            sessionExpiresAt = DateTime.SpecifyKind(granted.Session.ExpiresAt, DateTimeKind.Utc),
            // Where the site's newest hand-over stands: the builder continues a draft of it only while
            // it is "open" (CONTRACT.md section 11, step 2).
            current = new
            {
                handoverId = granted.Current.HandoverId,
                state = granted.Current.State,
                at = granted.Current.At.HasValue ? DateTime.SpecifyKind(granted.Current.At.Value, DateTimeKind.Utc) : (DateTime?)null
            },
            startFresh = granted.Session.StartFresh,
            adviser = adviser.Length <= 120 ? adviser : adviser[..120],
            publishing = granted.Beta.AdviserPublishes ? "adviser" : "staff",
            returnTo = PortalBase + "/portal/Website",
            look = BuilderBeta.Look(granted.Site)
        });
    }

    [HttpGet("session")]
    public async Task<IActionResult> Check()
    {
        if (!FromTheBuilder()) return NotFound();
        var (session, site, _) = await BuilderBeta.FindSessionAsync(_db, Bearer(), DateTime.UtcNow);
        if (session == null || site == null) return Problem401(Ended);
        if (await NotIncludedAsync(site.AgentUserId) is { } notIncluded) return Problem401(notIncluded);
        return Json(new { sessionExpiresAt = DateTime.SpecifyKind(session.ExpiresAt, DateTimeKind.Utc) });
    }

    // A fresh hand-over (572) of the site as it is now. From then on the session works on it: only a
    // package of THIS hand-over is taken back (it is also the site's newest, so no older one can be).
    [HttpGet("handover")]
    public async Task<IActionResult> Handover()
    {
        if (!FromTheBuilder()) return NotFound();
        var (session, site, _) = await BuilderBeta.FindSessionAsync(_db, Bearer(), DateTime.UtcNow);
        if (session == null || site == null) return Problem401(Ended);
        var agentId = site.AgentUserId;
        if (await NotIncludedAsync(agentId) is { } notIncluded) return Problem401(notIncluded);
        var allowed = SiteHandoverService.AllowedBlockTypes(
            articles: (await _entitlements.GetAccessAsync(agentId, PackageFeatureCodes.Newsletters)).IsIncluded,
            blog: (await _entitlements.GetAccessAsync(agentId, PackageFeatureCodes.ManagedBlog)).IsIncluded,
            customForms: (await _entitlements.GetAccessAsync(agentId, PackageFeatureCodes.CustomForms)).IsIncluded);
        var capacity = await _entitlements.GetAccessAsync(agentId, PackageFeatureCodes.FileUploadCapacity);
        var blob = Storage();
        if (blob == null) return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "iPro's file storage did not answer. Try again in a minute." });

        var full = await _db.AgentWebsites.AsNoTracking().Include(w => w.AgentUser).Include(w => w.Template).FirstAsync(w => w.Id == site.Id);
        var handover = await SiteHandoverService.BuildAsync(_db, blob, full, allowed, AgentStorage.LimitBytes(capacity.LimitValue), ReadStarterBannerAsync);
        if (!handover.CanHandOver)
        {
            _logger.LogInformation("Builder hand-over refused for agent {AgentId}: {Count} reason(s)", agentId, handover.Blockers.Count);
            return UnprocessableEntity(new
            {
                message = "Your website cannot be opened in the new builder yet. Nothing on it changed. In the page editor:",
                reasons = handover.Blockers
            });
        }

        var record = await SiteHandoverService.RecordAsync(_db, full, handover, BuilderBeta.CustomerIssuer);
        session.HandoverId = record.HandoverId;
        session.StartFresh = false;
        await _db.SaveChangesAsync();
        // What the adviser sent or looked at of an earlier hand-over can no longer be brought in (only the
        // newest hand-over can come back): it is withdrawn now rather than left waiting for nothing. My
        // Website's "Start again" says so before it is pressed.
        foreach (var older in await _db.WebsiteBuilderImports
                     .Where(i => i.AgentWebsiteId == site.Id && i.Source == WebsiteBuilderImportSources.Customer && i.HandoverId != record.HandoverId
                                 && (i.Status == WebsiteBuilderImportStatus.Pending || i.Status == WebsiteBuilderImportStatus.Preview))
                     .ToListAsync())
        {
            if (older.Status == WebsiteBuilderImportStatus.Preview) await BuilderImportService.DropPreviewAsync(_db, blob, older);
            else if (await BuilderImportService.DiscardAsync(_db, blob, older))
                _logger.LogInformation("Agent {AgentId}'s waiting package {ImportId} was withdrawn by a new hand-over", agentId, older.Id);
        }
        _logger.LogInformation("Builder hand-over {HandoverId} made for agent {AgentId}", record.HandoverId, agentId);
        return File(handover.Zip, "application/zip", handover.FileName);
    }

    [HttpPost("preview")]
    [RequestSizeLimit(BodyLimit)]
    public Task<IActionResult> Preview([FromQuery] string? page) => TakeAsync(preview: true, page);

    [HttpPost("return")]
    [RequestSizeLimit(BodyLimit)]
    public Task<IActionResult> Return() => TakeAsync(preview: false, null);

    private async Task<IActionResult> TakeAsync(bool preview, string? page)
    {
        if (!FromTheBuilder()) return NotFound();
        var (session, site, beta) = await BuilderBeta.FindSessionAsync(_db, Bearer(), DateTime.UtcNow);
        if (session == null || site == null || beta == null) return Problem401(Ended);
        if (await NotIncludedAsync(site.AgentUserId) is { } notIncluded) return Problem401(notIncluded);

        var bytes = await ReadBodyAsync();
        if (bytes == null)
            return UnprocessableEntity(new { message = "Your site is larger than iPro takes in one go (40 MB of pictures). Remove some pictures and try again." });
        // A session works on one hand-over: a package of any other is not this session's to send.
        if (session.HandoverId.Length == 0 || BuilderPackageReader.PeekHandoverId(bytes) != session.HandoverId)
            return UnprocessableEntity(new { message = "This draft belongs to an earlier visit. Open the builder again from My Website." });
        // Its changes already went onto the website (and are there, or were put back): a second package of
        // it could not be told apart from what the first wrote. Opened again, the builder opens the site
        // as it is now and keeps this draft among its versions.
        if (await BuilderBeta.IsSpentAsync(_db, site.Id, session.HandoverId))
            return UnprocessableEntity(new { message = "The changes from this draft already went onto your website. To change it again, open the builder again from My Website: it opens your website as it is now and keeps this draft under Versions." });

        var capacity = await _entitlements.GetAccessAsync(site.AgentUserId, PackageFeatureCodes.FileUploadCapacity);
        var context = await BuilderImportService.ContextAsync(_db, site, AgentStorage.LimitBytes(capacity.LimitValue), session.HandoverId);
        var read = BuilderPackageReader.Read(bytes, context);
        if (!read.IsValid)
        {
            return UnprocessableEntity(new
            {
                message = preview ? "iPro cannot show this yet:" : "iPro cannot take this yet. Nothing on your website changed:",
                errors = read.Problems.Take(50).Select(p => new { page = p.Where, message = p.Message })
            });
        }

        var blob = Storage();
        if (blob == null) return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "iPro's file storage did not answer. Try again in a minute." });

        WebsiteBuilderImport import;
        try
        {
            // Stored as the adviser's, and a look as a look with its key, in one write.
            import = await BuilderImportService.StoreAsync(_db, blob, site, read, preview ? WebsiteBuilderImportSources.LookFileName : WebsiteBuilderImportSources.SentFileName, BuilderBeta.CustomerSender,
                preview ? WebsiteBuilderImportStatus.Preview : WebsiteBuilderImportStatus.Pending, WebsiteBuilderImportSources.Customer);
        }
        catch (Exception ex) when (ex is Azure.RequestFailedException or IOException or InvalidOperationException or DbUpdateException)
        {
            _logger.LogError(ex, "A package the builder sent for agent {AgentId} could not be stored", site.AgentUserId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "iPro could not store your pictures just now. Nothing on your website changed. Try again in a minute." });
        }

        // What the adviser looked at or sent before is replaced by this: one look, one waiting package.
        // Only what came BEFORE this one goes (two arriving at once never drop each other), and only now,
        // so a send that fails keeps the one before; a picture the new one shares with them becomes the
        // new one's (BuilderImportService.ReleaseImagesAsync).
        foreach (var older in await _db.WebsiteBuilderImports
                     .Where(i => i.AgentWebsiteId == site.Id && i.Id < import.Id && i.Source == WebsiteBuilderImportSources.Customer
                                 && (i.Status == WebsiteBuilderImportStatus.Preview || (!preview && i.Status == WebsiteBuilderImportStatus.Pending)))
                     .ToListAsync())
        {
            if (older.Status == WebsiteBuilderImportStatus.Preview) await BuilderImportService.DropPreviewAsync(_db, blob, older);
            else await BuilderImportService.DiscardAsync(_db, blob, older);
        }

        if (preview)
        {
            // The look keeps what publishing keeps: every section the adviser did not remove in the
            // builder (one added in the page editor meanwhile), as their review and SuperAdmin's do.
            var planned = await BuilderImportService.PlanAsync(_db, import);
            var keep = planned?.Plan == null ? new HashSet<int>() : WebsiteBuilderController.KeptSections(planned.Plan);
            var slug = (page ?? string.Empty).Trim();
            var address = $"{PortalBase}/PublicWebsite/PreviewBuilderImport?id={import.Id}&key={import.PreviewKey}"
                + (keep.Count > 0 ? "&sections=" + string.Join(",", keep.OrderBy(x => x)) : string.Empty)
                + (BuilderContract.Slug.IsMatch(slug) && slug.Length <= BuilderContract.MaxSlug ? "&page=" + Uri.EscapeDataString(slug) : string.Empty);
            return Json(new { previewUrl = address, expiresAt = DateTime.SpecifyKind(import.PreviewKeyExpiresAt!.Value, DateTimeKind.Utc) });
        }

        _logger.LogInformation("Agent {AgentId} sent their site from the builder (import {ImportId})", site.AgentUserId, import.Id);
        if (beta.AdviserPublishes)
        {
            return StatusCode(StatusCodes.Status202Accepted, new
            {
                publishing = "adviser",
                reviewUrl = $"{PortalBase}/portal/WebsiteBuilder/Review/{import.Id}",
                message = "Sent. Review it in iPro and publish it when you are ready."
            });
        }

        await NotifyStaffAsync(site, import);
        return StatusCode(StatusCodes.Status202Accepted, new
        {
            publishing = "staff",
            message = "Sent to iPro. iPro's team checks it and publishes it, and you will get an email. Your website does not change until then."
        });
    }

    // Whether the adviser's account still carries their website: billing in order, and a package that
    // includes it (what the portal's own My Website asks). A sentence when not: the builder ends the session.
    private async Task<string?> NotIncludedAsync(int agentId)
    {
        if (await _entitlements.IsAccessGatedAsync(agentId))
            return "Your iPro account needs attention under Billing, so the builder is closed for now. Your website has not changed.";
        if (!(await _entitlements.GetAccessAsync(agentId, PackageFeatureCodes.InstantWebsite)).IsIncluded)
            return "Your iPro package does not include a website any more, so the builder is closed. Your website has not changed.";
        return null;
    }

    // iPro's own mailbox hears of a site waiting for staff (the owner's decision 3 C). Never fails the send.
    private async Task NotifyStaffAsync(AgentWebsite site, WebsiteBuilderImport import)
    {
        try
        {
            var to = SignupNotice.Recipient(_configuration);
            if (to == null) return;
            var agent = await _db.AgentUsers.AsNoTracking().FirstAsync(a => a.Id == site.AgentUserId);
            var notice = BuilderBetaNotice.SentForReview(agent, import, _configuration);
            await _services.GetRequiredService<IEmailService>().SendAsync(to, "iPro", notice.Subject, notice.Html, notice.Text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The notice of a site sent from the builder (import {ImportId}) was not sent", import.Id);
        }
    }

    // The builder's server, by the secret the two share. Unset or short, no caller is.
    private bool FromTheBuilder()
    {
        var key = _configuration["Builder:ServiceKey"] ?? string.Empty;
        if (key.Length < 32) return false;
        var given = Request.Headers[ServiceKeyHeader].ToString();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(key));
    }

    private string? Bearer()
    {
        var header = Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..].Trim() : null;
    }

    private async Task<byte[]?> ReadBodyAsync()
    {
        if (Request.ContentLength > BuilderContract.MaxZipBytes) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > BuilderContract.MaxZipBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private UnauthorizedObjectResult Problem401(string message) => Unauthorized(new { message });

    private IBlobStorageService? Storage()
    {
        try { return _services.GetRequiredService<IBlobStorageService>(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Blob storage unavailable for the builder's doors");
            return null;
        }
    }

    // One of the page editor's starter banners, read from this app's own files (the name has already
    // been matched against the list the editor offers: SiteHandoverService.StarterBannerFile).
    private async Task<byte[]?> ReadStarterBannerAsync(string fileName)
    {
        var path = Path.Combine(_environment.WebRootPath, "images", "starter-banners", Path.GetFileName(fileName));
        if (!System.IO.File.Exists(path) || new FileInfo(path).Length > BuilderContract.MaxAssetBytes) return null;
        return await System.IO.File.ReadAllBytesAsync(path);
    }
}
