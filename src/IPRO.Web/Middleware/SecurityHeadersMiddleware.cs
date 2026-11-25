using System.Security.Cryptography;

namespace IPRO.Web.Middleware;

/// <summary>
/// Adds security headers to every response — replaces nothing from the old app (it had none).
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    // 585: in Development, the local storage emulator's address (IPRO.Utility.LocalStorageImages); "" elsewhere.
    private readonly string _localImages;
    private readonly IConfiguration _configuration;

    public SecurityHeadersMiddleware(RequestDelegate next, IWebHostEnvironment environment, IConfiguration configuration)
    {
        _next = next;
        _localImages = IPRO.Utility.LocalStorageImages.Source(environment.IsDevelopment(), configuration);
        _configuration = configuration;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        // One random nonce per request, stored where views can read it (CspNonceExtensions) and
        // interpolated into script-src below -- this is what lets inline <script> blocks that need
        // server-rendered data keep running without falling back to 'unsafe-inline' for everything.
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        context.Items["CspNonce"] = nonce;

        // Prevent clickjacking
        headers["X-Frame-Options"] = "SAMEORIGIN";

        // Prevent MIME sniffing
        headers["X-Content-Type-Options"] = "nosniff";

        // XSS protection (legacy browsers)
        headers["X-XSS-Protection"] = "1; mode=block";

        // Referrer policy
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

        // Permissions policy — disable unused browser APIs
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(self), payment=(self)";

        // Every page here is server-rendered per request (agent portal, admin, public agent sites).
        // Without this, browsers can serve a stale snapshot from disk cache or back/forward cache
        // after data changes server-side (e.g. the client list looking unchanged right after adding
        // one) instead of hitting the server again. Static assets (css/js/images) are excluded so
        // they keep their own caching.
        if (!(context.Request.Path.HasValue && System.IO.Path.HasExtension(context.Request.Path.Value)))
        {
            headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            headers["Pragma"] = "no-cache";
        }

        // Content Security Policy
        headers["Content-Security-Policy"] =
            "default-src 'self'; " +
            $"script-src 'self' 'nonce-{nonce}' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com https://cdn.tiny.cloud https://cdn.jsdelivr.net/npm/chart.js@4.4.0; " +
            "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com; " +
            "img-src 'self' data: blob: https://*.blob.core.windows.net https://cdn.tiny.cloud" + _localImages + "; " +
            "font-src 'self' https://cdnjs.cloudflare.com; " +
            "connect-src 'self'; " +
            "frame-src 'self' https://www.google.com https://www.youtube-nocookie.com; " +
            "frame-ancestors 'self';";

        // HSTS — force HTTPS for 1 year
        // 678 (audit 2026-11-24 E-4): includeSubDomains only on names whose every subdomain is iPro's (the platform's
        // own address and the free *.247advisers.com addresses). On an adviser's own domain, or iPro's brand domains
        // (whose mail.* names live on the old host), it made a browser refuse plain HTTP for every name under them for
        // a year: an adviser's webmail or router page, say. Those names get HTTPS pinned on themselves only.
        headers["Strict-Transport-Security"] = SecurityHeadersMiddlewareExtensions.HstsFor(context.Request.Host.Host, _configuration);

        await _next(context);
    }
}

public static class SecurityHeadersMiddlewareExtensions
{
    // 632 D-2 (2026-11-24), with the audit's E-4: a redirect that iPro sends from a name only to point people
    // elsewhere -- an adviser's short address (example.com -> www.example.com), an old brand name -- pins
    // HTTPS on that one name and not on the names under it. "includeSubDomains" there would make browsers
    // refuse plain HTTP for a year on every other name of that domain (an adviser's webmail, the old host's
    // mail.* names), which iPro does not serve. The other headers stay as set.
    public const string HstsThisNameOnly = "max-age=31536000";
    public const string HstsWithSubdomains = "max-age=31536000; includeSubDomains";

    // 678 (audit E-4): which of the two a host gets.
    public static string HstsFor(string? host, IConfiguration configuration)
    {
        var name = (host ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
        if (name.Length == 0) return HstsThisNameOnly;
        var canonical = new Uri(IPRO.Utility.WebAppUrlHelper.GetWebAppBaseUrl(configuration)).Host.Trim('.').ToLowerInvariant();
        var temporaryRoot = (configuration["App:TemporarySiteRootDomain"] ?? "247advisers.com").Trim().Trim('.').ToLowerInvariant();
        return name == canonical
               || name.EndsWith("." + temporaryRoot, StringComparison.Ordinal)
               || name.EndsWith(".azurewebsites.net", StringComparison.Ordinal)
            ? HstsWithSubdomains
            : HstsThisNameOnly;
    }

    public static void PinThisNameOnly(HttpContext context) =>
        context.Response.Headers["Strict-Transport-Security"] = HstsThisNameOnly;

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityHeadersMiddleware>();

    /// <summary>The per-request CSP nonce set by SecurityHeadersMiddleware -- add nonce="@Context.GetCspNonce()" to every inline &lt;script&gt; block.</summary>
    public static string GetCspNonce(this HttpContext context) =>
        context.Items["CspNonce"] as string ?? string.Empty;
}
