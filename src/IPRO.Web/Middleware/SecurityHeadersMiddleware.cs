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

        // Content Security Policy
        headers["Content-Security-Policy"] =
            "default-src 'self'; " +
            "script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com https://cdn.tiny.cloud https://cdn.jsdelivr.net/npm/chart.js@4.4.0; " +
            "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com; " +
            "img-src 'self' data: blob: https://*.blob.core.windows.net https://cdn.tiny.cloud; " +
            "font-src 'self' https://cdnjs.cloudflare.com; " +
            "connect-src 'self'; " +
            "frame-src 'self' https://www.google.com; " +
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
}
