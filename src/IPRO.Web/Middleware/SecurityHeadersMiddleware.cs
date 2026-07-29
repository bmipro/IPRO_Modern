using System.Security.Cryptography;

namespace IPRO.Web.Middleware;

/// <summary>
/// Adds security headers to every response — replaces nothing from the old app (it had none).
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

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
            "img-src 'self' data: blob: https://*.blob.core.windows.net https://cdn.tiny.cloud; " +
            "font-src 'self' https://cdnjs.cloudflare.com; " +
            "connect-src 'self'; " +
            "frame-src 'self' https://www.google.com https://www.youtube-nocookie.com; " +
            "frame-ancestors 'self';";

        // HSTS — force HTTPS for 1 year
        headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";

        await _next(context);
    }
}

public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityHeadersMiddleware>();

    /// <summary>The per-request CSP nonce set by SecurityHeadersMiddleware -- add nonce="@Context.GetCspNonce()" to every inline &lt;script&gt; block.</summary>
    public static string GetCspNonce(this HttpContext context) =>
        context.Items["CspNonce"] as string ?? string.Empty;
}
