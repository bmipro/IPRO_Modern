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
// call carries, as a bearer token, the one-time pass the adviser's browser brought to the builder, or
// the session the builder got for it; nothing here reads a cookie or answers a signed-in adviser.
//
//   POST builder-api/session    the pass, traded for a session (once; ten minutes)
//   GET  builder-api/session    whether the session still stands
//   GET  builder-api/handover   a new hand-over of the adviser's site as it is now: the package
//   POST builder-api/preview    "See it on my site": a look at the draft through the site's own design
//   POST builder-api/return     "Send to iPro": the draft, waiting for review; nothing on the site changes
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("builder-api")]
public class BuilderApiController : Controller
{
    // The contract's 40 MB and a little room.
    private const long BodyLimit = BuilderContract.MaxZipBytes + 1024 * 1024;

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
        var (granted, refusal) = await BuilderBeta.RedeemPassAsync(_db, Bearer(), DateTime.UtcNow);
        if (granted == null) return Problem401(refusal);
        var agent = granted.Agent;
        var adviser = !string.IsNullOrWhiteSpace(agent.CompanyName) ? agent.CompanyName.Trim()
            : !string.IsNullOrWhiteSpace(granted.Site.SiteTitle) ? granted.Site.SiteTitle.Trim()
            : $"{agent.FirstName} {agent.LastName}".Trim();
        _logger.LogInformation("Builder session opened for agent {AgentId} ({Kind})", agent.Id, granted.Session.StartFresh ? "fresh" : "continue");
        return Json(new
        {
            tenant = granted.Beta.Tenant,
            sessionToken = granted.Token,
            sessionExpiresAt = DateTime.SpecifyKind(granted.Session.ExpiresAt, DateTimeKind.Utc),
            handoverId = granted.Session.HandoverId,
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
        var (session, _, _) = await BuilderBeta.FindSessionAsync(_db, Bearer(), DateTime.UtcNow);
        return session == null
            ? Problem401("Your session has ended. Open the builder again from My Website.")
            : Json(new { sessionExpiresAt = DateTime.SpecifyKind(session.ExpiresAt, DateTimeKind.Utc) });
    }

    // A fresh hand-over (572) of the site as it is now. From then on the session works on it: only a
    // package of THIS hand-over is taken back (it is also the site's newest, so no older one can be).
    [HttpGet("handover")]
    public async Task<IActionResult> Handover()
    {
        var (session, site, _) = await BuilderBeta.FindSessionAsync(_db, Bearer(), DateTime.UtcNow);
        if (session == null || site == null) return Problem401("Your session has ended. Open the builder again from My Website.");
        var agentId = site.AgentUserId;
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
        var (session, site, beta) = await BuilderBeta.FindSessionAsync(_db, Bearer(), DateTime.UtcNow);
        if (session == null || site == null || beta == null) return Problem401("Your session has ended. Open the builder again from My Website.");

        var bytes = await ReadBodyAsync();
        if (bytes == null)
            return UnprocessableEntity(new { message = "Your site is larger than iPro takes in one go (40 MB of pictures). Remove some pictures and try again." });
        // A session works on one hand-over: a package of any other is not this session's to send.
        if (session.HandoverId.Length == 0 || BuilderPackageReader.PeekHandoverId(bytes) != session.HandoverId)
            return UnprocessableEntity(new { message = "This draft belongs to an earlier visit. Open the builder again from My Website." });

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
            import = await BuilderImportService.StoreAsync(_db, blob, site, read, preview ? "see-it-on-my-site.zip" : "sent-from-the-builder.zip", BuilderBeta.CustomerSender);
        }
        catch (Exception ex) when (ex is Azure.RequestFailedException or IOException or InvalidOperationException or DbUpdateException)
        {
            _logger.LogError(ex, "A package the builder sent for agent {AgentId} could not be stored", site.AgentUserId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "iPro could not store your pictures just now. Nothing on your website changed. Try again in a minute." });
        }
        import.Source = WebsiteBuilderImportSources.Customer;
        if (preview) import.Status = WebsiteBuilderImportStatus.Preview;
        await _db.SaveChangesAsync();

        // What the adviser looked at or sent before is replaced by this: one look, one waiting package.
        // Dropped only now, so a send that fails keeps the one before; a picture the new one shares
        // with them stays (BuilderImportService keeps what another package still links).
        foreach (var older in await _db.WebsiteBuilderImports
                     .Where(i => i.AgentWebsiteId == site.Id && i.Id != import.Id && i.Source == WebsiteBuilderImportSources.Customer
                                 && (i.Status == WebsiteBuilderImportStatus.Preview || (!preview && i.Status == WebsiteBuilderImportStatus.Pending)))
                     .ToListAsync())
        {
            if (older.Status == WebsiteBuilderImportStatus.Preview) await BuilderImportService.DropPreviewAsync(_db, blob, older);
            else await BuilderImportService.DiscardAsync(_db, blob, older);
        }

        if (preview)
        {
            var key = await BuilderImportService.LookKeyAsync(_db, import);
            var slug = (page ?? string.Empty).Trim();
            var address = $"{PortalBase}/PublicWebsite/PreviewBuilderImport?id={import.Id}&key={key}"
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
