using System.Security.Cryptography;

namespace IPRO.Admin.Middleware;

/// <summary>
/// Same protections as IPRO.Web's SecurityHeadersMiddleware, scoped to what this app actually
/// loads (no rich-text-editor CDN, no Google Maps frame-src, no blob-storage images).
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        // One random nonce per request -- see IPRO.Web's SecurityHeadersMiddleware for the reasoning.
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        context.Items["CspNonce"] = nonce;

        headers["X-Frame-Options"] = "SAMEORIGIN";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-XSS-Protection"] = "1; mode=block";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";

        if (!(context.Request.Path.HasValue && System.IO.Path.HasExtension(context.Request.Path.Value)))
        {
            headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            headers["Pragma"] = "no-cache";
        }

        headers["Content-Security-Policy"] =
            "default-src 'self'; " +
            $"script-src 'self' 'nonce-{nonce}' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com; " +
            "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com; " +
            "img-src 'self' data:; " +
            "font-src 'self' https://cdnjs.cloudflare.com; " +
            "connect-src 'self'; " +
            "frame-src 'none'; " +
            "frame-ancestors 'self';";

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
