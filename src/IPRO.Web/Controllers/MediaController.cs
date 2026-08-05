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
        // to the private agent-documents container and walks straight past the allowlist above.
        //
        // The first version of this guard tested the RAW route value, which was not enough either.
        // ASP.NET decodes the request path exactly once, so "%252e%252e" arrived here as the literal
        // text "%2e%2e" -- no ".." to find -- and ParseBlobUrl's own decode turned it back into ".."
        // afterwards. Confirmed exploitable in production on 2026-08-05: a request under agent-logos
        // returned a real blob out of agent-photos. Decode to a fixed point FIRST, then test.
        var decodedPath = FullyDecode(blobPath);
        if (HasTraversal(decodedPath))
        {
            _logger.LogWarning("Rejected media path traversal attempt: {Container}/{Blob}", container, blobPath);
            return NotFound();
        }

        var url = _blobs.GetPublicUrl(container, decodedPath);

        // Final check on the FULLY RESOLVED target rather than on the input string. Any encoding
        // trick that survives everything above still has to produce a URL whose container is the one
        // that was authorised -- and this compares exactly that. Checks on input are guesses about
        // what the input will become; this one observes what it actually became.
        if (!ResolvesToContainer(url, container))
        {
            _logger.LogWarning("Rejected media request that resolved outside {Container}: {Blob}", container, blobPath);
            return NotFound();
        }

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

    // Decode until the string stops changing, so no amount of nesting ("%25252e") hides a dot
    // segment from the check below. Capped to keep a malicious input from spinning.
    private static string FullyDecode(string value)
    {
        for (var i = 0; i < 8; i++)
        {
            string decoded;
            try { decoded = Uri.UnescapeDataString(value); }
            catch (UriFormatException) { return value; }
            if (string.Equals(decoded, value, StringComparison.Ordinal)) return value;
            value = decoded;
        }
        return value;
    }

    private static bool HasTraversal(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (value.StartsWith('/') || value.StartsWith('\\')) return true;
        if (value.Contains('\\', StringComparison.Ordinal)) return true;
        // Segment equality, not Contains(".."): a file legitimately named "report..final.png" is
        // not a traversal, while a bare ".." segment always is.
        return value.Split('/').Any(segment => segment == ".." || segment == ".");
    }

    // Rebuilds the (container, blob) split the storage layer will perform and confirms the container
    // is unchanged. Mirrors BlobStorageService.ParseBlobUrl deliberately -- if that ever diverges,
    // this fails closed (mismatch -> 404) rather than open.
    private static bool ResolvesToContainer(string url, string expectedContainer)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var parts = uri.AbsolutePath.TrimStart('/').Split('/', 2);
        if (parts.Length != 2) return false;
        var resolved = Uri.UnescapeDataString(parts[0]);
        return string.Equals(resolved, expectedContainer, StringComparison.OrdinalIgnoreCase)
               && NewsletterMediaProxy.IsProxyableContainer(resolved);
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
