using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Web.Controllers;

// Serves newsletter images from the app's own domain instead of *.blob.core.windows.net, which costs a
// full SpamAssassin point (URI_IMG_CWINDOWSNET). See NewsletterMediaProxy for why, and for the container
// allowlist this endpoint is bound by.
//
// Anonymous by necessity: mail clients fetch these while rendering an email and carry no session. That is
// only acceptable because every container reachable here is already public -- the allowlist is the whole
// security boundary, so it lives in one place and is checked before any blob call is made.
[AllowAnonymous]
[Route(NewsletterMediaProxy.RoutePrefix)]
public class MediaController : Controller
{
    private readonly IBlobStorageService _blobs;
    private readonly ILogger<MediaController> _logger;

    public MediaController(IBlobStorageService blobs, ILogger<MediaController> logger)
    { _blobs = blobs; _logger = logger; }

    [HttpGet("{container}/{*blobPath}")]
    [ResponseCache(Duration = 31536000, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> Get(string container, string blobPath)
    {
        // 404 rather than 403 for a disallowed container: this endpoint should not confirm whether a
        // private container exists.
        if (!NewsletterMediaProxy.IsProxyableContainer(container) || string.IsNullOrWhiteSpace(blobPath))
        {
            return NotFound();
        }

        // Checking the container alone is NOT enough. GetPublicUrl concatenates these into a URL string,
        // and the blob SDK later normalises it -- so "agent-logos" + "../agent-documents/x.pdf" resolves
        // to the private agent-documents container and walks straight past the allowlist above. Reject
        // any traversal or absolute path in the second segment before it can be concatenated.
        if (blobPath.Contains("..", StringComparison.Ordinal)
            || blobPath.StartsWith('/') || blobPath.StartsWith('\\')
            || blobPath.Contains('\\', StringComparison.Ordinal))
        {
            _logger.LogWarning("Rejected media path traversal attempt: {Container}/{Blob}", container, blobPath);
            return NotFound();
        }

        var url = _blobs.GetPublicUrl(container, blobPath);

        Stream? stream;
        try
        {
            stream = await _blobs.DownloadAsync(url);
        }
        catch (Exception ex)
        {
            // A missing image must not 500 an email render; the client just shows a broken image.
            _logger.LogWarning(ex, "Newsletter media {Container}/{Blob} could not be served", container, blobPath);
            return NotFound();
        }

        if (stream == null) return NotFound();

        // Blob names are GUID-prefixed and never rewritten in place, so a given URL's bytes are immutable
        // and can be cached hard. This is what keeps repeated opens off the app.
        Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
        return File(stream, ContentTypeFor(blobPath));
    }

    private static string ContentTypeFor(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            _ => "image/jpeg"
        };
    }
}
