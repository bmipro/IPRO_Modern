using System.Security.Claims;
using IPRO.Admin.Infrastructure;
using IPRO.Business.Interfaces;
using IPRO.Scheduler;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IPRO.Admin.Controllers;

// 474 (2026-09-11): the nightly database dumps (DatabaseDumpJob) listed, and a way to take one now.
// The files are private; download them through the Azure portal or the CLI, never from here.
[Authorize(Policy = "SuperAdmin")]
public class BackupsController : Controller
{
    private readonly IBlobStorageService _blob;
    private readonly IServiceProvider _services;
    private readonly IAdminAuditLogService _auditLog;
    private readonly IConfiguration _configuration;
    private int CurrentAdminId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    private string CurrentAdminUsername => User.Identity?.Name ?? "unknown";

    public BackupsController(IBlobStorageService blob, IServiceProvider services, IAdminAuditLogService auditLog, IConfiguration configuration)
    {
        _blob = blob; _services = services; _auditLog = auditLog; _configuration = configuration;
    }

    public sealed record BackupFileRow(string Name, DateTime? TakenAtUtc);

    public async Task<IActionResult> Index()
    {
        List<string> urls;
        try { urls = await _blob.ListAsync(DatabaseDumpJob.ContainerName); }
        catch (Exception ex)
        {
            urls = new List<string>();
            ViewBag.ListError = ex.Message;
        }
        var rows = urls
            .Select(url => new BackupFileRow(url.Split('/').Last(), DatabaseDumpJob.TryParseTimestamp(url, out var t) ? t : null))
            .OrderByDescending(r => r.TakenAtUtc ?? DateTime.MinValue)
            .ToList();
        ViewBag.Zone = AdminClock.Zone(_configuration);
        ViewBag.Container = DatabaseDumpJob.ContainerName;
        ViewBag.RetentionDays = DatabaseDumpJob.RetentionDays;
        return View(rows);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RunNow()
    {
        try
        {
            var job = ActivatorUtilities.CreateInstance<DatabaseDumpJob>(_services);
            var url = await job.RunAndReportAsync();
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "DatabaseDumpRun", $"Database dump taken on demand: {url.Split('/').Last()}");
            TempData["Success"] = "Dump taken and uploaded.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"The dump failed: {ex.Message}";
        }
        return RedirectToAction(nameof(Index));
    }
}
