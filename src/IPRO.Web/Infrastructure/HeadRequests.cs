using Microsoft.AspNetCore.Http;

namespace IPRO.Web.Infrastructure;

// 509 (2026-09-21): an MVC action marked [HttpGet] answers HEAD with 405, so uptime monitors, link
// checkers and validators saw the public pages -- and /health/version -- as broken while browsers
// were fine. The pipeline now answers HEAD like GET without the body (Program.cs), but ONLY for the
// addresses listed here. An allow-list on purpose: this app has GET addresses that DO something --
// an email's open and click tracking, a one-click poll vote, unsubscribe, sign-out -- and mail
// scanners probe links with HEAD. Those keep answering 405, as they always have.
public static class HeadRequests
{
    private static readonly HashSet<string> Answered = new(StringComparer.OrdinalIgnoreCase)
    {
        "/", "/accountants", "/mortgage", "/mortgages", "/terms", "/privacy", "/Preview",
        "/Account/Register", "/Account/Login",
        "/health", "/health/version",
        "/robots.txt", "/sitemap.xml"
    };

    public static bool IsAnswered(PathString path) =>
        !path.HasValue || Answered.Contains(path.Value!.Length > 1 ? path.Value!.TrimEnd('/') : path.Value!);

    // The pipeline step (Program.cs, before routing). The request runs as a GET so an [HttpGet] action
    // matches; the page is written to nowhere (Kestrel never sends a body for HEAD anyway: this spares
    // the writing); and the method is put back so the request is logged as what it was.
    public static async Task AnswerAsync(HttpContext context, Func<Task> next)
    {
        if (!HttpMethods.IsHead(context.Request.Method) || !IsAnswered(context.Request.Path))
        {
            await next();
            return;
        }

        var body = context.Response.Body;
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = Stream.Null;
        try
        {
            await next();
        }
        finally
        {
            context.Response.Body = body;
            context.Request.Method = HttpMethods.Head;
        }
    }
}
