using System;
using System.Linq;

namespace IPRO.Utility;

// Newsletter images were served straight from *.blob.core.windows.net, which SpamAssassin penalises
// (URI_IMG_CWINDOWSNET, -1.0) because that host is free and anonymously signup-able, so spammers use it
// heavily. Serving the same bytes from the app's own domain removes the penalty and puts images on the
// brand's domain.
//
// SAFETY: only containers that are ALREADY anonymously readable may be proxied. Every entry below is
// uploaded with isPrivate:false, so proxying exposes nothing that a direct blob URL didn't already.
// agent-documents and portal-documents are uploaded isPrivate:true and are deliberately absent -- adding
// either would turn this into an open proxy that hands out other agents' private files, authenticated by
// nothing. Do not add a container here without checking the isPrivate flag at its upload site.
public static class NewsletterMediaProxy
{
    public const string RoutePrefix = "media";

    private static readonly string[] PublicContainers =
    {
        "agent-logos", "website-media", "website-gallery", "article-media", "agent-photos"
    };

    public static bool IsProxyableContainer(string? container) =>
        !string.IsNullOrWhiteSpace(container) &&
        PublicContainers.Contains(container, StringComparer.OrdinalIgnoreCase);

    /// Rewrites a blob URL to an app-hosted one. Anything that is not a proxyable blob URL -- a relative
    /// path, an external image, a private container -- is returned unchanged so callers can pass
    /// everything through this without special-casing.
    public static string Rewrite(string url, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(baseUrl)) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (!uri.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase)) return url;

        // AbsolutePath is percent-encoded; keep it that way. It is going straight back into a URL, and
        // decoding here would produce spaces in an href. (The blob SDK needs the decoded form -- that is
        // BlobStorageService's job, not this one.)
        var parts = uri.AbsolutePath.TrimStart('/').Split('/', 2);
        if (parts.Length != 2 || !IsProxyableContainer(parts[0])) return url;

        return $"{baseUrl.TrimEnd('/')}/{RoutePrefix}/{parts[0]}/{parts[1]}";
    }
}
