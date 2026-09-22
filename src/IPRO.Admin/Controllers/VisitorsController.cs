using IPRO.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Admin.Controllers;

// 512 (2026-09-22): visits to the platform's own public pages and where they came from -- the
// report the owner asked for on launch day. Its own controller (the address stays under /Reports)
// so ReportsController's constructor, which tests pin, is untouched.
[Authorize(Policy = "AdminAccess")]
public class VisitorsController : Controller
{
    private readonly IPRODbContext _db;

    public VisitorsController(IPRODbContext db) => _db = db;

    [HttpGet("/Reports/Visitors")]
    public async Task<IActionResult> Index(int days = 30)
    {
        days = days is 7 or 30 or 90 ? days : 30;
        return View(await PlatformVisits.ReportAsync(_db, days, DateTime.UtcNow));
    }
}
