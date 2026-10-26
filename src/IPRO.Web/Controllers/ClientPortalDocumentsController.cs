using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize(AuthenticationSchemes = "ClientPortal")]
public class ClientPortalDocumentsController : Controller
{
    private const long MaxFileSizeBytes = 20 * 1024 * 1024;

    private readonly IPRODbContext _db;
    private readonly IBlobStorageService _blob;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClientPortalDocumentsController> _logger;
    private int ClientId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public ClientPortalDocumentsController(IPRODbContext db, IBlobStorageService blob, IPackageEntitlementService entitlements, IEmailService email, IConfiguration configuration, ILogger<ClientPortalDocumentsController> logger)
    {
        _db = db;
        _blob = blob;
        _entitlements = entitlements;
        _email = email;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        var documents = await _db.PortalDocuments
            .AsNoTracking()
            .Where(d => d.ClientId == ClientId)
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync();
        ViewBag.PortalReadOnly = await IPRO.Web.Infrastructure.ClientPortalActivity.IsReadOnlyAsync(_entitlements, User);
        return View(documents);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Upload(IFormFile file)
    {
        // 598: read-only while the adviser's plan has no client portal.
        if (await IPRO.Web.Infrastructure.ClientPortalActivity.IsReadOnlyAsync(_entitlements, User))
        {
            TempData["Error"] = IPRO.Web.Infrastructure.ClientPortalActivity.ReadOnlyMessage;
            return RedirectToAction(nameof(Index));
        }

        if (file == null || file.Length == 0)
        {
            TempData["Error"] = "Choose a file to upload.";
            return RedirectToAction(nameof(Index));
        }
        if (file.Length > MaxFileSizeBytes)
        {
            TempData["Error"] = "That file is larger than the 20 MB upload limit.";
            return RedirectToAction(nameof(Index));
        }

        // 598: a client's upload counts against the adviser's storage like the adviser's own (the total
        // already counted portal files; only this path never checked it). The message speaks to the client.
        var adviserId = IPRO.Web.Infrastructure.ClientPortalActivity.AdviserId(User);
        if (adviserId is null) return Forbid();
        var capacity = await _entitlements.GetAccessAsync(adviserId.Value, PackageFeatureCodes.FileUploadCapacity);
        var limitBytes = IPRO.Web.Infrastructure.AgentStorageUsage.LimitBytes(capacity.LimitValue);
        var usedBytes = await IPRO.Web.Infrastructure.AgentStorageUsage.TotalBytesAsync(_db, adviserId.Value);
        if (limitBytes > 0 && usedBytes + file.Length > limitBytes)
        {
            TempData["Error"] = "Your advisor's document storage is full, so this file can't be uploaded right now. Please send it to your advisor another way, or let them know so they can make room.";
            return RedirectToAction(nameof(Index));
        }

        await using var stream = file.OpenReadStream();
        var validation = await PortalDocumentValidator.ValidateAsync(file.FileName, stream);
        if (!validation.IsValid)
        {
            TempData["Error"] = validation.Error;
            return RedirectToAction(nameof(Index));
        }
        stream.Position = 0;
        var url = await _blob.UploadAsync(stream, file.FileName, "portal-documents", validation.ContentType, isPrivate: true);

        _db.PortalDocuments.Add(new PortalDocument
        {
            ClientId = ClientId,
            UploadedByClient = true,
            FileName = Path.GetFileName(file.FileName),
            BlobUrl = url,
            ContentType = validation.ContentType,
            FileSizeBytes = file.Length
        });
        await _db.SaveChangesAsync();
        await IPRO.Web.Infrastructure.ClientPortalActivity.NotifyAdviserAsync(_db, _email, _configuration, ClientId, IPRO.Web.Infrastructure.ClientPortalActivity.Kind.Document, _logger);

        TempData["Success"] = "Document uploaded.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Download(int id)
    {
        var document = await _db.PortalDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id && d.ClientId == ClientId);
        if (document == null) return NotFound();

        var stream = await _blob.DownloadAsync(document.BlobUrl);
        if (stream == null) return NotFound();

        return File(stream, string.IsNullOrWhiteSpace(document.ContentType) ? "application/octet-stream" : document.ContentType, document.FileName);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        // 598: read-only while the adviser's plan has no client portal.
        if (await IPRO.Web.Infrastructure.ClientPortalActivity.IsReadOnlyAsync(_entitlements, User))
        {
            TempData["Error"] = IPRO.Web.Infrastructure.ClientPortalActivity.ReadOnlyMessage;
            return RedirectToAction(nameof(Index));
        }

        var document = await _db.PortalDocuments.FirstOrDefaultAsync(d => d.Id == id && d.ClientId == ClientId && d.UploadedByClient);
        if (document == null) return NotFound();

        await _blob.DeleteAsync(document.BlobUrl);
        _db.PortalDocuments.Remove(document);
        await _db.SaveChangesAsync();

        TempData["Success"] = "Document deleted.";
        return RedirectToAction(nameof(Index));
    }
}
