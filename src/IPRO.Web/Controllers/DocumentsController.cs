using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class DocumentsController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IBlobStorageService _blob;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public DocumentsController(IPRODbContext db, IPackageEntitlementService entitlements, IBlobStorageService blob)
    {
        _db = db;
        _entitlements = entitlements;
        _blob = blob;
    }

    public async Task<IActionResult> Index(string? search, string? category)
    {
        var query = _db.AgentDocuments.Where(d => d.AgentUserId == AgentId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            search = search.Trim();
            query = query.Where(d => d.FileName.Contains(search));
        }
        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(d => d.Category == category);
        }

        var documents = await query.OrderByDescending(d => d.UploadedAt).ToListAsync();
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.FileUploadCapacity);
        var usedBytes = await _db.AgentDocuments.Where(d => d.AgentUserId == AgentId).SumAsync(d => (long?)d.FileSizeBytes) ?? 0;

        ViewBag.Search = search;
        ViewBag.Category = category;
        ViewBag.Categories = await _db.AgentDocuments.Where(d => d.AgentUserId == AgentId && d.Category != "").Select(d => d.Category).Distinct().OrderBy(c => c).ToListAsync();
        ViewBag.UsageAccess = access;
        ViewBag.UsedMb = Math.Round(usedBytes / 1024.0 / 1024.0, 1);

        return View(documents);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Upload(IFormFile file, string? category)
    {
        category = category?.Trim() ?? "";

        if (file == null || file.Length == 0)
        {
            TempData["Error"] = "Choose a file to upload.";
            return RedirectToAction(nameof(Index));
        }
        if (file.Length > 20 * 1024 * 1024)
        {
            TempData["Error"] = "That file is larger than the 20 MB per-file upload limit.";
            return RedirectToAction(nameof(Index));
        }

        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.FileUploadCapacity);
        var limitBytes = (long)(access.LimitValue ?? 0) * 1024 * 1024;
        var usedBytes = await _db.AgentDocuments.Where(d => d.AgentUserId == AgentId).SumAsync(d => (long?)d.FileSizeBytes) ?? 0;
        if (limitBytes > 0 && usedBytes + file.Length > limitBytes)
        {
            var usedMb = Math.Round(usedBytes / 1024.0 / 1024.0, 1);
            var limitMb = access.LimitValue ?? 0;
            TempData["Error"] = $"That upload would exceed your storage limit ({usedMb} MB of {limitMb} MB used). Delete unused documents to free up space, or contact us to increase your storage.";
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
        var url = await _blob.UploadAsync(stream, file.FileName, "agent-documents", validation.ContentType, isPrivate: true);

        _db.AgentDocuments.Add(new AgentDocument
        {
            AgentUserId = AgentId,
            FileName = Path.GetFileName(file.FileName),
            BlobUrl = url,
            ContentType = validation.ContentType,
            FileSizeBytes = file.Length,
            Category = category
        });
        await _db.SaveChangesAsync();

        TempData["Success"] = "Document uploaded.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Download(int id)
    {
        var document = await _db.AgentDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id && d.AgentUserId == AgentId);
        if (document == null) return NotFound();

        var stream = await _blob.DownloadAsync(document.BlobUrl);
        if (stream == null) return NotFound();

        return File(stream, string.IsNullOrWhiteSpace(document.ContentType) ? "application/octet-stream" : document.ContentType, document.FileName);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var document = await _db.AgentDocuments.FirstOrDefaultAsync(d => d.Id == id && d.AgentUserId == AgentId);
        if (document == null) return NotFound();

        await _blob.DeleteAsync(document.BlobUrl);
        _db.AgentDocuments.Remove(document);
        await _db.SaveChangesAsync();

        TempData["Success"] = "Document deleted.";
        return RedirectToAction(nameof(Index));
    }
}
