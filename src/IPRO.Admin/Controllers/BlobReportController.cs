using IPRO.DataAccess;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Admin.Controllers;

// The A5-H11 orphan sweep, as a REPORT and nothing else. The original design — a sweep that
// deletes unreferenced blobs — was rejected: "referenced by no database row" does not prove
// "referenced by no delivered email", so the sweep would have blanked images in mail already
// sitting in inboxes. This page shows a human what looks unreferenced; deciding what (if
// anything) to do about a file is deliberately manual. There is no delete action here and none
// may be added on top of this enumeration.
[Authorize(Policy = "SuperAdmin")]
public class BlobReportController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IBlobStorageService _blob;

    public BlobReportController(IPRODbContext db, IBlobStorageService blob)
    {
        _db = db;
        _blob = blob;
    }

    public sealed record ContainerReport(
        string Container, string Holds, int TotalBlobs, int Referenced, List<string> Unreferenced, string? Error);

    // Walks every container the app owns (BlobReferences.Containers is the registry) and checks
    // each blob against every URL-bearing column and stored HTML body. Synchronous full scan on
    // purpose: this account holds ~17 MB, and a report that might be stale would defeat the point.
    public async Task<IActionResult> Index()
    {
        var reports = new List<ContainerReport>();
        foreach (var (container, holds) in BlobReferences.Containers)
        {
            try
            {
                var urls = await _blob.ListAsync(container);
                var unreferenced = new List<string>();
                foreach (var url in urls)
                {
                    if (!await BlobReferences.IsReferencedAsync(_db, url)) unreferenced.Add(url);
                }
                reports.Add(new ContainerReport(container, holds, urls.Count, urls.Count - unreferenced.Count, unreferenced, null));
            }
            catch (Exception ex)
            {
                reports.Add(new ContainerReport(container, holds, 0, 0, new List<string>(), ex.Message));
            }
        }

        return View(reports);
    }
}
