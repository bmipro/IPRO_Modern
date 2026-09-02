using System;

namespace IPRO.Utility;

public sealed record StatusPageText(string Title, string Message);
public sealed record StatusPageBackLink(string Href, string Label);

public sealed class StatusPageModel
{
    public int Code { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string BackHref { get; init; } = "/";
    public string BackLabel { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
}

// 456 (2026-09-02): the one set of decisions behind the branded status pages, shared by IPRO.Web and
// IPRO.Admin. Everything takes plain strings so it is testable without ASP.NET types.
//
// Who gets a page: a request that explicitly accepts HTML (a browser) on a path that is not a
// machine endpoint. Event Grid, SendGrid and PayPal post to webhooks and read the status code; a
// health probe reads the body; Hangfire has its own UI; and /error itself must never re-enter.
// Everything else keeps the bare status it always had.
public static class StatusPagePolicy
{
    private static readonly string[] MachinePrefixes =
    {
        "/health",
        "/AzureEmailEvents",
        "/Newsletter/SendGridEvents",
        "/billing/webhook",
        "/hangfire",
        "/error",
    };

    public static bool ShouldRender(string? path, string? accept)
    {
        if (string.IsNullOrWhiteSpace(accept) || accept.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        var p = path ?? string.Empty;
        foreach (var prefix in MachinePrefixes)
        {
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    // Where the visitor was decides where "back" goes. The client-facing prefixes are the same
    // never-shadowed set Program.cs routes on; the agent portal lives under /portal; everything
    // else is the public site or a sign-in page.
    public static StatusPageBackLink BackLink(string? originalPath)
    {
        var p = (originalPath ?? string.Empty).TrimStart('/');
        if (p.StartsWith("clientportal", StringComparison.OrdinalIgnoreCase))
            return new("/ClientPortal", "Back to the client portal");
        if (p.Equals("portal", StringComparison.OrdinalIgnoreCase) || p.StartsWith("portal/", StringComparison.OrdinalIgnoreCase))
            return new("/portal", "Back to your portal");
        return new("/", "Back to the home page");
    }

    public static StatusPageText Describe(int code) => code switch
    {
        400 => new("That request could not be processed",
                   "The page sent something the server could not accept. Go back and try again; if it keeps happening, let us know."),
        401 => new("Please sign in",
                   "That page needs you to be signed in."),
        403 => new("You do not have access to that page",
                   "Your account is not allowed to open it. If you think it should be, contact the person who manages your account."),
        404 => new("Page not found",
                   "The address may be mistyped, or the page may have moved or been removed."),
        405 => new("That action is not available this way",
                   "The page was reached in a way it does not support. Go back and use the buttons on the page."),
        408 => new("The request took too long",
                   "Please try again."),
        429 => new("Too many requests",
                   "Please wait a moment and try again."),
        500 => new("Something went wrong",
                   "We could not complete that request. Please try again, or contact support if the problem continues."),
        502 or 503 or 504 => new("The service is briefly unavailable",
                   "We are restarting or updating. Please try again in a minute."),
        _ => new($"Something went wrong (error {code})",
                 $"The server answered with status {code}. Please try again, or contact support if the problem continues."),
    };
}
