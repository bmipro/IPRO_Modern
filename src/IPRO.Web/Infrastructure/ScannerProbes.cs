using Microsoft.AspNetCore.Http;

namespace IPRO.Web.Infrastructure;

// 649 (found 2026-11-08, tracing the slowdown of 2026-11-04 at 6:03 PM Eastern). A scanner sent about 950
// requests in a minute to one customer site, asking for WordPress files and server secrets
// (.git/config, wp-login.php, proc/self/environ). Every one was "not found" -- iPro is not WordPress --
// but an address with no file ending is a page address on a customer's domain, and each "not found"
// page is the site's own: its header, menu and footer, read from the database. 470 of them at once
// filled the database's connections and slowed every page for five minutes. On ordinary days such
// probes are 500 to 3,000 a day across the customer sites (Application Insights, 30 days).
//
// These addresses are answered "Not found." here, before the URL-space rule and before any database
// work, on every host. Only shapes no iPro site, portal page or customer's previous website can have:
//   - a hidden file or folder: a part of the address that starts with a dot (.git, .env, .aws, .ssh,
//     .DS_Store, .htaccess), except .well-known, which browsers and certificate checks ask for;
//   - WordPress's own folders and files: a part that starts with wp- or wp_ (wp-admin, wp-content,
//     wp-includes, wp-json, wp-login.php, wp-config.php, wp_filemanager.php), and xmlrpc.php;
//   - cgi-bin and phpmyadmin, and addresses that start /proc/self/, /etc/passwd or /vendor/phpunit/.
// Words that could be a real page (terms, privacy, login, admin, wordpress, wp) and old-style page
// addresses (/services.php, which 553's OldAddresses may send on) are not touched: they go on as before.
// Static files have already had their turn, and the per-address limit still counts every probe.
public static class ScannerProbes
{
    public const string Answer = "Not found.";

    private static readonly string[] Starts = { "/proc/self/", "/etc/passwd", "/vendor/phpunit/" };

    public static bool IsProbe(PathString path)
    {
        if (!path.HasValue) return false;
        var value = path.Value!;
        foreach (var part in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length > 1 && part[0] == '.' && !part.Equals(".well-known", StringComparison.OrdinalIgnoreCase)) return true;
            if (part.StartsWith("wp-", StringComparison.OrdinalIgnoreCase) || part.StartsWith("wp_", StringComparison.OrdinalIgnoreCase)) return true;
            if (part.Equals("xmlrpc.php", StringComparison.OrdinalIgnoreCase)
                || part.Equals("cgi-bin", StringComparison.OrdinalIgnoreCase)
                || part.Equals("phpmyadmin", StringComparison.OrdinalIgnoreCase)) return true;
        }
        foreach (var start in Starts)
            if (value.StartsWith(start, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // A bare 404 with a body of its own, so the branded status page (StatusPagePolicy) does not render
    // one for it either.
    public static Task AnswerAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        return HttpMethods.IsHead(context.Request.Method) ? Task.CompletedTask : context.Response.WriteAsync(Answer);
    }
}
