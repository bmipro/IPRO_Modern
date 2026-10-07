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

    // 554: set while a HEAD is being answered as a GET, so the page it reaches knows no one is looking
    // (a customer site's page does not count it as a visit).
    public const string AnsweringItem = "IPRO.AnsweringHead";

    public static bool IsBeingAnswered(HttpContext context) => context.Items.ContainsKey(AnsweringItem);

    // The pipeline step (Program.cs, before routing). The request runs as a GET so an [HttpGet] action
    // matches; the page is written to nowhere (Kestrel never sends a body for HEAD anyway: this spares
    // the writing); and the method is put back so the request is logged as what it was.
    //
    // 554: alsoAnswered is the second half of the allow-list -- the pages of a CUSTOMER'S site. The
    // list above is the platform's own addresses; a customer's /gallery or /contact answered HEAD
    // with 404 (only a GET is handed to the public site, so nothing matched at all), and a link
    // checker or uptime monitor reported every page but the home page as broken. The pipeline passes
    // the same test it uses to hand a GET to the public site, which already leaves out everything
    // that DOES something: /t, /poll, /email-preferences, /account, /portal.
    public static async Task AnswerAsync(HttpContext context, Func<Task> next, Func<HttpContext, bool>? alsoAnswered = null)
    {
        if (!HttpMethods.IsHead(context.Request.Method) ||
            !(IsAnswered(context.Request.Path) || alsoAnswered?.Invoke(context) == true))
        {
            await next();
            return;
        }

        var body = context.Response.Body;
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = Stream.Null;
        context.Items[AnsweringItem] = true;
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
