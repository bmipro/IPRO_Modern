using System.Security.Claims;
using IPRO.DataAccess;
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
    private int ClientId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public ClientPortalDocumentsController(IPRODbContext db, IBlobStorageService blob)
    {
        _db = db;
        _blob = blob;
    }

    public async Task<IActionResult> Index()
    {
        var documents = await _db.PortalDocuments
            .AsNoTracking()
            .Where(d => d.ClientId == ClientId)
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync();
        return View(documents);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Upload(IFormFile file)
    {
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
}
